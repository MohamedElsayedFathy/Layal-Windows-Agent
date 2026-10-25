[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,
    [switch]$RequireSignature,
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'

$certificateBase64 = $env:LAYAL_SIGNING_CERTIFICATE_BASE64
$certificatePassword = $env:LAYAL_SIGNING_CERTIFICATE_PASSWORD
$expectedThumbprint = $env:LAYAL_SIGNING_CERTIFICATE_THUMBPRINT
$present = @(
    (-not [string]::IsNullOrWhiteSpace($certificateBase64)),
    (-not [string]::IsNullOrWhiteSpace($certificatePassword)),
    (-not [string]::IsNullOrWhiteSpace($expectedThumbprint))
)
$anyConfigured = $present -contains $true
$allConfigured = $present -notcontains $false

if (-not $anyConfigured) {
    if ($RequireSignature) {
        throw 'Code signing is required, but the LAYAL_SIGNING_CERTIFICATE_* secrets are not all configured.'
    }
    Write-Host "Code signing skipped for $FilePath (certificate secrets are not configured)."
    return
}
if (-not $allConfigured) {
    throw 'Partial code-signing configuration detected. Configure all three LAYAL_SIGNING_CERTIFICATE_* secrets or configure none.'
}

$resolvedFile = (Resolve-Path -LiteralPath $FilePath).Path
$temporaryPfx = Join-Path ([System.IO.Path]::GetTempPath()) ("layal-signing-" + [guid]::NewGuid().ToString('N') + '.pfx')
$certificate = $null

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
    throw 'SignTool.exe was not found. Install a Windows SDK containing the x64 SignTool.'
}

try {
    $compactBase64 = $certificateBase64 -replace '\s', ''
    $pfxBytes = [Convert]::FromBase64String($compactBase64)
    [IO.File]::WriteAllBytes($temporaryPfx, $pfxBytes)
    [Array]::Clear($pfxBytes, 0, $pfxBytes.Length)

    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $temporaryPfx,
        $certificatePassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
    )
    if (-not $certificate.HasPrivateKey) { throw 'The configured PFX does not contain a private signing key.' }

    $configuredThumbprint = ($expectedThumbprint -replace '\s', '').ToUpperInvariant()
    $actualThumbprint = $certificate.Thumbprint.ToUpperInvariant()
    if ($actualThumbprint -ne $configuredThumbprint) {
        throw "Signing certificate thumbprint mismatch. Expected $configuredThumbprint; PFX contains $actualThumbprint."
    }

    $now = [DateTime]::UtcNow
    if ($now -lt $certificate.NotBefore.ToUniversalTime() -or $now -gt $certificate.NotAfter.ToUniversalTime()) {
        throw 'The code-signing certificate is outside its validity period.'
    }

    $ekuExtensions = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
    if ($ekuExtensions.Count -eq 0) {
        throw 'The certificate has no Enhanced Key Usage extension; refusing to sign without an explicit Code Signing EKU.'
    }
    $ekuOids = @($ekuExtensions[0].EnhancedKeyUsages | ForEach-Object { $_.Value })
    if ($ekuOids -notcontains '1.3.6.1.5.5.7.3.3') {
        throw 'The configured certificate does not declare the Code Signing EKU (1.3.6.1.5.5.7.3.3).'
    }

    $signTool = Find-SignTool
    Write-Host "Signing file with configured certificate $actualThumbprint and RFC 3161 SHA-256 timestamp."
    # Do not echo the command line: it contains the PFX password.
    & $signTool sign /fd SHA256 /f $temporaryPfx /p $certificatePassword /tr $TimestampUrl /td SHA256 /d 'Layal Windows Agent' /v $resolvedFile
    if ($LASTEXITCODE -ne 0) { throw "SignTool failed to sign $resolvedFile (exit code $LASTEXITCODE)." }

    & $signTool verify /pa /all /v $resolvedFile
    if ($LASTEXITCODE -ne 0) { throw "SignTool could not validate $resolvedFile (exit code $LASTEXITCODE)." }

    $signature = Get-AuthenticodeSignature -FilePath $resolvedFile
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Get-AuthenticodeSignature returned '$($signature.Status)' for $resolvedFile."
    }
    if (-not $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint.ToUpperInvariant() -ne $actualThumbprint) {
        throw "The signer returned by Authenticode does not match the configured certificate for $resolvedFile."
    }
    if (-not $signature.TimeStamperCertificate) { throw "The Authenticode signature on $resolvedFile has no verifiable timestamp." }

    Write-Host "PASS: Authenticode signature and timestamp verified: $([IO.Path]::GetFileName($resolvedFile))"
    Write-Host "Signer thumbprint: $actualThumbprint"
    Write-Host "Timestamp signer: $($signature.TimeStamperCertificate.Subject)"
}
finally {
    if ($certificate) { $certificate.Dispose() }
    if (Test-Path -LiteralPath $temporaryPfx) {
        Remove-Item -LiteralPath $temporaryPfx -Force -ErrorAction SilentlyContinue
    }
}
