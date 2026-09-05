# Evania

Evania is a Windows desktop app built with .NET 10, WinUI 3, and Windows App SDK.
It combines several utility features in one shell:

- Chat page with WebView2-based AI provider access
- Notes
- Screenshots
- Screen recording
- Password management
- Bookmarks
- Tray icon support
- Startup task support
- Copilot key integration

## Requirements

- Windows 11 version 22H2 or later
- Visual Studio Community 2026 or newer with the Windows App SDK workload
- .NET 10 SDK
- A trusted signing certificate for local MSIX installs

## Repository layout

- `Evania.csproj` - main WinUI project
- `App.xaml.cs` - app startup and single-instance behavior
- `Views/` - XAML pages for the app shell
- `ViewModels/` - page and navigation view models
- `Services/` - app services, persistence, capture, tray, and startup logic
- `Controls/` - custom controls, including the markdown editor
- `Assets/` - icons and package artwork
- `run-app.ps1` - build, install, and launch helper for local testing

## Build

### Visual Studio

Open `Evania.slnx` and build the solution in Visual Studio.

### Command line

```powershell
dotnet build
```

## Run locally

Use the helper script to publish, install, and launch the signed MSIX:

```powershell
.\run-app.ps1
```

You can also choose a platform:

```powershell
.\run-app.ps1 -Platform x64 -Configuration Debug
```

The script expects the signing certificate to be available in the current user store and trusted on the local machine.

## Production notes

- App settings are stored under `%APPDATA%\Evania`.
- The chat page uses a shared WebView2 environment so provider sign-in persists between launches.
- The app is designed to reuse the chat page across navigation instead of recreating it.
- Screenshots, recordings, webcam access, and graphics capture require the related Windows capabilities and permissions.

## Troubleshooting

- If package installation fails, make sure the signing certificate is trusted.
- If the app does not start correctly after installing, reinstall from a clean package build using `run-app.ps1`.
- If a feature needs camera, microphone, or screen capture access, verify Windows privacy settings.

## License

No license file is included in this repository.