# Run app - build a signed MSIX, install it, and launch Evania
#
# Evania registers as a Microsoft Copilot hardware key provider, and providers must be
# signed - so this script always installs the packaged MSIX rather than registering the
# build output loose. Loose registrations and staged packages cannot replace each other,
# and the script handles both cases when an old install is in the way.
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
$certThumbprint = "E7BFA2284298F4307DE372E8543057925AEA76C7"

# Signing needs the private key in the user store, and install needs the public
# certificate trusted machine-wide. Reading both stores is allowed without elevation,
# so fail fast with instructions instead of a cryptic deployment error later.
if (-not (Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $certThumbprint)) {
    Write-Host "Signing certificate $certThumbprint is missing from Cert:\CurrentUser\My."
    Write-Host "Import it with: Import-PfxCertificate -FilePath Evania_TemporaryKey.pfx -CertStoreLocation Cert:\CurrentUser\My -Password ([SecureString]::new())"
    exit 1
}
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certThumbprint)) {
    Write-Host "Certificate $certThumbprint is not in Cert:\LocalMachine\TrustedPeople, so the signed MSIX cannot install."
    Write-Host "Install Evania_TemporaryKey.pfx via the certificate wizard: Local Machine, store 'Trusted People', no password."
    exit 1
}

# The exe is locked while the app runs, and the build copies over it - stop it up front
# rather than after ten retry warnings.
Get-Process Evania -ErrorAction SilentlyContinue | Stop-Process -Force

# Symbol package generation needs mspdbcmf.exe. Visual Studio ships it, but the bare
# dotnet CLI does not set VsInstallRoot, so the packaging targets cannot find it on
# their own and warn about it every run.
$pdbCmf = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -property installationPath
    if ($vsRoot) {
        $msvcRoot = Join-Path $vsRoot "VC\Tools\MSVC"
        if (Test-Path $msvcRoot) {
            $latestMsvc = Get-ChildItem $msvcRoot -Directory | Sort-Object { [Version]$_.Name } | Select-Object -Last 1
            $candidate = Join-Path $latestMsvc.FullName "bin\Hostx64\x64\mspdbcmf.exe"
            if (Test-Path $candidate) {
                $pdbCmf = $candidate
            }
        }
    }
}

Write-Host "Publishing signed package for $Platform ($Configuration)..."
$publishArgs = @($projectPath, "-c", $Configuration, "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$rid", "-p:GenerateAppxPackageOnBuild=true", "-p:AppxBundle=Never")
if ($pdbCmf) {
    $publishArgs += "-p:MsPdbCmfExeFullpath=$pdbCmf"
}
dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed."
    exit 1
}

# Packaging drops the main package and its scale resource packages into the build
# layout; the exact folder varies with how publish was invoked (bin, obj, or the
# project-root AppPackages the packaging targets default to), and a previous run's
# artifacts may still be lying around, so take the newest main package.
$candidates = Get-ChildItem -Path (Join-Path $PSScriptRoot "bin"), (Join-Path $PSScriptRoot "obj"), (Join-Path $PSScriptRoot "AppPackages") -Recurse -Filter "*.msix" -ErrorAction SilentlyContinue
$mainPackage = $candidates |
    Where-Object { $_.Name -notmatch '_scale-\d+' } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $mainPackage) {
    Write-Host "No .msix found under bin\ or obj\."
    exit 1
}
$resources = Get-ChildItem -Path $mainPackage.DirectoryName -Filter "*_scale-*.msix" -ErrorAction SilentlyContinue

Write-Host "Installing $($mainPackage.Name)..."
$install = @{ Path = $mainPackage.FullName }
if ($resources) {
    $install["DependencyPath"] = $resources.FullName
}

try {
    # Reinstalling the same version is refused by default; this flag is what makes a
    # developer-signed package replaceable by its own rebuild.
    Add-AppxPackage @install -ForceApplicationShutdown -ForceUpdateFromDevelopmentMode
}
catch {
    # A leftover loose registration cannot be upgraded to a staged package at all - the
    # only way forward is removing it first. Evania's data lives in %APPDATA%\Evania and
    # the picture/video libraries, so nothing user-visible is lost.
    Write-Host "In-place update failed; removing the existing registration and reinstalling."

    $existing = Get-AppxPackage -Name $packageId
    if ($existing) {
        Remove-AppxPackage -Package $existing.PackageFullName
    }

    Add-AppxPackage @install
}

$package = Get-AppxPackage -Name $packageId
if (-not $package) {
    Write-Host "Package installation failed."
    exit 1
}
Write-Host "Installed: $($package.PackageFullName) (SignatureKind: $($package.SignatureKind))"

Write-Host "Launching app..."
explorer.exe "shell:AppsFolder\$($package.PackageFamilyName)!$appId"
Write-Host "Done."
