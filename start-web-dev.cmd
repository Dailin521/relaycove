@echo off
setlocal
chcp 65001 >nul

set "RELAYCOVE_ROOT=%~dp0"
set "RELAYCOVE_WEB=%RELAYCOVE_ROOT%src\RelayCove.Web"

if not exist "%RELAYCOVE_WEB%\package.json" (
    echo [RelayCove] 找不到 Web 工程：%RELAYCOVE_WEB%
    pause
    exit /b 1
)

if not exist "%RELAYCOVE_WEB%\node_modules\.bin\vite.cmd" (
    echo [RelayCove] Web 依赖尚未准备。请先在以下目录执行 npm ci：
    echo %RELAYCOVE_WEB%
    pause
    exit /b 1
)

where pwsh.exe >nul 2>nul
if %errorlevel% equ 0 (
    start "" pwsh.exe -NoProfile -WindowStyle Hidden -File "%RELAYCOVE_ROOT%scripts\open-web-preview.ps1"
) else (
    start "" powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "%RELAYCOVE_ROOT%scripts\open-web-preview.ps1"
)

cd /d "%RELAYCOVE_WEB%"
echo [RelayCove] 正在启动本地 Web UI...
echo [RelayCove] 浏览器入口：http://127.0.0.1:5173/
echo [RelayCove] 此入口连接真实 Zulip Realm；测试 fixture 不作为日常入口。
echo [RelayCove] 停止服务请按 Ctrl+C。
echo.
call npm.cmd run dev

if %errorlevel% neq 0 (
    echo.
    echo [RelayCove] 本地 Web 服务启动失败，请查看上方错误。
    pause
)
