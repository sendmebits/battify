@echo off
setlocal

echo ========================================
echo Battify - Standalone Build Script
echo ========================================
echo.

REM Clean previous builds
echo [1/4] Cleaning previous builds...
if exist "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish" (
    rmdir /s /q "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
)
if exist "Battify-Standalone" (
    rmdir /s /q "Battify-Standalone"
)
echo Done.
echo.

REM Build the standalone application (self-contained, multiple files, smaller)
echo [2/4] Building standalone application...
echo Building self-contained deployment...
dotnet publish -c Release -r win-x64 --self-contained true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en
if %errorlevel% neq 0 (
    echo ERROR: Build failed!
    pause
    exit /b %errorlevel%
)
echo Done.
echo.

REM Create distribution folder
echo [3/4] Creating distribution package...
mkdir "Battify-Standalone"
xcopy /E /I /Y "bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\*" "Battify-Standalone\"
if exist "README.md" copy "README.md" "Battify-Standalone\"

REM Clean up any .pdb files that might have been copied
del /Q "Battify-Standalone\*.pdb" 2>nul
echo Done.
echo.

REM Clean up build artifacts
echo [4/5] Cleaning up build artifacts...
if exist "bin" (
    rmdir /s /q "bin"
)
if exist "obj" (
    rmdir /s /q "obj"
)
echo Done.
echo.

REM Display results
echo [5/5] Build complete!
echo.
echo ========================================
echo Standalone package created:
echo   Battify-Standalone\
echo.
echo Build artifacts cleaned up:
echo   - bin\ (removed)
echo   - obj\ (removed)
echo ========================================
echo.
echo Package size:
for /f "tokens=3" %%a in ('dir /s "Battify-Standalone" ^| find "File(s)"') do set size=%%a
echo Total: %size% bytes
echo.
dir "Battify-Standalone\Battify.exe" | find "Battify.exe"
echo.
echo This package includes the .NET runtime
echo and can run on any Windows 10+ machine.
echo.
pause
