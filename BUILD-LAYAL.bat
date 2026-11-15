@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)

set "PUBLISH_DIR=artifacts\publish\win-x64"
set "AUDIT_ARGS="
if "%LAYAL_ALLOW_OFFLINE_NUGET_AUDIT_BYPASS%"=="1" (
  echo WARNING: NuGet vulnerability auditing is disabled for this local build only.
  set "AUDIT_ARGS=-p:NuGetAudit=false"
)

echo Restoring packages for win-x64...
dotnet restore "src\Layal.Desktop\Layal.Desktop.csproj" --runtime win-x64 --ignore-failed-sources %AUDIT_ARGS%
if errorlevel 1 goto failed

echo Publishing self-contained x64 Release build...
dotnet publish "src\Layal.Desktop\Layal.Desktop.csproj" -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=false -o "%PUBLISH_DIR%" --nologo
if errorlevel 1 goto failed

echo.
echo Verifying the bundled .NET runtime...
for %%F in (Layal.exe Layal.dll Layal.runtimeconfig.json coreclr.dll hostfxr.dll) do (
  if not exist "%PUBLISH_DIR%\%%F" (
    echo Missing required self-contained runtime file: %%F
    goto failed
  )
)

echo.
echo Self-contained publish completed:
echo %CD%\%PUBLISH_DIR%
echo The published app does not require a separately installed .NET Desktop Runtime.
pause
exit /b 0

:failed
echo.
echo Build failed. Review the error above. No .NET runtime was installed or removed.
pause
exit /b 1
