[CmdletBinding()]
param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Artifacts = Join-Path $Root 'artifacts'
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$LogPath = Join-Path $Artifacts "verification-$Stamp.log"
$TranscriptStarted = $false
$Failure = $null

try {
    Start-Transcript -Path $LogPath -Force | Out-Null
    $TranscriptStarted = $true
    Set-Location $Root

    Write-Host '=== Layal verification ==='
    Write-Host "Project root: $Root"
    Write-Host "Timestamp: $(Get-Date -Format o)"

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        throw 'BLOCKED: .NET SDK not found. Install the .NET 10 SDK on Windows, then rerun this script.'
    }

    Write-Host '--- Installed SDKs ---'
    & dotnet --list-sdks
    if ($LASTEXITCODE -ne 0) { throw "dotnet --list-sdks failed with exit code $LASTEXITCODE" }
    $sdkText = (& dotnet --list-sdks | Out-String)
    if ($sdkText -notmatch '(^|\s)10\.') {
        throw 'BLOCKED: .NET 10 SDK is required by the current project. Install .NET 10 SDK and retry.'
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if ($python) {
        Write-Host '--- Static project validation ---'
        & $python.Source (Join-Path $Root 'tools/validate_project.py')
        if ($LASTEXITCODE -ne 0) { throw "Static project validation failed with exit code $LASTEXITCODE" }
        Write-Host '--- Source/release hygiene validation ---'
        & $python.Source (Join-Path $Root 'tools/validate_release.py')
        if ($LASTEXITCODE -ne 0) { throw "Source/release hygiene validation failed with exit code $LASTEXITCODE" }
    } else {
        throw 'Python 3 is required to run tools/validate_project.py. Install Python 3 and retry.'
    }

    if (-not $SkipRestore) {
        Write-Host '--- Restore ---'
        & dotnet restore (Join-Path $Root 'Layal.sln') --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    }

    Write-Host '--- Release build ---'
    & dotnet build (Join-Path $Root 'Layal.sln') --configuration Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $Root 'src/Layal.Desktop/bin/Release/net10.0-windows/Layal.exe'
    $dll = Join-Path $Root 'src/Layal.Desktop/bin/Release/net10.0-windows/Layal.dll'
    if ((Test-Path $exe) -or (Test-Path $dll)) {
        Write-Host 'PASS: Release output exists.'
        if (Test-Path $exe) { Write-Host "EXE: $exe" }
        if (Test-Path $dll) { Write-Host "DLL: $dll" }
    } else {
        throw 'Build returned success but the expected Layal output file was not found.'
    }

    $testProjects = @(Get-ChildItem -Path $Root -Recurse -Filter '*Tests.csproj' -File | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
    if ($testProjects.Count -gt 0) {
        $testResults = Join-Path $Artifacts 'test-results'
        New-Item -ItemType Directory -Force -Path $testResults | Out-Null
        foreach ($testProject in $testProjects) {
            Write-Host "--- Test project: $($testProject.FullName) ---"
            $trxName = "$($testProject.BaseName).trx"
            & dotnet test $testProject.FullName --configuration Release --no-restore --nologo --logger "trx;LogFileName=$trxName" --results-directory $testResults
            if ($LASTEXITCODE -ne 0) { throw "dotnet test failed for $($testProject.Name) with exit code $LASTEXITCODE" }
        }
        & $python.Source (Join-Path $Root 'tools/validate_test_results.py') $testResults
        if ($LASTEXITCODE -ne 0) { throw "TRX result validation failed with exit code $LASTEXITCODE" }
    } else {
        Write-Warning 'NOT RUN: No C# automated test project is present. A successful build is not a passing test suite.'
    }

    Write-Host '=== RESULT: ALL CHECKS REACHED IN THIS SCRIPT PASSED ==='
    Write-Host 'Important: this script does not replace interactive Windows acceptance tests for voice, native automation, AI provider behavior, cameras, or installer operations.'
}
catch {
    $Failure = $_
    Write-Host ("FAIL: " + $_.Exception.Message) -ForegroundColor Red
}
finally {
    if ($TranscriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
    Write-Host "Verification log: $LogPath"
}

if ($Failure) { exit 1 }
exit 0
