# Run app - build, deploy, and launch Evania
param(
    [ValidateSet("x86", "x64", "ARM64")]
    [string]$Platform = "x64",
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectPath = Join-Path $PSScriptRoot "Evania.csproj"
$rid = "win-$Platform"
$packageId = "6d49ca0a-c00b-4ae7-8f12-1e03f16b71c4"
$appId = "App"

Write-Host "Building for $Platform..."
dotnet build $projectPath -c $Configuration -p:Platform=$Platform -p:RuntimeIdentifier=$rid -p:AppxBundle=Never
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed."
    exit 1
}

$binRoot = Join-Path $PSScriptRoot "bin\$Platform\$Configuration"
$manifest = Get-ChildItem -Path $binRoot -Recurse -Filter "AppxManifest.xml" -ErrorAction SilentlyContinue |
    Select-Object -First 1
if (-not $manifest) {
    Write-Host "AppxManifest.xml not found under $binRoot."
    exit 1
}

if (-not (Get-Command Add-AppxPackage -ErrorAction SilentlyContinue)) {
    Write-Host "Add-AppxPackage is not available. Run this script with Windows PowerShell 5.1."
    exit 1
}

Write-Host "Deploying package..."
Add-AppxPackage -Register -Path $manifest.FullName -ForceApplicationShutdown

$package = Get-AppxPackage -Name $packageId
if (-not $package) {
    Write-Host "Package deployment failed."
    exit 1
}

Write-Host "Launching app..."
explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!$appId"
Write-Host "Done."
