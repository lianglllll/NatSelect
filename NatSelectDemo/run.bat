@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

echo =================================
echo  NatSelect Demo Run (%CONFIG%)
echo =================================

if "%2"=="-d" (
    echo Starting in background mode ^(daemon^)...
    echo Logs will be written to bin\%CONFIG%\net10.0\logs\
    start "NatSelect Server" /D "%~dp0." dotnet run --project . -c %CONFIG%
    echo Process started. Check logs in bin\%CONFIG%\net10.0\logs\
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
@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

echo =================================
echo  NatSelect Demo Run (%CONFIG%)
echo =================================

if "%2"=="-d" (
    echo Starting in background mode (daemon)...
    echo Logs will be written to bin\%CONFIG%\net10.0\logs\
    start "NatSelect Server" /D "%~dp0." dotnet run --project . -c %CONFIG%
    echo Process started. Check logs in bin\%CONFIG%\net10.0\logs\
    exit /b 0
)

echo Press Ctrl+C to stop gracefully (or use stop.bat)
echo.
dotnet run --project "%~dp0." -c %CONFIG%
if errorlevel 1 (
    echo.
    echo Run FAILED
    exit /b 1
)
