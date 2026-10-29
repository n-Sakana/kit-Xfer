@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Xfer.ps1" %*
set "Xfer_EXIT=%ERRORLEVEL%"
if not "%Xfer_EXIT%"=="0" (
    echo.
    echo Xfer stopped with exit code %Xfer_EXIT%.
    pause
)
endlocal & exit /b %Xfer_EXIT%
