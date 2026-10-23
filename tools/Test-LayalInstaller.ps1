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
# Exercise the product's real default per-user installation path. The preflight below
# refuses to touch a machine that already has a Layal install or Start Menu shortcut.
$installDirectory = Join-Path $env:LOCALAPPDATA 'Programs\Layal'
$startMenuShortcutPath = Join-Path ([Environment]::GetFolderPath('Programs')) 'Layal.lnk'
$userDataDirectory = Join-Path $env:LOCALAPPDATA 'Layal'
$sentinelPath = Join-Path $userDataDirectory "release-readiness-$testId.txt"
$sentinelContents = "Layal installer data-preservation test $testId"
$appProcess = $null
$testOwnsInstall = $false
if ($env:GITHUB_ACTIONS -ne 'true' -and $env:LAYAL_ALLOW_INSTALLER_TESTS -ne '1') {
    throw 'Refusing to run destructive installer tests outside CI unless LAYAL_ALLOW_INSTALLER_TESTS=1 is explicitly set in a disposable VM.'
}

function Normalize-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath($Path).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
}

function Get-LayalUninstallEntries {
    $uninstallRoot = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    if (-not (Test-Path -LiteralPath $uninstallRoot)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $uninstallRoot -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ItemProperty -LiteralPath $_.PSPath -ErrorAction SilentlyContinue } |
            Where-Object { $_.DisplayName -eq 'Layal' }
    )
}

function Get-InstallationEntries {
    param([Parameter(Mandatory = $true)][string]$InstallationPath)

    $expectedPath = Normalize-FullPath -Path $InstallationPath
    return @(
        Get-LayalUninstallEntries | Where-Object {
            if ([string]::IsNullOrWhiteSpace([string]$_.InstallLocation)) {
                $false
            } else {
                try {
                    (Normalize-FullPath -Path ([string]$_.InstallLocation)) -ieq $expectedPath
                } catch {
                    $false
                }
            }
        }
    )
}

function Assert-StartMenuShortcut {
    $exe = Join-Path $installDirectory 'Layal.exe'
    if (-not (Test-Path -LiteralPath $startMenuShortcutPath -PathType Leaf)) {
        throw "Layal Start Menu shortcut was not created: $startMenuShortcutPath"
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($startMenuShortcutPath)
    if ([IO.Path]::GetFullPath([string]$shortcut.TargetPath) -ine [IO.Path]::GetFullPath($exe)) {
        throw "Layal Start Menu shortcut points to '$($shortcut.TargetPath)' instead of '$exe'."
    }
    Write-Host 'PASS: Start Menu shortcut points to the installed Layal.exe.'
}

function Assert-UserDataSentinel {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$ExpectedContents
    )

    if (-not (Test-Path -LiteralPath $sentinelPath -PathType Leaf)) {
        throw "User data sentinel was removed during $Stage."
    }
    $actualContents = (Get-Content -LiteralPath $sentinelPath -Raw -Encoding utf8).Trim()
    if ($actualContents -cne $ExpectedContents) {
        throw "User data sentinel contents changed during $Stage."
    }
    Write-Host "PASS: user data sentinel and contents survived $Stage."
}

function Assert-InstalledVersion {
    param([Parameter(Mandatory = $true)][string]$ExpectedVersion)

    $exe = Join-Path $installDirectory 'Layal.exe'
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "Installer acceptance failed: Layal.exe is missing after installing $ExpectedVersion."
    }

    $entries = @(Get-InstallationEntries -InstallationPath $installDirectory)
    if ($entries.Count -ne 1) {
        throw "Expected one Layal Add/Remove Programs entry for $ExpectedVersion; found $($entries.Count)."
    }
    if ([string]$entries[0].DisplayVersion -ne $ExpectedVersion) {
        throw "Expected installed version $ExpectedVersion; registry reports '$($entries[0].DisplayVersion)'."
    }

    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf) -or
        [string]::IsNullOrWhiteSpace([string]$entries[0].UninstallString)) {
        throw "Uninstaller or its Add/Remove Programs registration is missing for Layal $ExpectedVersion."
    }

    Assert-StartMenuShortcut
    Write-Host "PASS: install/upgrade registry reports Layal $ExpectedVersion."
}

function Assert-Uninstalled {
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'Layal.exe') -PathType Leaf) {
        throw 'Layal.exe remains in the installation directory after uninstall.'
    }
    if (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe') -PathType Leaf) {
        throw 'The uninstaller remains in the installation directory after uninstall.'
    }
    if (Test-Path -LiteralPath $startMenuShortcutPath -PathType Leaf) {
        throw 'Layal Start Menu shortcut remains after uninstall.'
    }

    $remainingEntries = @(Get-LayalUninstallEntries)
    if ($remainingEntries.Count -ne 0) {
        throw "Layal still has $($remainingEntries.Count) Add/Remove Programs registration(s) after uninstall."
    }

    if (Test-Path -LiteralPath $installDirectory -PathType Container) {
        $remainingFiles = @(Get-ChildItem -LiteralPath $installDirectory -Force -ErrorAction SilentlyContinue)
        if ($remainingFiles.Count -ne 0) {
            throw "Installation directory still contains $($remainingFiles.Count) item(s) after uninstall."
        }
    }
    Write-Host 'PASS: uninstall removed app files, shortcut, and Add/Remove Programs registration.'
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
    $arguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-'
    )
    $process = Start-Process -FilePath $Path -ArgumentList $arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Installer failed with exit code $($process.ExitCode): $Path"
    }
}

try {
    $existingLayalEntries = @(Get-LayalUninstallEntries)
    if ($existingLayalEntries.Count -ne 0) {
        throw "Refusing installer lifecycle tests because this user already has $($existingLayalEntries.Count) Layal uninstall registration(s). Use a clean disposable Windows profile."
    }
    if (Test-Path -LiteralPath $installDirectory) {
        throw "Refusing to overwrite a pre-existing default install folder: $installDirectory"
    }
    if (Test-Path -LiteralPath $startMenuShortcutPath) {
        throw "Refusing to overwrite a pre-existing Start Menu shortcut: $startMenuShortcutPath"
    }

    New-Item -ItemType Directory -Force -Path $userDataDirectory | Out-Null
    Set-Content -LiteralPath $sentinelPath -Value $sentinelContents -Encoding utf8

    # From this point the test may create an installation that its finally block owns.
    # A preflight refusal above must never trigger uninstallation of a pre-existing app.
    $testOwnsInstall = $true
    Write-Host "--- Install baseline version $PreviousVersion ---"
    Invoke-QuietInstaller -Path $previousSetup
    Assert-InstalledVersion -ExpectedVersion $PreviousVersion
    Assert-UserDataSentinel -Stage 'fresh install' -ExpectedContents $sentinelContents
    Test-AppStartup

    Write-Host "--- Upgrade to $CurrentVersion ---"
    Invoke-QuietInstaller -Path $currentSetup
    Assert-InstalledVersion -ExpectedVersion $CurrentVersion
    Test-AppStartup
    Assert-UserDataSentinel -Stage 'upgrade' -ExpectedContents $sentinelContents

    Write-Host "--- Roll back to $PreviousVersion ---"
    Invoke-QuietInstaller -Path $previousSetup
    Assert-InstalledVersion -ExpectedVersion $PreviousVersion
    Test-AppStartup
    Assert-UserDataSentinel -Stage 'rollback' -ExpectedContents $sentinelContents

    Write-Host '--- Uninstall ---'
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        throw 'Inno Setup uninstaller was not installed.'
    }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($uninstall.ExitCode)."
    }
    Assert-Uninstalled
    $testOwnsInstall = $false
    Assert-UserDataSentinel -Stage 'uninstall' -ExpectedContents $sentinelContents

    Write-Host 'PASS: default-path install, upgrade, rollback, launch smoke, uninstall registration/shortcut cleanup, and user-data preservation.'
}
finally {
    if ($appProcess) {
        try { Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    }

    # Remove only the unique sentinel created here; never remove the Layal user-data folder.
    Remove-Item -LiteralPath $sentinelPath -Force -ErrorAction SilentlyContinue

    # Best-effort cleanup if a test failed before uninstall completed.
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if ($testOwnsInstall -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
        try {
            $cleanup = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
            if ($cleanup.ExitCode -ne 0) { Write-Warning "Best-effort test cleanup returned $($cleanup.ExitCode)." }
        } catch {
            Write-Warning "Could not complete best-effort test uninstall: $($_.Exception.Message)"
        }
    }
}
