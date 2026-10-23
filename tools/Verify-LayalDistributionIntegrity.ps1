[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DistributionDirectory,
    [switch]$RequireSignature
)
$ErrorActionPreference = 'Stop'
$distribution = (Resolve-Path -LiteralPath $DistributionDirectory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$statusFile = Get-ChildItem -LiteralPath $distribution -Filter '*-release-status.txt' -File | Select-Object -First 1
if (-not $statusFile) { throw 'Distribution release-status file is missing.' }
$statusText = Get-Content -LiteralPath $statusFile.FullName -Raw -Encoding utf8
$isSigned = $statusText -match '(?im)^Code signing:\s*SIGNED AND VERIFIED\s*$'
$isUnsigned = $statusText -match '(?im)^Code signing:\s*NOT SIGNED\b'
if (-not $isSigned -and -not $isUnsigned) { throw 'Release status must explicitly say SIGNED AND VERIFIED or NOT SIGNED.' }
if ($RequireSignature -and -not $isSigned) { throw 'This run requires a signed release, but the release-status file identifies it as unsigned.' }

$checksumPath = Join-Path $distribution 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'SHA256SUMS.txt is missing.' }
$rootPrefix = $distribution + [IO.Path]::DirectorySeparatorChar
$manifestEntries = @{}
$checksumLines = @(Get-Content -LiteralPath $checksumPath -Encoding ascii)
if ($checksumLines.Count -eq 0) { throw 'SHA256SUMS.txt is empty.' }
foreach ($line in $checksumLines) {
    if ($line -notmatch '^([0-9a-fA-F]{64})  (.+)$') { throw "Malformed SHA-256 manifest entry: $line" }
    $expected = $Matches[1].ToLowerInvariant()
    $relative = $Matches[2].Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($relative) -or $relative.StartsWith('/') -or ($relative.Split('/') -contains '..')) {
        throw "Unsafe path in SHA-256 manifest: $relative"
    }
    if ($relative -eq 'SHA256SUMS.txt') { throw 'SHA256SUMS.txt must not attempt to hash itself.' }
    if ($manifestEntries.ContainsKey($relative)) { throw "Duplicate SHA-256 manifest entry: $relative" }
    $manifestEntries[$relative] = $expected

    $nativeRelative = $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $target = [IO.Path]::GetFullPath((Join-Path $distribution $nativeRelative))
    if (-not $target.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Checksum entry resolves outside the distribution folder: $relative"
    }
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "SHA-256 manifest references a missing file: $relative" }
    $actual = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "SHA-256 mismatch: $relative" }
}

$actualFiles = Get-ChildItem -LiteralPath $distribution -File -Recurse |
    Where-Object { $_.FullName -ne $checksumPath } |
    ForEach-Object { $_.FullName.Substring($distribution.Length + 1).Replace('\', '/') }
$actualSet = @{}
foreach ($relative in $actualFiles) { $actualSet[$relative] = $true }
foreach ($relative in $manifestEntries.Keys) {
    if (-not $actualSet.ContainsKey($relative)) { throw "SHA-256 manifest contains a file not found during inventory: $relative" }
}
foreach ($relative in $actualSet.Keys) {
    if (-not $manifestEntries.ContainsKey($relative)) { throw "File is not covered by SHA256SUMS.txt: $relative" }
}
Write-Host "PASS: SHA-256 manifest verified for $($manifestEntries.Count) distribution files."

function Find-SignTool {
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $sdkBin = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/bin'
    if (Test-Path -LiteralPath $sdkBin -PathType Container) {
        $candidates = Get-ChildItem -LiteralPath $sdkBin -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64/signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }
        if ($candidates) { return ($candidates | Select-Object -First 1) }
    }
    throw 'SignTool.exe was not found to verify the release signatures.'
}

function Assert-AuthenticodeSignature {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Thumbprint)
    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Authenticode signature is not valid for $Path (status: $($signature.Status))."
    }
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $Thumbprint) {
        throw "Authenticode signer thumbprint mismatch for $Path."
    }
    if (-not $signature.TimeStamperCertificate) { throw "Authenticode timestamp is missing for $Path." }
    $signtool = Find-SignTool
    & $signtool verify /pa /all /v $Path
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed for $Path (exit code $LASTEXITCODE)." }
    Write-Host "PASS: Authenticode and timestamp verified: $([IO.Path]::GetFileName($Path))"
}

$installer = Get-ChildItem -LiteralPath $distribution -Filter 'Layal-Setup-*-win-x64.exe' -File | Select-Object -First 1
$portable = Get-ChildItem -LiteralPath $distribution -Filter 'Layal-*-portable-win-x64.zip' -File | Select-Object -First 1
if (-not $installer -or -not $portable) { throw 'Installer or portable ZIP is missing from the distribution.' }

if ($isSigned) {
    $thumbprintMatch = [regex]::Match($statusText, '(?im)^Signing certificate thumbprint:\s*([0-9A-F]{40})\s*$')
    if (-not $thumbprintMatch.Success) { throw 'Signed release status does not contain a valid certificate thumbprint.' }
    $thumbprint = $thumbprintMatch.Groups[1].Value.ToUpperInvariant()
    if ($statusText -notmatch '(?im)^Authenticode verification:\s*PASS\b') { throw 'Signed release status does not record successful Authenticode verification.' }
    if ($statusText -notmatch '(?im)^RFC 3161 timestamp:\s*PASS\b') { throw 'Signed release status does not record a successful RFC 3161 timestamp.' }
    Assert-AuthenticodeSignature -Path $installer.FullName -Thumbprint $thumbprint

    $temporaryExtraction = Join-Path ([IO.Path]::GetTempPath()) ("layal-integrity-" + [guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $portable.FullName -DestinationPath $temporaryExtraction -Force
        $portableExe = Join-Path $temporaryExtraction 'Layal.exe'
        $portableAssembly = Join-Path $temporaryExtraction 'Layal.dll'
        if (-not (Test-Path -LiteralPath $portableExe -PathType Leaf)) { throw 'Portable ZIP does not contain Layal.exe at its root.' }
        if (-not (Test-Path -LiteralPath $portableAssembly -PathType Leaf)) { throw 'Portable ZIP does not contain Layal.dll at its root.' }
        Assert-AuthenticodeSignature -Path $portableExe -Thumbprint $thumbprint
        Assert-AuthenticodeSignature -Path $portableAssembly -Thumbprint $thumbprint
    }
    finally {
        if (Test-Path -LiteralPath $temporaryExtraction) {
            Remove-Item -LiteralPath $temporaryExtraction -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host 'PASS: signed installer, Layal.exe, and Layal.dll use the configured, trusted certificate.'
}
else {
    if ($RequireSignature) { throw 'Unsigned artifacts are not permitted for this validation run.' }
    $installerSignature = Get-AuthenticodeSignature -FilePath $installer.FullName
    if ($installerSignature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        throw "Release status says unsigned, but $($installer.FullName) reports Authenticode status $($installerSignature.Status)."
    }
    $temporaryExtraction = Join-Path ([IO.Path]::GetTempPath()) ("layal-integrity-" + [guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -LiteralPath $portable.FullName -DestinationPath $temporaryExtraction -Force
        $portableExe = Join-Path $temporaryExtraction 'Layal.exe'
        $portableAssembly = Join-Path $temporaryExtraction 'Layal.dll'
        if (-not (Test-Path -LiteralPath $portableExe -PathType Leaf)) { throw 'Portable ZIP does not contain Layal.exe at its root.' }
        if (-not (Test-Path -LiteralPath $portableAssembly -PathType Leaf)) { throw 'Portable ZIP does not contain Layal.dll at its root.' }
        foreach ($portableBinary in @($portableExe, $portableAssembly)) {
            $signature = Get-AuthenticodeSignature -FilePath $portableBinary
            if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
                throw "Release status says unsigned, but $portableBinary reports Authenticode status $($signature.Status)."
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryExtraction) {
            Remove-Item -LiteralPath $temporaryExtraction -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host 'PASS: package integrity verified; code-signing status is explicitly unsigned.'
}
