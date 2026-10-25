@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)

where pwsh >nul 2>nul
if not errorlevel 1 (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "tools\Build-LayalDistribution.ps1"
  if errorlevel 1 goto failed
  goto success
)

where powershell >nul 2>nul
if errorlevel 1 (
  echo PowerShell was not found. Install PowerShell 7 or enable Windows PowerShell.
  pause
  exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "tools\Build-LayalDistribution.ps1"
if errorlevel 1 goto failed

:success
echo.
echo Distribution files were generated under:
echo %CD%\artifacts\distribution
echo.
echo IMPORTANT: review Layal-*-release-status.txt. The generated package is unsigned,
echo and SBOM generation does not certify license compliance or manual Windows acceptance.
pause
exit /b 0

:failed
echo.
echo Distribution build failed. Review the error above.
pause
exit /b 1
