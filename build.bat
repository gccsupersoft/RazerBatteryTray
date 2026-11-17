@echo off
chcp 65001 >nul
cd /d "%~dp0"
echo ============================================
echo   RazerBatteryTray 一键编译脚本 (Windows)
echo ============================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [错误] 编译失败，请检查上方报错信息。
    pause
    exit /b %ERRORLEVEL%
)
echo.
echo [成功] 已生成单文件程序: "%~dp0RazerBatteryTray.exe"
pause
