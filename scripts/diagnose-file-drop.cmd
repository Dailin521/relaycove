@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0diagnose-file-drop.ps1"
set "diagnosticExit=%ERRORLEVEL%"
if not "%diagnosticExit%"=="0" echo Diagnostic failed. Keep the error shown above.
pause
exit /b %diagnosticExit%
