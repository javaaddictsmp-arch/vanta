@echo off
REM Builds Vanta into a single self-contained EXE.
REM Requires the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0)
cd /d "%~dp0"
dotnet publish TuneUp.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
if errorlevel 1 (
  echo.
  echo BUILD FAILED - copy the error text above and send it over.
  pause
  exit /b 1
)
echo.
echo Done. Your EXE: %~dp0publish\Vanta.exe
pause
