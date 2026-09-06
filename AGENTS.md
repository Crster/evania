# AGENTS.md

## Scope

This repository contains a Windows desktop app built with .NET 10, WinUI 3, and Windows App SDK.
Use the existing app structure and conventions in this repository; do not introduce new frameworks or large architectural changes unless the task requires it.

## Key areas

- `App.xaml.cs` - startup, activation, single-instance behavior
- `Windows/` - shell windows and navigation host
- `Views/` - page UI and page lifecycle code
- `ViewModels/` - UI-facing state and commands
- `Services/` - settings, storage, capture, tray, startup, and environment helpers
- `Controls/` - custom control code, especially the markdown editor

## Working rules

- Keep changes minimal and targeted to the request.
- Prefer the existing MVVM and service patterns already used in the codebase.
- Reuse existing services when adding behavior that fits them.
- Avoid adding debugging traces or temporary diagnostic code.
- Reuse shared resources when possible to reduce startup cost and memory use.
- For navigation pages that should persist, prefer page caching instead of rebuilding the page on every navigation.

## Build and validation

- Use the solution build to validate changes.
- Prefer `run_build` for compilation checks when working in the workspace.
- If a change affects a specific file first, use targeted error checks before broader validation.
- For feature work that changes UI behavior, verify the relevant page or view model still compiles cleanly.

## Packaging and local install

- The app is packaged as an MSIX.
- Use `run-app.ps1` for local install and launch when packaging behavior matters.
- Keep signing and packaging assumptions aligned with the existing MSIX workflow.

## Notes

- Settings and data are persisted in `%APPDATA%\Evania` (outside the MSIX container, survives uninstall).
- Chat uses a shared WebView2 user data folder so provider sign-in survives across sessions.
- The app includes startup task and Copilot key integration; be careful not to break activation or single-instance routing.