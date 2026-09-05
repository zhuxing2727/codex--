@echo off
cd /d "%~dp0"
set "NODE_EXE=node.exe"
if exist "%~dp0runtime\node.exe" set "NODE_EXE=%~dp0runtime\node.exe"
where node.exe >nul 2>nul
if not exist "%~dp0runtime\node.exe" if errorlevel 1 if exist "%ProgramFiles%\nodejs\node.exe" set "NODE_EXE=%ProgramFiles%\nodejs\node.exe"
if "%NODE_EXE%"=="node.exe" where node.exe >nul 2>nul
if errorlevel 1 if not exist "%~dp0runtime\node.exe" if not exist "%ProgramFiles%\nodejs\node.exe" (echo Node.js runtime not found. & exit /b 1)
powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "$c=New-Object Net.Sockets.TcpClient; try { $c.Connect('127.0.0.1',17891); exit 0 } catch { exit 1 } finally { $c.Dispose() }"
if errorlevel 1 (
  start "Ergouzi Account Agent" /b "%NODE_EXE%" "%~dp0ergouzi-account-agent.mjs"
  timeout /t 1 /nobreak >nul
)
start "Ergouzi Wallet Token Sync" /b "%NODE_EXE%" "%~dp0ergouzi-wallet-token-sync.mjs"
start "DeepSeek Whale Overlay" powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0whale-overlay.ps1"
exit /b 0
