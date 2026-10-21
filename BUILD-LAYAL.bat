@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET SDK was not found. Install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  pause
  exit /b 1
)
echo Restoring packages...
dotnet restore "src\Layal.Desktop\Layal.Desktop.csproj"
if errorlevel 1 goto failed
echo Publishing x64 Release build...
dotnet publish "src\Layal.Desktop\Layal.Desktop.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o "artifacts\publish\win-x64"
if errorlevel 1 goto failed
echo.
echo Publish completed. Verify the output folder before distribution:
echo %CD%\artifacts\publish\win-x64
pause
exit /b 0
:failed
echo Build failed. Review the errors above.
pause
exit /b 1
