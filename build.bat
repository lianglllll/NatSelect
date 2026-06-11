@echo off
setlocal

set CONFIG=%1
if "%CONFIG%"=="" set CONFIG=Debug

echo =================================
echo  NatSelect Build (%CONFIG%)
echo =================================

dotnet build "%~dp0NatSelect.sln" -c %CONFIG% --nologo
if errorlevel 1 (
    echo.
    echo Build FAILED
    exit /b 1
)

echo.
echo Build SUCCEEDED
