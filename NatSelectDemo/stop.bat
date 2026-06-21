@echo off
echo =================================
echo  NatSelect Stop
echo =================================
echo Sending shutdown signal...

powershell -NoProfile -Command "$e = [System.Threading.EventWaitHandle]::OpenExisting('NatSelect_Shutdown'); $e.Set(); $e.Close(); Write-Host 'Signal sent. Server is shutting down gracefully.'"

if errorlevel 1 (
    echo.
    echo FAILED - Is the NatSelect server running?
    exit /b 1
)
