@echo off
setlocal
cd /d "%~dp0"

set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 (
    if exist "%ProgramFiles%\dotnet\dotnet.exe" (
        set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
    ) else (
        echo [ShutaNote] .NET 8 SDK was not found.
        echo Install it from: https://dotnet.microsoft.com/download/dotnet/8.0
        pause
        exit /b 1
    )
)

echo [ShutaNote] Starting Debug build...
"%DOTNET%" run --project "%~dp0ShutaNote.csproj" --configuration Debug
set "EXIT_CODE=%ERRORLEVEL%"

if not "%EXIT_CODE%"=="0" (
    echo.
    echo [ShutaNote] Debug run failed with exit code %EXIT_CODE%.
    pause
)

exit /b %EXIT_CODE%
