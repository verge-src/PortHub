@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
    echo .NET SDK 10 ne naiden. Ustanovite SDK s https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)

dotnet publish "src\PortHub\PortHub.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish\PortHub-win-x64"
if errorlevel 1 (
    echo.
    echo Oshibka sborki.
    pause
    exit /b 1
)
echo.
echo Gotovo: publish\PortHub-win-x64\PortHub.exe
pause
