@echo off
setlocal EnableExtensions

pushd "%~dp0" || exit /b 1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0sign-artifacts.ps1" -SignDistExecutables
set "exitCode=%errorlevel%"

popd
exit /b %exitCode%
