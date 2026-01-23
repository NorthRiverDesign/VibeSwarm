@echo off
REM VibeSwarm Publish Script for Windows
REM This script publishes the application in Release mode

setlocal

set PROJECT_FILE=%~dp0src\VibeSwarm.Web\VibeSwarm.Web.csproj
set OUTPUT_DIR=%~dp0build

echo ========================================
echo   VibeSwarm - Publishing for Pi
echo ========================================
echo.

REM Check if project file exists
if not exist "%PROJECT_FILE%" (
    echo Error: Project file not found at %PROJECT_FILE%
    exit /b 1
)

REM Clean previous build
if exist "%OUTPUT_DIR%" (
    echo Cleaning previous build...
    rmdir /s /q "%OUTPUT_DIR%"
)

REM Publish the application
echo Publishing application in Release mode...
echo.

dotnet publish "%PROJECT_FILE%" -c Release -o "%OUTPUT_DIR%" --self-contained false

if %ERRORLEVEL% equ 0 (
    echo.
    echo ========================================
    echo   Build completed successfully!
    echo ========================================
    echo.
    echo Published to: %OUTPUT_DIR%
    echo.
    echo To deploy to Raspberry Pi:
    echo   1. Copy the build folder to your Pi
    echo   2. Run: ./start-vibeswarm.sh
    echo.
) else (
    echo.
    echo ========================================
    echo   Build failed!
    echo ========================================
    exit /b 1
)

endlocal
