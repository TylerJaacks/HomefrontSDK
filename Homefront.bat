@echo off
rem Starts the Homefront client (game\Binaries, Goldberg Steam emulator) for testing on the local server.
rem Real Steam must be closed: if it runs, the game hands itself over to Steam and your account.
tasklist /FI "IMAGENAME eq steam.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "steam.exe" >nul
if not errorlevel 1 goto steam_running
rem A second copy started while the first is still loading can crash.
tasklist /FI "IMAGENAME eq HOMEFRONT.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "HOMEFRONT.exe" >nul
if not errorlevel 1 goto already_running

rem -ALLOWMULTIPLE: without it the client won't start while a dedicated server runs on this PC.
start "" /D "%~dp0..\game\Binaries" "%~dp0..\game\Binaries\HOMEFRONT.exe" -ALLOWMULTIPLE
echo Starting Homefront. The window takes about a minute to appear.
timeout /t 5 /nobreak >nul
exit /b 0

:steam_running
echo Steam is running. Exit Steam completely (Steam menu - Exit), then run this again.
pause
exit /b 1

:already_running
echo Homefront is already running (it can take a minute before its window appears).
pause
exit /b 1
