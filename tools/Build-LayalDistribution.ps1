[CmdletBinding()]
param(
    [switch]$RunInstallerLifecycleTests,
    [switch]$RequireCodeSigning
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $Root 'src/Layal.Desktop/Layal.Desktop.csproj'
$InstallerScript = Join-Path $Root 'installer/Layal.iss'
$Artifacts = Join-Path $Root 'artifacts'
$PublishDirectory = Join-Path $Artifacts 'publish/win-x64'
$Distribution = Join-Path $Artifacts 'distribution'
$SbomDirectory = Join-Path $Distribution 'sbom'

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)][string]$Executable,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$Description
    )
    Write-Host "--- $Description ---"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Description failed with exit code $LASTEXITCODE."
    }
}

[xml]$projectXml = Get-Content -LiteralPath $ProjectPath -Raw
$versionNode = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if (-not $versionNode -or [string]::IsNullOrWhiteSpace($versionNode.InnerText)) {
    throw 'Could not determine the project version from Layal.Desktop.csproj.'
}
$version = $versionNode.InnerText.Trim()
if ($version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)([-+][0-9A-Za-z.-]+)?$') {
    throw "Project version '$version' is not a supported three-part version."
}
$versionMatch = [regex]::Match($version, '^(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)$')
if (-not $versionMatch.Success) {
    throw 'The distribution lifecycle fixture requires a stable numeric project version (no prerelease suffix).'
}
$major = [int]$versionMatch.Groups['major'].Value
$minor = [int]$versionMatch.Groups['minor'].Value
$patch = [int]$versionMatch.Groups['patch'].Value
if ($patch -gt 0) {
    $previousVersion = "$major.$minor.$($patch - 1)"
} elseif ($minor -gt 0) {
    $previousVersion = "$major.$($minor - 1).99"
} elseif ($major -gt 0) {
    $previousVersion = "$($major - 1).99.99"
} else {
    throw 'Cannot create a lower synthetic installer version from 0.0.0.'
}
$assemblyVersion = "$major.$minor.$patch.0"
$previousAssemblyVersion = (($previousVersion.Split('.') | Select-Object -First 3) -join '.') + '.0'

$signingValues = @(
    $env:LAYAL_SIGNING_CERTIFICATE_BASE64,
    $env:LAYAL_SIGNING_CERTIFICATE_PASSWORD,
    $env:LAYAL_SIGNING_CERTIFICATE_THUMBPRINT
)
$signingPresent = @($signingValues | ForEach-Object { -not [string]::IsNullOrWhiteSpace($_) })
$signingConfigured = $signingPresent -notcontains $false
$anySigningConfigured = $signingPresent -contains $true
if ($anySigningConfigured -and -not $signingConfigured) {
    throw 'Partial code-signing configuration detected. Configure all three LAYAL_SIGNING_CERTIFICATE_* secrets or configure none.'
}
if ($RequireCodeSigning -and -not $signingConfigured) {
    throw 'A tagged release requires code signing. Configure LAYAL_SIGNING_CERTIFICATE_BASE64, LAYAL_SIGNING_CERTIFICATE_PASSWORD, and LAYAL_SIGNING_CERTIFICATE_THUMBPRINT in GitHub Actions repository secrets.'
}
if ($RequireCodeSigning) {
    # Fail before publishing or signing anything if owner-approved licensing/provenance is incomplete.
    Invoke-Checked -Executable 'python' -Arguments @(
        (Join-Path $PSScriptRoot 'validate_licenses.py'),
        '--manifest-only', '--require-release-clearance'
    ) -Description 'Enforce release legal/license and asset-provenance preflight'

    $releaseCommitSha = (& git -C $Root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $releaseCommitSha -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Could not resolve the full Git commit SHA for the signed release candidate.'
    }
    Invoke-Checked -Executable 'python' -Arguments @(
        (Join-Path $PSScriptRoot 'validate_release_readiness.py'),
        '--require-manual-acceptance', '--commit-sha', $releaseCommitSha
    ) -Description 'Enforce evidence-backed manual Windows release acceptance'
}

# Clear generated distribution outputs so stale SBOMs or notices cannot survive a rebuild.
Remove-Item -LiteralPath $Distribution -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Artifacts, $Distribution, $SbomDirectory | Out-Null
Remove-Item -LiteralPath $PublishDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

# Publish self-contained to avoid requiring .NET Desktop Runtime on the target PC.
Invoke-Checked -Executable 'dotnet' -Arguments @(
    'publish', $ProjectPath, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--output', $PublishDirectory,
    '-p:PublishSingleFile=false', "-p:Version=$version",
    "-p:AssemblyVersion=$assemblyVersion", "-p:FileVersion=$assemblyVersion",
    '--nologo'
) -Description "Publish self-contained Layal $version (win-x64)"

$exePath = Join-Path $PublishDirectory 'Layal.exe'
$appAssemblyPath = Join-Path $PublishDirectory 'Layal.dll'
$coreClrPath = Join-Path $PublishDirectory 'coreclr.dll'
$hostFxrPath = Join-Path $PublishDirectory 'hostfxr.dll'
foreach ($requiredPath in @($exePath, $appAssemblyPath, $coreClrPath, $hostFxrPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Self-contained publish is incomplete; expected file missing: $requiredPath"
    }
}
Write-Host 'PASS: Layal.exe and bundled .NET runtime files exist.'

$signerScript = Join-Path $PSScriptRoot 'Sign-LayalFile.ps1'
& $signerScript -FilePath $exePath -RequireSignature:$RequireCodeSigning
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing/verification failed for Layal.exe (exit code $LASTEXITCODE)." }
& $signerScript -FilePath $appAssemblyPath -RequireSignature:$RequireCodeSigning
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing/verification failed for Layal.dll (exit code $LASTEXITCODE)." }
if (-not $signingConfigured) {
    Write-Host 'NOT SIGNED: Layal.exe and Layal.dll remain unsigned for this non-release CI build.'
}

# Include license notices in the installed application payload and portable ZIP.
$payloadLicenseDirectory = Join-Path $PublishDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $payloadLicenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $payloadLicenseDirectory 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/WTFPL.txt') -Destination (Join-Path $payloadLicenseDirectory 'WTFPL.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/MIT-JeremyAnsel.HLSL.Targets.txt') -Destination (Join-Path $payloadLicenseDirectory 'MIT-JeremyAnsel.HLSL.Targets.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/PROJECT-LICENSE-STATUS.md') -Destination (Join-Path $payloadLicenseDirectory 'PROJECT-LICENSE-STATUS.md') -Force

$portableZip = Join-Path $Distribution "Layal-$version-portable-win-x64.zip"
Remove-Item -LiteralPath $portableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $portableZip -CompressionLevel Optimal -Force

# Produce an SBOM for the direct and transitive .NET dependency graph. A generated SBOM is
# inventory evidence, not a substitute for a human license/notice compatibility review.
$toolDirectory = Join-Path $Artifacts 'dotnet-tools'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$sbomCommand = Join-Path $toolDirectory 'dotnet-CycloneDX.exe'
if (-not (Test-Path -LiteralPath $sbomCommand -PathType Leaf)) {
    Invoke-Checked -Executable 'dotnet' -Arguments @(
        'tool', 'install', 'CycloneDX', '--tool-path', $toolDirectory, '--version', '6.3.1'
    ) -Description 'Install pinned CycloneDX .NET SBOM generator'
}
Invoke-Checked -Executable $sbomCommand -Arguments @($ProjectPath, '-o', $SbomDirectory, '--output-format', 'json') -Description 'Generate CycloneDX SBOM in JSON format'
$sbomFiles = @(Get-ChildItem -LiteralPath $SbomDirectory -Filter '*.json' -File -Recurse)
$validSbom = $false
foreach ($sbomFile in $sbomFiles) {
    try {
        $sbomData = Get-Content -LiteralPath $sbomFile.FullName -Raw | ConvertFrom-Json
        if ($sbomData.bomFormat -eq 'CycloneDX' -and $sbomData.components) {
            $validSbom = $true
            if ($sbomFile.Name -ne "Layal-$version.cdx.json") {
                $renamedSbom = Join-Path $SbomDirectory "Layal-$version.cdx.json"
                Move-Item -LiteralPath $sbomFile.FullName -Destination $renamedSbom -Force
            }
            break
        }
    } catch {
        # Continue scanning generated JSON files; the expected SBOM must pass below.
    }
}
if (-not $validSbom) {
    throw 'CycloneDX did not produce a valid JSON SBOM with dependency components.'
}

$expectedSbom = Join-Path $SbomDirectory "Layal-$version.cdx.json"
$distributionLicenseDirectory = Join-Path $Distribution 'licenses'
New-Item -ItemType Directory -Force -Path $distributionLicenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $distributionLicenseDirectory 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/WTFPL.txt') -Destination (Join-Path $distributionLicenseDirectory 'WTFPL.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/MIT-JeremyAnsel.HLSL.Targets.txt') -Destination (Join-Path $distributionLicenseDirectory 'MIT-JeremyAnsel.HLSL.Targets.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/PROJECT-LICENSE-STATUS.md') -Destination (Join-Path $distributionLicenseDirectory 'PROJECT-LICENSE-STATUS.md') -Force
$licenseAuditReport = Join-Path $distributionLicenseDirectory "Layal-$version-license-audit.md"
$licenseArguments = @(
    (Join-Path $PSScriptRoot 'validate_licenses.py'),
    '--sbom', $expectedSbom,
    '--enrich-sbom-license-metadata',
    '--report', $licenseAuditReport
)
if ($RequireCodeSigning) { $licenseArguments += '--require-release-clearance' }
Invoke-Checked -Executable 'python' -Arguments $licenseArguments -Description 'Validate CycloneDX SBOM and enforce legal clearance for release builds'

$dependencyReport = Join-Path $Distribution "Layal-$version-dependencies.txt"
$dependencyOutput = & dotnet list $ProjectPath package --include-transitive 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not generate the .NET dependency list (exit code $LASTEXITCODE)."
}
$dependencyOutput | Set-Content -LiteralPath $dependencyReport -Encoding utf8

# Find or install Inno Setup only on the build runner; no installer compiler is required on user PCs.
$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$isccCandidates = @(
    (Join-Path $programFilesX86 'Inno Setup 6/ISCC.exe'),
    (Join-Path $programFiles 'Inno Setup 6/ISCC.exe')
)
$isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1 }
if (-not $iscc) {
    $choco = Get-Command 'choco.exe' -ErrorAction SilentlyContinue
    if (-not $choco) {
        throw 'Inno Setup 6 compiler (ISCC.exe) is missing and Chocolatey is unavailable.'
    }
    Invoke-Checked -Executable $choco.Source -Arguments @('install', 'innosetup', '--yes', '--no-progress') -Description 'Install Inno Setup 6 on the CI runner'
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $iscc) {
        throw 'Inno Setup installation completed but ISCC.exe was not found.'
    }
}

$currentInstaller = Join-Path $Distribution "Layal-Setup-$version-win-x64.exe"
$previousInstallerInDistribution = Join-Path $Distribution "Layal-Setup-$previousVersion-win-x64.exe"
$previousInstallerForTest = Join-Path $Artifacts "installer-test/Layal-Setup-$previousVersion-test-win-x64.exe"
$previousPublishDirectory = Join-Path $Artifacts 'publish/rollback-fixture-win-x64'
$installerTestDirectory = Split-Path -Parent $previousInstallerForTest
New-Item -ItemType Directory -Force -Path $installerTestDirectory, $previousPublishDirectory | Out-Null
Remove-Item -LiteralPath $previousInstallerInDistribution, $previousInstallerForTest -Force -ErrorAction SilentlyContinue

if ($RunInstallerLifecycleTests) {
    # This fixture uses the current source with a lower version number to test Inno's
    # install/upgrade/downgrade/uninstall mechanics. It is not an actual historical release.
    Invoke-Checked -Executable 'dotnet' -Arguments @(
        'publish', $ProjectPath, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '--output', $previousPublishDirectory,
        '-p:PublishSingleFile=false', "-p:Version=$previousVersion",
        "-p:AssemblyVersion=$previousAssemblyVersion", "-p:FileVersion=$previousAssemblyVersion",
        '--nologo'
    ) -Description "Publish lower-version installer test fixture $previousVersion"

    $previousPublishExe = Join-Path $previousPublishDirectory 'Layal.exe'
    if (-not (Test-Path -LiteralPath $previousPublishExe -PathType Leaf)) {
        throw 'Rollback fixture did not contain Layal.exe.'
    }

    $oldVersionDefine = '/DAppVersion=' + $previousVersion
    $oldPublishDefine = '/DPublishDir=' + $previousPublishDirectory
    Invoke-Checked -Executable $iscc -Arguments @($oldVersionDefine, $oldPublishDefine, $InstallerScript) -Description "Compile installer fixture $previousVersion"
    if (-not (Test-Path -LiteralPath $previousInstallerInDistribution -PathType Leaf)) {
        throw "Compiled previous-version installer was not found: $previousInstallerInDistribution"
    }
    Move-Item -LiteralPath $previousInstallerInDistribution -Destination $previousInstallerForTest -Force
}

$currentVersionDefine = '/DAppVersion=' + $version
$currentPublishDefine = '/DPublishDir=' + $PublishDirectory
Invoke-Checked -Executable $iscc -Arguments @($currentVersionDefine, $currentPublishDefine, $InstallerScript) -Description "Compile Layal installer $version"

if (-not (Test-Path -LiteralPath $currentInstaller -PathType Leaf)) {
    throw "Installer output was not created: $currentInstaller"
}
if ((Get-Item -LiteralPath $currentInstaller).Length -lt 1000000) {
    throw 'Installer output is unexpectedly small; refusing to package an incomplete installer.'
}
Write-Host "PASS: installer created: $currentInstaller"

& $signerScript -FilePath $currentInstaller -RequireSignature:$RequireCodeSigning

if ($RunInstallerLifecycleTests) {
    & (Join-Path $PSScriptRoot 'Test-LayalInstaller.ps1') -PreviousInstallerPath $previousInstallerForTest -CurrentInstallerPath $currentInstaller -PreviousVersion $previousVersion -CurrentVersion $version
    if ($LASTEXITCODE -ne 0) {
        throw "Installer lifecycle acceptance failed with exit code $LASTEXITCODE."
    }
}

# Generated release evidence explicitly records gates that this pipeline cannot prove.
$distributionStatus = Join-Path $Distribution "Layal-$version-release-status.txt"
@(
    "Layal version: $version"
    "Target: Windows x64"
    "Deployment: self-contained; .NET runtime files are bundled with the app."
    "Installer: per-user Inno Setup installer; upgrades use a stable AppId."
    "User data: stored outside the installation directory and intentionally preserved by uninstall."
    "Installer lifecycle CI: $(if ($RunInstallerLifecycleTests) { 'PASS: fresh install, upgrade, rollback, app startup, shortcut/registry cleanup, and user-data preservation' } else { 'NOT RUN: use -RunInstallerLifecycleTests before release validation' })"
    "Rollback fixture: synthetic lower version from current source; not a historical release binary."
    $(if ($signingConfigured) { "Code signing: SIGNED AND VERIFIED" } else { "Code signing: NOT SIGNED by this build script. Do not present this artifact as a signed public release." })
    $(if ($signingConfigured) { "Signing certificate thumbprint: $(($env:LAYAL_SIGNING_CERTIFICATE_THUMBPRINT -replace '\s','').ToUpperInvariant())" } else { "Signing certificate thumbprint: NOT AVAILABLE" })
    $(if ($signingConfigured) { "Authenticode verification: PASS (SignTool and Get-AuthenticodeSignature)" } else { "Authenticode verification: NOT RUN (no signing certificate configured)" })
    $(if ($signingConfigured) { "RFC 3161 timestamp: PASS (SHA-256)" } else { "RFC 3161 timestamp: NOT RUN" })
    $(if ($signingConfigured) { "Signed files: Layal.exe; Layal.dll; Layal-Setup-$version-win-x64.exe" } else { "Signed files: NONE" })
    "License review: NOT CERTIFIED by generated SBOM. Review all direct/transitive dependencies and included assets before public/commercial distribution."
    "Manual Windows acceptance: still required on a clean target PC, including display/DPI and user-session behavior."
) | Set-Content -LiteralPath $distributionStatus -Encoding utf8

# Hash each output except this manifest itself; paths are relative and use forward slashes.
$checksumPath = Join-Path $Distribution 'SHA256SUMS.txt'
Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue
$hashLines = foreach ($file in (Get-ChildItem -LiteralPath $Distribution -File -Recurse | Where-Object { $_.FullName -ne $checksumPath } | Sort-Object FullName)) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $file.FullName.Substring($Distribution.Length).TrimStart('\','/').Replace('\','/')
    "$hash  $relative"
}
$hashLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

$integrityVerifier = Join-Path $PSScriptRoot 'Verify-LayalDistributionIntegrity.ps1'
$verifyParameters = @{ DistributionDirectory = $Distribution }
if ($RequireCodeSigning) { $verifyParameters.RequireSignature = $true }
& $integrityVerifier @verifyParameters

Write-Host ''
Write-Host "Distribution folder: $Distribution"
Get-ChildItem -LiteralPath $Distribution -File -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
Write-Host 'NOTE: packaging and lifecycle tests do not replace code signing, license review, or target-device acceptance.'
) {
        throw 'Could not resolve the full Git commit SHA for the signed release candidate.'
    }
    Invoke-Checked -Executable 'python' -Arguments @(
        (Join-Path $PSScriptRoot 'validate_release_readiness.py'),
        '--require-manual-acceptance', '--commit-sha', $releaseCommitSha
    ) -Description 'Enforce evidence-backed manual Windows release acceptance'
}

# Clear generated distribution outputs so stale SBOMs or notices cannot survive a rebuild.
Remove-Item -LiteralPath $Distribution -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $Artifacts, $Distribution, $SbomDirectory | Out-Null
Remove-Item -LiteralPath $PublishDirectory -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $PublishDirectory | Out-Null

# Publish self-contained to avoid requiring .NET Desktop Runtime on the target PC.
Invoke-Checked -Executable 'dotnet' -Arguments @(
    'publish', $ProjectPath, '--configuration', 'Release', '--runtime', 'win-x64',
    '--self-contained', 'true', '--output', $PublishDirectory,
    '-p:PublishSingleFile=false', "-p:Version=$version",
    "-p:AssemblyVersion=$assemblyVersion", "-p:FileVersion=$assemblyVersion",
    '--nologo'
) -Description "Publish self-contained Layal $version (win-x64)"

$exePath = Join-Path $PublishDirectory 'Layal.exe'
$appAssemblyPath = Join-Path $PublishDirectory 'Layal.dll'
$coreClrPath = Join-Path $PublishDirectory 'coreclr.dll'
$hostFxrPath = Join-Path $PublishDirectory 'hostfxr.dll'
foreach ($requiredPath in @($exePath, $appAssemblyPath, $coreClrPath, $hostFxrPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Self-contained publish is incomplete; expected file missing: $requiredPath"
    }
}
Write-Host 'PASS: Layal.exe and bundled .NET runtime files exist.'

$signerScript = Join-Path $PSScriptRoot 'Sign-LayalFile.ps1'
& $signerScript -FilePath $exePath -RequireSignature:$RequireCodeSigning
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing/verification failed for Layal.exe (exit code $LASTEXITCODE)." }
& $signerScript -FilePath $appAssemblyPath -RequireSignature:$RequireCodeSigning
if ($LASTEXITCODE -ne 0) { throw "Authenticode signing/verification failed for Layal.dll (exit code $LASTEXITCODE)." }
if (-not $signingConfigured) {
    Write-Host 'NOT SIGNED: Layal.exe and Layal.dll remain unsigned for this non-release CI build.'
}

# Include license notices in the installed application payload and portable ZIP.
$payloadLicenseDirectory = Join-Path $PublishDirectory 'licenses'
New-Item -ItemType Directory -Force -Path $payloadLicenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $payloadLicenseDirectory 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/WTFPL.txt') -Destination (Join-Path $payloadLicenseDirectory 'WTFPL.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/MIT-JeremyAnsel.HLSL.Targets.txt') -Destination (Join-Path $payloadLicenseDirectory 'MIT-JeremyAnsel.HLSL.Targets.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/PROJECT-LICENSE-STATUS.md') -Destination (Join-Path $payloadLicenseDirectory 'PROJECT-LICENSE-STATUS.md') -Force

$portableZip = Join-Path $Distribution "Layal-$version-portable-win-x64.zip"
Remove-Item -LiteralPath $portableZip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $PublishDirectory '*') -DestinationPath $portableZip -CompressionLevel Optimal -Force

# Produce an SBOM for the direct and transitive .NET dependency graph. A generated SBOM is
# inventory evidence, not a substitute for a human license/notice compatibility review.
$toolDirectory = Join-Path $Artifacts 'dotnet-tools'
New-Item -ItemType Directory -Force -Path $toolDirectory | Out-Null
$sbomCommand = Join-Path $toolDirectory 'dotnet-CycloneDX.exe'
if (-not (Test-Path -LiteralPath $sbomCommand -PathType Leaf)) {
    Invoke-Checked -Executable 'dotnet' -Arguments @(
        'tool', 'install', 'CycloneDX', '--tool-path', $toolDirectory, '--version', '6.3.1'
    ) -Description 'Install pinned CycloneDX .NET SBOM generator'
}
Invoke-Checked -Executable $sbomCommand -Arguments @($ProjectPath, '-o', $SbomDirectory, '--output-format', 'json') -Description 'Generate CycloneDX SBOM in JSON format'
$sbomFiles = @(Get-ChildItem -LiteralPath $SbomDirectory -Filter '*.json' -File -Recurse)
$validSbom = $false
foreach ($sbomFile in $sbomFiles) {
    try {
        $sbomData = Get-Content -LiteralPath $sbomFile.FullName -Raw | ConvertFrom-Json
        if ($sbomData.bomFormat -eq 'CycloneDX' -and $sbomData.components) {
            $validSbom = $true
            if ($sbomFile.Name -ne "Layal-$version.cdx.json") {
                $renamedSbom = Join-Path $SbomDirectory "Layal-$version.cdx.json"
                Move-Item -LiteralPath $sbomFile.FullName -Destination $renamedSbom -Force
            }
            break
        }
    } catch {
        # Continue scanning generated JSON files; the expected SBOM must pass below.
    }
}
if (-not $validSbom) {
    throw 'CycloneDX did not produce a valid JSON SBOM with dependency components.'
}

$expectedSbom = Join-Path $SbomDirectory "Layal-$version.cdx.json"
$distributionLicenseDirectory = Join-Path $Distribution 'licenses'
New-Item -ItemType Directory -Force -Path $distributionLicenseDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $Root 'docs/THIRD_PARTY_NOTICES.md') -Destination (Join-Path $distributionLicenseDirectory 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/WTFPL.txt') -Destination (Join-Path $distributionLicenseDirectory 'WTFPL.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/MIT-JeremyAnsel.HLSL.Targets.txt') -Destination (Join-Path $distributionLicenseDirectory 'MIT-JeremyAnsel.HLSL.Targets.txt') -Force
Copy-Item -LiteralPath (Join-Path $Root 'licenses/PROJECT-LICENSE-STATUS.md') -Destination (Join-Path $distributionLicenseDirectory 'PROJECT-LICENSE-STATUS.md') -Force
$licenseAuditReport = Join-Path $distributionLicenseDirectory "Layal-$version-license-audit.md"
$licenseArguments = @(
    (Join-Path $PSScriptRoot 'validate_licenses.py'),
    '--sbom', $expectedSbom,
    '--enrich-sbom-license-metadata',
    '--report', $licenseAuditReport
)
if ($RequireCodeSigning) { $licenseArguments += '--require-release-clearance' }
Invoke-Checked -Executable 'python' -Arguments $licenseArguments -Description 'Validate CycloneDX SBOM and enforce legal clearance for release builds'

$dependencyReport = Join-Path $Distribution "Layal-$version-dependencies.txt"
$dependencyOutput = & dotnet list $ProjectPath package --include-transitive 2>&1
if ($LASTEXITCODE -ne 0) {
    throw "Could not generate the .NET dependency list (exit code $LASTEXITCODE)."
}
$dependencyOutput | Set-Content -LiteralPath $dependencyReport -Encoding utf8

# Find or install Inno Setup only on the build runner; no installer compiler is required on user PCs.
$programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
$programFiles = [Environment]::GetFolderPath('ProgramFiles')
$isccCandidates = @(
    (Join-Path $programFilesX86 'Inno Setup 6/ISCC.exe'),
    (Join-Path $programFiles 'Inno Setup 6/ISCC.exe')
)
$isccCommand = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
$iscc = if ($isccCommand) { $isccCommand.Source } else { $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1 }
if (-not $iscc) {
    $choco = Get-Command 'choco.exe' -ErrorAction SilentlyContinue
    if (-not $choco) {
        throw 'Inno Setup 6 compiler (ISCC.exe) is missing and Chocolatey is unavailable.'
    }
    Invoke-Checked -Executable $choco.Source -Arguments @('install', 'innosetup', '--yes', '--no-progress') -Description 'Install Inno Setup 6 on the CI runner'
    $iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $iscc) {
        throw 'Inno Setup installation completed but ISCC.exe was not found.'
    }
}

$currentInstaller = Join-Path $Distribution "Layal-Setup-$version-win-x64.exe"
$previousInstallerInDistribution = Join-Path $Distribution "Layal-Setup-$previousVersion-win-x64.exe"
$previousInstallerForTest = Join-Path $Artifacts "installer-test/Layal-Setup-$previousVersion-test-win-x64.exe"
$previousPublishDirectory = Join-Path $Artifacts 'publish/rollback-fixture-win-x64'
$installerTestDirectory = Split-Path -Parent $previousInstallerForTest
New-Item -ItemType Directory -Force -Path $installerTestDirectory, $previousPublishDirectory | Out-Null
Remove-Item -LiteralPath $previousInstallerInDistribution, $previousInstallerForTest -Force -ErrorAction SilentlyContinue

if ($RunInstallerLifecycleTests) {
    # This fixture uses the current source with a lower version number to test Inno's
    # install/upgrade/downgrade/uninstall mechanics. It is not an actual historical release.
    Invoke-Checked -Executable 'dotnet' -Arguments @(
        'publish', $ProjectPath, '--configuration', 'Release', '--runtime', 'win-x64',
        '--self-contained', 'true', '--output', $previousPublishDirectory,
        '-p:PublishSingleFile=false', "-p:Version=$previousVersion",
        "-p:AssemblyVersion=$previousAssemblyVersion", "-p:FileVersion=$previousAssemblyVersion",
        '--nologo'
    ) -Description "Publish lower-version installer test fixture $previousVersion"

    $previousPublishExe = Join-Path $previousPublishDirectory 'Layal.exe'
    if (-not (Test-Path -LiteralPath $previousPublishExe -PathType Leaf)) {
        throw 'Rollback fixture did not contain Layal.exe.'
    }

    $oldVersionDefine = '/DAppVersion=' + $previousVersion
    $oldPublishDefine = '/DPublishDir=' + $previousPublishDirectory
    Invoke-Checked -Executable $iscc -Arguments @($oldVersionDefine, $oldPublishDefine, $InstallerScript) -Description "Compile installer fixture $previousVersion"
    if (-not (Test-Path -LiteralPath $previousInstallerInDistribution -PathType Leaf)) {
        throw "Compiled previous-version installer was not found: $previousInstallerInDistribution"
    }
    Move-Item -LiteralPath $previousInstallerInDistribution -Destination $previousInstallerForTest -Force
}

$currentVersionDefine = '/DAppVersion=' + $version
$currentPublishDefine = '/DPublishDir=' + $PublishDirectory
Invoke-Checked -Executable $iscc -Arguments @($currentVersionDefine, $currentPublishDefine, $InstallerScript) -Description "Compile Layal installer $version"

if (-not (Test-Path -LiteralPath $currentInstaller -PathType Leaf)) {
    throw "Installer output was not created: $currentInstaller"
}
if ((Get-Item -LiteralPath $currentInstaller).Length -lt 1000000) {
    throw 'Installer output is unexpectedly small; refusing to package an incomplete installer.'
}
Write-Host "PASS: installer created: $currentInstaller"

& $signerScript -FilePath $currentInstaller -RequireSignature:$RequireCodeSigning

if ($RunInstallerLifecycleTests) {
    & (Join-Path $PSScriptRoot 'Test-LayalInstaller.ps1') -PreviousInstallerPath $previousInstallerForTest -CurrentInstallerPath $currentInstaller -PreviousVersion $previousVersion -CurrentVersion $version
    if ($LASTEXITCODE -ne 0) {
        throw "Installer lifecycle acceptance failed with exit code $LASTEXITCODE."
    }
}

# Generated release evidence explicitly records gates that this pipeline cannot prove.
$distributionStatus = Join-Path $Distribution "Layal-$version-release-status.txt"
@(
    "Layal version: $version"
    "Target: Windows x64"
    "Deployment: self-contained; .NET runtime files are bundled with the app."
    "Installer: per-user Inno Setup installer; upgrades use a stable AppId."
    "User data: stored outside the installation directory and intentionally preserved by uninstall."
    "Installer lifecycle CI: $(if ($RunInstallerLifecycleTests) { 'PASS: fresh install, upgrade, rollback, app startup, shortcut/registry cleanup, and user-data preservation' } else { 'NOT RUN: use -RunInstallerLifecycleTests before release validation' })"
    "Rollback fixture: synthetic lower version from current source; not a historical release binary."
    $(if ($signingConfigured) { "Code signing: SIGNED AND VERIFIED" } else { "Code signing: NOT SIGNED by this build script. Do not present this artifact as a signed public release." })
    $(if ($signingConfigured) { "Signing certificate thumbprint: $(($env:LAYAL_SIGNING_CERTIFICATE_THUMBPRINT -replace '\s','').ToUpperInvariant())" } else { "Signing certificate thumbprint: NOT AVAILABLE" })
    $(if ($signingConfigured) { "Authenticode verification: PASS (SignTool and Get-AuthenticodeSignature)" } else { "Authenticode verification: NOT RUN (no signing certificate configured)" })
    $(if ($signingConfigured) { "RFC 3161 timestamp: PASS (SHA-256)" } else { "RFC 3161 timestamp: NOT RUN" })
    $(if ($signingConfigured) { "Signed files: Layal.exe; Layal.dll; Layal-Setup-$version-win-x64.exe" } else { "Signed files: NONE" })
    "License review: NOT CERTIFIED by generated SBOM. Review all direct/transitive dependencies and included assets before public/commercial distribution."
    "Manual Windows acceptance: still required on a clean target PC, including display/DPI and user-session behavior."
) | Set-Content -LiteralPath $distributionStatus -Encoding utf8

# Hash each output except this manifest itself; paths are relative and use forward slashes.
$checksumPath = Join-Path $Distribution 'SHA256SUMS.txt'
Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue
$hashLines = foreach ($file in (Get-ChildItem -LiteralPath $Distribution -File -Recurse | Where-Object { $_.FullName -ne $checksumPath } | Sort-Object FullName)) {
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $relative = $file.FullName.Substring($Distribution.Length).TrimStart('\','/').Replace('\','/')
    "$hash  $relative"
}
$hashLines | Set-Content -LiteralPath $checksumPath -Encoding ascii

$integrityVerifier = Join-Path $PSScriptRoot 'Verify-LayalDistributionIntegrity.ps1'
$verifyParameters = @{ DistributionDirectory = $Distribution }
if ($RequireCodeSigning) { $verifyParameters.RequireSignature = $true }
& $integrityVerifier @verifyParameters

Write-Host ''
Write-Host "Distribution folder: $Distribution"
Get-ChildItem -LiteralPath $Distribution -File -Recurse | Select-Object FullName, Length | Format-Table -AutoSize
Write-Host 'NOTE: packaging and lifecycle tests do not replace code signing, license review, or target-device acceptance.'
