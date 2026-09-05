@echo off
cd /d "%~dp0"
set "NODE_EXE=node.exe"
if exist "%~dp0runtime\node.exe" set "NODE_EXE=%~dp0runtime\node.exe"
where node.exe >nul 2>nul
if not exist "%~dp0runtime\node.exe" if errorlevel 1 if exist "%ProgramFiles%\nodejs\node.exe" set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
if "%NODE_EXE%"=="node.exe" where node.exe >nul 2>nul
if errorlevel 1 if not exist "%~dp0runtime\node.exe" if not exist "%ProgramFiles%\nodejs\node.exe" (echo Node.js runtime not found. & exit /b 1)
start "Ergouzi Account Agent" /b "%NODE_EXE%" "%~dp0ergouzi-account-agent.mjs"
