# Monitor Switcher

Open-source Windows Forms utility for switching monitor profiles without a separate helper application.

Source repository: https://github.com/Ci303/monitor-switcher-native

## What It Does

- Detects arbitrary numbers of active, disabled, added and disconnected monitors using Windows display APIs.
- Shows each saved monitor with a stable alias, connection state and quick enable or disable action.
- Saves and restores named layouts, including position, resolution, orientation and primary display.
- Restores a selected profile after a physical reconnection or at sign-in when the monitor set matches safely.
- Supports a preferred primary display, fallback primary display, dark mode, always-on-top and notification-area operation.
- Opens saved monitor registry keys and records recent display actions for diagnosis.

Monitor Switcher does not download, install or execute a third-party monitor utility. Detection and topology changes use Windows CCD (`QueryDisplayConfig` and `SetDisplayConfig`) directly.

## Requirements

- Windows 10 or Windows 11
- GitHub release zip: no separate .NET installation required
- Local development: .NET 8 SDK

The application is designed for an extended desktop. Cloned or mirrored paths are rejected because they do not provide one unique desktop source per monitor. Display drivers, docks, KVM switches and DisplayLink devices can expose unstable or duplicate identities; ambiguous matches fail closed instead of changing an unrelated display.

## Detection and Identity

Monitor Switcher scans displays at start-up, when Windows reports a display or device change, and when **Refresh** is selected. Windows CCD supplies the active and available paths. Device-interface paths and reliable EDID serials are used for exact matching, so saved aliases can follow a physical display when Windows changes `\\.\DISPLAYn` names. Displays that report missing or duplicate identities fail closed where a physical match cannot be proved.

Disconnected hardware cannot always be distinguished from a driver-retained inactive path. The application therefore records the last known display and will only perform an automatic restore when the complete current set can be matched reliably. Inconclusive state is shown as unavailable and no topology change is attempted.

## Settings

Settings includes:

- Editable monitor aliases.
- Preferred and fallback primary display selection.
- Dark mode and always-on-top toggles.
- Minimise-to-tray, start-with-Windows, restore-layout-on-app-start and confirm-before-disable options.
- **Open Registry**, including double-click support for a monitor registry-key cell.
- **Update App** for downloading the latest stable GitHub release. The updater requires the exact release archive and matching `.sha256`, validates both, and extracts the update separately without replacing the running installation.
- A monitor identity details panel and recent diagnostics.

## Layout Profiles

Press **Save** to name and capture the current arrangement. Press **Restore** to apply the selected profile. The selected profile can be changed in Settings.

Native profiles are versioned, app-owned configuration documents. Each active display records its strong identity, CCD adapter/target identifiers, desktop position, resolution, orientation and primary state. Profile replacement is transactional: an interrupted write is recovered or rolled back as a matched unit, and the previous valid profile is retained as a `.bak` file.

Restore validates the complete requested topology before applying it, persists the accepted topology in the Windows display database, then queries Windows again to verify the active set and geometry. Automatic reconnect restore never disables an extra display. Use an explicit **Restore** only when you intend to replace the current active set.

Profiles created by the previous external-tool version remain usable for safe geometry-only restoration when the complete active set and identity sidecar match. Exact-set restore, including activating a disabled monitor, requires a native profile; save the profile once with this version to capture the required CCD routes. Legacy files are not migrated at start-up. An explicit **Save**, or the optional automatic save-before-disable setting after an exact identity check, upgrades the selected profile transactionally.

If Windows or the graphics driver starts with the wrong rotation or position, enable both **Start with Windows** and **Restore layout on app start**. Monitor Switcher will apply the selected profile shortly after sign-in when validation succeeds.

## Storage

Per-user data is stored under:

```text
%APPDATA%\WorkMonitorSwitcher
```

The default layout remains at `monitor-layout.cfg` for compatibility; named profiles are in the `layouts` directory. The file extension is historical—the native configuration format is versioned and human-readable. Settings, aliases, the profile index and monitor identity data are also written atomically and recovered from a valid backup when possible.

An older default layout beside `MonitorSwitcher.exe` is considered for one-time migration into the per-user directory. The application does not write profiles into its installation directory.

## Running Locally

```powershell
dotnet build MonitorSwitcher.sln
dotnet run --project .\MonitorSwitcher\MonitorSwitcher.csproj
```

Running the application can change the live Windows display topology. Build and regression-test commands do not launch the Windows Forms application or apply a monitor layout.

## Regression Tests

The regression suite is a console runner rather than a `dotnet test` project:

```powershell
dotnet run --project .\MonitorSwitcher.Tests\MonitorSwitcher.Tests.csproj -c Release
```

CI builds the full solution and runs this command for every push and pull request. Release packaging also runs the suite before publishing.

## Releases

GitHub releases are self-contained Windows x64 zip archives. Tagged `vX.Y.Z` builds use `X.Y.Z` in the application metadata and retain the tag in the archive name. There is currently no installer: extract the zip to a folder and run `MonitorSwitcher.exe`.

Release archives include `LICENSE`, `README.md`, `RELEASE_NOTES.md`, `THIRD-PARTY-NOTICES.md` and the exact resolved .NET runtime notices in `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`, with a matching `.sha256` checksum asset. They contain Monitor Switcher and the Microsoft .NET runtime required for a self-contained build; no separate monitor-control executable or package dependency is included.

## Licence

Copyright (c) 2026 Ci303.

Monitor Switcher is free software licensed under GNU General Public License v3.0 only (`GPL-3.0-only`). See `LICENSE` for the full terms. Runtime attribution for self-contained releases is described in `THIRD-PARTY-NOTICES.md`; the complete resolved runtime notices are in `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`.

## Repository Notes

- Main application code: `MonitorSwitcher`
- Regression runner: `MonitorSwitcher.Tests`
- User settings and aliases: `%APPDATA%\WorkMonitorSwitcher`
- Publishing profile: `MonitorSwitcher/Properties/PublishProfiles`
