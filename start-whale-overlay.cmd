@echo off
cd /d "%~dp0"
if exist "%~dp0ErgouziWhaleWidget.exe" (
  start "" /b "%~dp0ErgouziWhaleWidget.exe"
  exit /b 0
)
echo ErgouziWhaleWidget.exe is missing. Reinstall the widget package.
exit /b 1
