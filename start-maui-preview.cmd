@echo off
setlocal
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\start-maui-preview.ps1" -Scene shell
if errorlevel 1 pause
