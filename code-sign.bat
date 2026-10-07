@echo off
setlocal EnableExtensions
title ReVue Code Signing

pushd "%~dp0"
if errorlevel 1 (
    echo ERROR: Could not open the ReVue project directory.
    echo.
    pause
    exit /b 1
)

echo Signing every executable in "%CD%\dist"...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign-artifacts.ps1" -SignDistExecutables
set "exitCode=%errorlevel%"

echo.
if "%exitCode%"=="0" (
    echo SUCCESS: Every executable in dist was signed and verified.
) else (
    echo ERROR: Code signing failed with exit code %exitCode%.
)
echo.
pause

popd
exit /b %exitCode%
