@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "SPT=%~1"
if not defined SPT set /p "SPT=Drag your SPT install folder here (or paste the path) and press Enter: "
set "SPT=%SPT:"=%"
if not defined SPT goto :nopath
if "%SPT:~-1%"=="\" set "SPT=%SPT:~0,-1%"

if not exist "%SPT%\EscapeFromTarkov_Data\Managed\UnityEngine.dll" goto :nomanaged
if not exist "%SPT%\BepInEx\core\BepInEx.dll" goto :nobepinex

powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=[Security.SecurityElement]::Escape($env:SPT); [IO.File]::WriteAllText([IO.Path]::Combine($PWD.Path,'SptPath.props'), '<Project><PropertyGroup><SptDir>'+$p+'</SptDir></PropertyGroup></Project>')"
if errorlevel 1 goto :writefail

echo.
echo SPT folder saved: %SPT%
echo (written to SptPath.props, which is git-ignored)
echo Now run Build.bat
goto :end

:nopath
echo.
echo [ERROR] No folder given.
goto :fail

:nomanaged
echo.
echo [ERROR] Could not find EscapeFromTarkov_Data\Managed\UnityEngine.dll in:
echo         %SPT%
echo This should be the folder that contains EscapeFromTarkov.exe.
goto :fail

:nobepinex
echo.
echo [ERROR] Could not find BepInEx\core\BepInEx.dll in:
echo         %SPT%
echo Run the SPT install / launcher once so BepInEx is set up, then try again.
goto :fail

:writefail
echo.
echo [ERROR] Could not write SptPath.props.
goto :fail

:fail
pause
exit /b 1

:end
endlocal
exit /b 0
