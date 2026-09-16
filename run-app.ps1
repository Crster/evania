# Run app - build a signed MSIX, install it, and launch Evania
#
# Evania registers as a Microsoft Copilot hardware key provider, and providers must be
# signed - so this script always installs the packaged MSIX rather than registering the
# build output loose. The package is updated in place (the way Visual Studio deploys on
# F5), so settings and other package state survive every run. When nothing has changed
# since the last deploy, the build and install are skipped entirely and the installed
# app is just relaunched.
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

# The exe is locked while the app runs, and the build copies over it - stop it up front
# rather than after ten retry warnings.
Get-Process Evania -ErrorAction SilentlyContinue | Stop-Process -Force

# Hash the source tree (everything except build output and files that never ship) so a
# later run can tell whether the installed package is already up to date.
function Get-SourceStamp {
    $files = Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|AppPackages|\.git|\.zcode)(\\|$)' -and
            $_.Extension -notin ".log", ".md", ".slnx", ".user"
        }
    $stampInput = ($files | Sort-Object FullName | ForEach-Object {
        "{0}={1}" -f $_.FullName.Substring($PSScriptRoot.Length + 1), (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA1).Hash
    }) -join "`n"
    $stampStream = [System.IO.MemoryStream]::new([System.Text.Encoding]::UTF8.GetBytes($stampInput))
    return (Get-FileHash -InputStream $stampStream -Algorithm SHA1).Hash
}

$stampFile = Join-Path $PSScriptRoot "obj\deploy-stamp.$Configuration-$Platform.txt"
$currentStamp = Get-SourceStamp

$package = Get-AppxPackage -Name $packageId
if ((Test-Path $stampFile) -and (Get-Content $stampFile) -eq $currentStamp -and $package) {
    Write-Host "Sources unchanged since the last deploy - relaunching $($package.PackageFullName)."
    Start-Process (Join-Path $package.InstallLocation "Evania.exe")
    exit 0
}

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

# Windows refuses to update a package in place when the version is unchanged but the
# contents differ (0x80073CFB), and AppxAutoIncrementPackageRevision is a Visual Studio
# IDE feature - under plain dotnet publish the version stays pinned to the manifest.
# Bump the revision in Package.appxmanifest the way VS does on F5, so every run builds
# a higher version that replaces the installed package without touching its data.
$manifestPath = Join-Path $PSScriptRoot "Package.appxmanifest"
$manifestText = Get-Content $manifestPath -Raw
$bumpedManifest = [regex]::Replace($manifestText, '(<Identity\b[^>]*\bVersion=")(\d+\.\d+\.\d+\.\d+)(")', {
    param($match)
    $version = [Version]$match.Groups[2].Value
    '{0}{1}.{2}.{3}.{4}{5}' -f $match.Groups[1].Value,
        $version.Major, $version.Minor, $version.Build, ($version.Revision + 1), $match.Groups[3].Value
})
if ($bumpedManifest -eq $manifestText) {
    Write-Host "Could not find an Identity Version to bump in Package.appxmanifest."
    exit 1
}
Set-Content -Path $manifestPath -Value $bumpedManifest -NoNewline -Encoding UTF8

# Build the way Visual Studio deploys on F5: a plain incremental build that packs and
# signs the MSIX straight from the build layout. dotnet publish adds nothing for local
# debug deploys - it recopies the entire output into a publish\ folder and runs
# ReadyToRun crossgen on every assembly - so it is only used for Release, where those
# publishing features matter. MsPdbCmfExeFullpath is deliberately left unset: that
# skips the .appxsym symbol package, which only Store uploads need.
Write-Host "Building signed package for $Platform ($Configuration)..."
$buildArgs = @($projectPath, "-c", $Configuration, "-p:Platform=$Platform",
    "-p:RuntimeIdentifier=$rid", "-p:GenerateAppxPackageOnBuild=true", "-p:AppxBundle=Never")

# The packaging targets need mspdbcmf.exe from Visual Studio's VC tools to emit the
# .appxsym, and they warn when they cannot find it - the bare dotnet CLI does not set
# VsInstallRoot for their lookup. Point the build at the VS install when present.
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsRoot = & $vswhere -latest -property installationPath
    if ($vsRoot) {
        $buildArgs += "-p:VsInstallRoot=$vsRoot"
    }
}

if ($Configuration -eq "Debug") {
    # Debug layouts land in obj\appx instead of AppPackages so the release folder stays
    # clean; the package search below already scans obj.
    $buildArgs += "-p:AppxPackageDir=$(Join-Path $PSScriptRoot "obj\appx")\"
    dotnet build @buildArgs
} else {
    dotnet publish @buildArgs
}
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed."
    exit 1
}

# Packaging drops the main package and its scale resource packages into the build
# layout; the exact folder varies with how the build was invoked, and a previous run's
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

# Update the existing package in place instead of removing it first - Remove-AppxPackage
# deletes the package's application data (LocalState plus the virtualized %APPDATA%
# copy inside the MSIX container), which resets the user's settings on every deploy.
# -ForceUpdateFromAnyVersion also lets this deploy replace a newer installed package
# (for example one deployed from Visual Studio) without a remove.
$install = @{ Path = $mainPackage.FullName; ForceUpdateFromAnyVersion = $true }
if ($resources) {
    $install["DependencyPath"] = $resources.FullName
}

try {
    Add-AppxPackage @install
} catch {
    # A development-mode (loose) registration or staged package cannot be updated by a
    # signed MSIX - that is the one case where the old install has to be removed first,
    # losing package data. Everything else re-registers in place above.
    $existing = Get-AppxPackage -Name $packageId
    if (-not $existing) {
        throw
    }
    Write-Host "In-place update failed: $($_.Exception.Message)"
    Write-Host "Removing existing package $($existing.PackageFullName) and reinstalling..."
    Remove-AppxPackage -Package $existing.PackageFullName
    Add-AppxPackage @install
}

$package = Get-AppxPackage -Name $packageId
if (-not $package) {
    Write-Host "Package installation failed."
    exit 1
}
Write-Host "Installed: $($package.PackageFullName) (SignatureKind: $($package.SignatureKind))"

# Record the state that produced this deploy (recomputed after the version bump, so
# the stored hash matches the manifest as built) - the next unchanged run can then
# skip straight to relaunching. Also keep only the current debug layout: every
# versioned layout is a couple hundred megabytes of churn.
Set-Content -Path $stampFile -Value (Get-SourceStamp)
if ($Configuration -eq "Debug") {
    Get-ChildItem -Path (Join-Path $PSScriptRoot "obj\appx") -Directory -Filter "Evania_*" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -ne $mainPackage.DirectoryName } |
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
}

# Launch the exe directly from the build output so it runs as a regular process
# without MSIX file-system virtualization. The package stays installed for the
# Copilot key provider extension, but the running process writes to the real %APPDATA%.
$exePath = Join-Path $mainPackage.DirectoryName "Evania.exe"
if (-not (Test-Path $exePath)) {
    # Fall back to the package install location
    $exePath = Join-Path $package.InstallLocation "Evania.exe"
}
Write-Host "Launching $exePath..."
Start-Process $exePath
Write-Host "Done."
