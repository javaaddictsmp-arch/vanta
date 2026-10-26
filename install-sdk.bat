@echo off
REM Installs the official .NET 8 SDK from Microsoft via winget. Approve the Windows admin (UAC) prompt when it appears.
cd /d "%~dp0"
echo Install started %date% %time% > install.log
winget install --id Microsoft.DotNet.SDK.8 -e --accept-package-agreements --accept-source-agreements >> install.log 2>&1
echo WINGET EXITCODE %errorlevel% >> install.log
"C:\Program Files\dotnet\dotnet.exe" --list-sdks >> install.log 2>&1
echo DONE >> install.log
echo.
echo Finished. You can close this window.
timeout /t 8 >nul
