@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK 10 ne naiden. Ustanovite SDK s https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

dotnet run --project "src\PortHub\PortHub.csproj"
if errorlevel 1 (
    echo.
    echo Oshibka zapuska. Smotrite soobsheniya vyshe.
    pause
)
