[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PreviousInstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$CurrentInstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$PreviousVersion,

    [Parameter(Mandatory = $true)]
    [string]$CurrentVersion
)

# Run only on a disposable, clean CI/VM user profile. These test installers use the production AppId.
$ErrorActionPreference = 'Stop'
$previousSetup = (Resolve-Path -LiteralPath $PreviousInstallerPath).Path
$currentSetup = (Resolve-Path -LiteralPath $CurrentInstallerPath).Path
$testId = [guid]::NewGuid().ToString('N')
$installDirectory = Join-Path $env:LOCALAPPDATA "Temp\Layal-InstallerAcceptance-$testId"
$userDataDirectory = Join-Path $env:LOCALAPPDATA 'Layal'
$sentinelPath = Join-Path $userDataDirectory "release-readiness-$testId.txt"
$appProcess = $null
if ($env:GITHUB_ACTIONS -ne 'true' -and $env:LAYAL_ALLOW_INSTALLER_TESTS -ne '1') {
    throw 'Refusing to run destructive installer tests outside CI unless LAYAL_ALLOW_INSTALLER_TESTS=1 is explicitly set in a disposable VM.'
}

function Assert-InstalledVersion {
    param([Parameter(Mandatory = $true)][string]$ExpectedVersion)

    $exe = Join-Path $installDirectory 'Layal.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Installer acceptance failed: Layal.exe is missing after installing $ExpectedVersion."
    }

    $uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    $entries = @(Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue | ForEach-Object {
        Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue
    } | Where-Object {
        $_.DisplayName -eq 'Layal' -and
        $_.InstallLocation -and
        ([IO.Path]::GetFullPath([string]$_.InstallLocation).TrimEnd('\') -ieq [IO.Path]::GetFullPath($installDirectory).TrimEnd('\'))
    })

    if ($entries.Count -ne 1) {
        throw "Expected one Layal Add/Remove Programs entry for $ExpectedVersion; found $($entries.Count)."
    }
    if ([string]$entries[0].DisplayVersion -ne $ExpectedVersion) {
        throw "Expected installed version $ExpectedVersion; registry reports '$($entries[0].DisplayVersion)'."
    }
    Write-Host "PASS: install/upgrade registry reports Layal $ExpectedVersion."
}

function Test-AppStartup {
    $exe = Join-Path $installDirectory 'Layal.exe'
    $script:appProcess = Start-Process -FilePath $exe -WorkingDirectory $installDirectory -PassThru
    Start-Sleep -Seconds 3
    $script:appProcess.Refresh()
    if ($script:appProcess.HasExited) {
        throw "Layal exited during startup smoke check (exit code $($script:appProcess.ExitCode))."
    }
    Stop-Process -Id $script:appProcess.Id -Force
    Wait-Process -Id $script:appProcess.Id -Timeout 10 -ErrorAction SilentlyContinue
    $script:appProcess = $null
    Write-Host 'PASS: installed Layal process remained running during startup smoke test.'
}

function Invoke-QuietInstaller {
    param([Parameter(Mandatory = $true)][string]$Path)
    $directoryArgument = '/DIR="' + $installDirectory + '"'
    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        $directoryArgument
    )
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode): $Path"
    }
}

try {
    if (Test-Path -LiteralPath $installDirectory) {
        throw "Refusing to use a pre-existing test install folder: $installDirectory"
    }

    New-Item -ItemType Directory -Force -Path $userDataDirectory | Out-Null
    Set-Content -LiteralPath $sentinelPath -Value "Layal installer data-preservation test $testId" -Encoding utf8

    Write-Host "--- Install baseline version $PreviousVersion ---"
    Invoke-QuietInstaller -Path $previousSetup
    Assert-InstalledVersion -ExpectedVersion $PreviousVersion
    Test-AppStartup

    Write-Host "--- Upgrade to $CurrentVersion ---"
    Invoke-QuietInstaller -Path $currentSetup
    Assert-InstalledVersion -ExpectedVersion $CurrentVersion
    Test-AppStartup
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'User data sentinel was removed during upgrade.'
    }
    Write-Host 'PASS: user data sentinel survived upgrade.'

    Write-Host "--- Roll back to $PreviousVersion ---"
    Invoke-QuietInstaller -Path $previousSetup
    Assert-InstalledVersion -ExpectedVersion $PreviousVersion
    Test-AppStartup
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'User data sentinel was removed during rollback.'
    }
    Write-Host 'PASS: user data sentinel survived rollback.'

    Write-Host '--- Uninstall ---'
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'Inno Setup uninstaller was not installed.'
    }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($uninstall.ExitCode)."
    }
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'Layal.exe')) {
        throw 'Layal.exe remains in the installation directory after uninstall.'
    }
    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw 'User data sentinel was removed during uninstall.'
    }

    Write-Host 'PASS: install, upgrade, rollback, launch smoke, uninstall, and user-data preservation.'
}
finally {
    if ($appProcess) {
        try { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    # Remove only the unique sentinel created here; never remove the Layal user-data folder.
    Remove-Item -LiteralPath $sentinelPath -Force -ErrorAction SilentlyContinue

    # Best-effort cleanup if a test failed before uninstall completed.
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        try {
            $cleanup = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
            if ($cleanup.ExitCode -ne 0) { Write-Warning "Best-effort test cleanup returned $($cleanup.ExitCode)." }
        } catch {
            Write-Warning "Could not complete best-effort test uninstall: $($_.Exception.Message)"
        }
    }
}
