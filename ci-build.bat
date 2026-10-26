@echo off
REM Logs a Debug build to build.log so errors can be read back. Safe to run any time.
cd /d "%~dp0"
echo Build started %date% %time% > build.log
echo --- where dotnet --- >> build.log
where dotnet >> build.log 2>&1
echo --- sdks --- >> build.log
dotnet --list-sdks >> build.log 2>&1
echo --- build --- >> build.log
dotnet build TuneUp.csproj -c Debug -nologo -v minimal >> build.log 2>&1
echo EXITCODE %errorlevel% >> build.log
echo DONE >> build.log
echo.
echo Finished. You can close this window.
timeout /t 5 >nul
