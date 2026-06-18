@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

echo =================================
echo  NatSelect Run (%CONFIG%)
echo =================================

if "%2"=="-d" (
    echo Starting in background mode (daemon)...
    echo Logs will be written to NatSelect\logs\
    start "NatSelect Server" /D "%~dp0NatSelect" dotnet run --project . -c %CONFIG%
    echo Process started. Check logs in NatSelect\logs\
    exit /b 0
)

echo Press Ctrl+C to stop gracefully
echo.
dotnet run --project "%~dp0NatSelect" -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Run FAILED
    exit /b 1
)
