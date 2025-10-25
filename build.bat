@echo off
echo Building Battify - Bluetooth Battery Monitor...
echo.

REM Clean previous builds
dotnet clean

REM Restore packages
echo Restoring NuGet packages...
dotnet restore
if %ERRORLEVEL% neq 0 (
    echo Failed to restore packages
    exit /b 1
)

REM Build the project
echo Building project...
dotnet build --configuration Release
if %ERRORLEVEL% neq 0 (
    echo Build failed
    exit /b 1
)

echo.
echo Build completed successfully!
echo Executable location: bin\Release\net8.0-windows10.0..XXXXX.0\Battify.exe
echo.
echo To run the application:
echo   cd bin\Release\net8.0-windows10.0.XXXXX.0
echo   Battify.exe
echo.
pause