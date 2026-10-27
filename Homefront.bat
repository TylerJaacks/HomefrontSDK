@echo off
rem Starts the Homefront client (HOMEFRONT\Binaries, real Steam: your account) for testing on the local server.
rem A second copy started while the first is still loading can crash.
tasklist /FI "IMAGENAME eq HOMEFRONT.exe" 2>nul | "%SystemRoot%\System32\find.exe" /I "HOMEFRONT.exe" >nul
if not errorlevel 1 goto already_running

rem -ALLOWMULTIPLE: without it the client won't start while a dedicated server runs on this PC.
start "" /D "%~dp0..\HOMEFRONT\Binaries" "%~dp0..\HOMEFRONT\Binaries\HOMEFRONT.exe" -ALLOWMULTIPLE
echo Starting Homefront. The window takes about a minute to appear.
timeout /t 5 /nobreak >nul
exit /b 0

:already_running
echo Homefront is already running (it can take a minute before its window appears).
pause
exit /b 1
