@echo off
setlocal enabledelayedexpansion

set CONFIG=Debug
set DAEMON=0

REM 解析命令行参数
:parse
if "%~1"=="" goto :run
if /i "%~1"=="-d" (
    set DAEMON=1
) else (
    set CONFIG=%~1
)
shift
goto :parse

:run
echo =================================
echo  NatSelect Demo Run (%CONFIG%)
echo =================================

if !DAEMON! equ 1 (
    echo Starting in background mode ^(daemon^)...
    echo Logs -^> logs\game-YYYYMMDD.log
    echo.
    REM 用 cmd /c 启动，由 cmd 解析重定向（外层用 ^ 转义）
    start "" /B cmd /c dotnet run --project "%~dp0." -c !CONFIG! ^> nul 2^>^&1
    echo Process started ^(PID: started in background^)
    echo Use stop.bat to shut down gracefully.
    exit /b 0
)

echo Press Ctrl+C to stop gracefully (or use stop.bat^)
echo.
dotnet run --project "%~dp0." -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Run FAILED
    exit /b 1
)
