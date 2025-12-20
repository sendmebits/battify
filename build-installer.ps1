# Build Installer Script for Windows
# Requires Inno Setup to be installed (https://jrsoftware.org/isinfo.php)
# Run this from the project root directory

param(
    [string]$Version = "1.0.2"
)

$ErrorActionPreference = "Stop"

Write-Host "=== Battify Installer Build ===" -ForegroundColor Cyan
Write-Host "Version: $Version" -ForegroundColor Yellow

# Close any running Battify instances
Write-Host "`nClosing any running Battify instances..."
$processes = Get-Process -Name "Battify" -ErrorAction SilentlyContinue
if ($processes) {
    $processes | Stop-Process -Force
    Start-Sleep -Seconds 1
    Write-Host "Closed running Battify process" -ForegroundColor Yellow
}

# Update version in setup.iss
Write-Host "`nUpdating version in setup.iss..."
$setupPath = "installer\setup.iss"
$content = Get-Content $setupPath -Raw
$content = $content -replace '#define MyAppVersion ".*"', "#define MyAppVersion `"$Version`""
Set-Content $setupPath $content

# Update version in .csproj
Write-Host "Updating version in Battify.csproj..."
$csprojPath = "Battify.csproj"
$csproj = Get-Content $csprojPath -Raw
$csproj = $csproj -replace '<Version>.*</Version>', "<Version>$Version</Version>"
$csproj = $csproj -replace '<AssemblyVersion>.*</AssemblyVersion>', "<AssemblyVersion>$Version</AssemblyVersion>"
$csproj = $csproj -replace '<FileVersion>.*</FileVersion>', "<FileVersion>$Version</FileVersion>"
Set-Content $csprojPath $csproj

# Clean previous build output
if (Test-Path "Battify-Standalone") {
    Write-Host "Cleaning previous standalone build..."
    Remove-Item -Recurse -Force "Battify-Standalone" -ErrorAction SilentlyContinue
}

# Build standalone first
Write-Host "`nBuilding standalone executable..."
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true -o Battify-Standalone

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

# Find Inno Setup
$isccPaths = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)

$iscc = $null
foreach ($path in $isccPaths) {
    if (Test-Path $path) {
        $iscc = $path
        break
    }
}

if (-not $iscc) {
    Write-Host "Inno Setup not found! Please install from https://jrsoftware.org/isinfo.php" -ForegroundColor Red
    exit 1
}

Write-Host "`nBuilding installer with Inno Setup..."
& $iscc "installer\setup.iss"

if ($LASTEXITCODE -ne 0) {
    Write-Host "Installer build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "`n=== Build Complete ===" -ForegroundColor Green
Write-Host "Installer output: installer-output\Battify-Setup-$Version.exe" -ForegroundColor Yellow

# Show file size
$installerPath = "installer-output\Battify-Setup-$Version.exe"
if (Test-Path $installerPath) {
    $size = (Get-Item $installerPath).Length / 1MB
    Write-Host "Installer size: $([math]::Round($size, 2)) MB" -ForegroundColor Yellow
}
