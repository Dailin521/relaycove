@echo off
setlocal
chcp 65001 >nul

cd /d "%~dp0"
where pwsh.exe >nul 2>nul
if %errorlevel% equ 0 (
    pwsh.exe -NoProfile -File "%~dp0scripts\deploy-web.ps1"
) else (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\deploy-web.ps1"
)

if %errorlevel% neq 0 (
    echo.
    echo [RelayCove] Web 一键部署失败，请查看上方错误；服务器当前版本不会被静默覆盖。
    pause
    exit /b 1
)

echo.
echo [RelayCove] Web 大版本已同步到固定入口：
echo https://hklight.2000521.xyz/relaycove-web/
start "" "https://hklight.2000521.xyz/relaycove-web/"
pause
