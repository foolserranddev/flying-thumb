@echo off
setlocal
cd /d "%~dp0"
title Flying Thumb Firmware Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Flash-FlyingThumb.ps1"
set "FLASH_RESULT=%ERRORLEVEL%"
echo.
if not "%FLASH_RESULT%"=="0" (
  echo The installer did not complete successfully.
  echo Please leave this window open when asking for help.
)
pause
exit /b %FLASH_RESULT%
