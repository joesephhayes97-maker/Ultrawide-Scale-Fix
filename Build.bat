@echo off
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 goto :nodotnet

if exist SptPath.props goto :build
call SetSptPath.bat
if errorlevel 1 exit /b 1

:build
dotnet build -c Release
if errorlevel 1 goto :failed
echo.
echo Done. Launch SPT and open the F12 menu to find the HayesUltrawiderescale settings.
pause
exit /b 0

:nodotnet
echo [ERROR] The .NET SDK was not found. Install it from https://dotnet.microsoft.com/download and try again.
pause
exit /b 1

:failed
echo.
echo Build failed.
pause
exit /b 1
