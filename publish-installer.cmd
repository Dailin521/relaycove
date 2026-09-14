@echo off
setlocal
chcp 65001 >nul
title RichChat 安装包发布

pushd "%~dp0"
if errorlevel 1 goto failed
set "RELAYCOVE_PUSHED=1"

where pwsh.exe >nul 2>nul
if errorlevel 1 (
    echo 未找到 PowerShell 7，请先安装后重试。
    goto failed
)
where dotnet.exe >nul 2>nul
if errorlevel 1 (
    echo 未找到 .NET SDK，请先安装工程要求的 SDK 和 MAUI Windows 工作负载。
    goto failed
)

if defined RELAYCOVE_ISCC goto compiler_ready
set "RELAYCOVE_ISCC=%~dp0.verify\installer\tools\inno-6.5.4\ISCC.exe"
if exist "%RELAYCOVE_ISCC%" goto compiler_ready
set "RELAYCOVE_ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%RELAYCOVE_ISCC%" goto compiler_ready
set "RELAYCOVE_ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%RELAYCOVE_ISCC%" goto compiler_ready
set "RELAYCOVE_ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

:compiler_ready
if not exist "%RELAYCOVE_ISCC%" (
    echo 未找到 Inno Setup 编译器，请安装 Inno Setup 6.5.4。
    echo 自定义路径可通过环境变量 RELAYCOVE_ISCC 指向 ISCC.exe。
    goto failed
)

echo [1/3] 还原 Release 自包含运行时依赖...
dotnet.exe restore RelayCove.sln -p:Configuration=Release -p:SelfContained=true -p:WindowsAppSDKSelfContained=true --nologo --verbosity minimal
if errorlevel 1 goto failed

echo [2/3] 运行 Full 验证并生成自包含 ZIP...
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\verify.ps1" -Mode Full
if errorlevel 1 goto failed

echo [3/3] 生成中文 EXE 安装器及 SHA-256 校验文件...
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\package-installer.ps1" -IsccPath "%RELAYCOVE_ISCC%"
if errorlevel 1 goto failed

echo.
echo 安装包生成成功。输出目录："%~dp0artifacts\package"
popd
if /i not "%~1"=="--no-pause" pause
exit /b 0

:failed
echo.
echo 安装包生成失败，请查看上方错误。修复后可重新运行此命令。
if defined RELAYCOVE_PUSHED popd
if /i not "%~1"=="--no-pause" pause
exit /b 1
