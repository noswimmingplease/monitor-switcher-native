# Monitor Switcher

Open-source Windows Forms utility for switching monitor profiles without a separate helper application.

[![CI](https://github.com/Ci303/monitor-switcher-native/actions/workflows/ci.yml/badge.svg)](https://github.com/Ci303/monitor-switcher-native/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Ci303/monitor-switcher-native?label=release)](https://github.com/Ci303/monitor-switcher-native/releases/latest)
[![Licence: GPL-3.0](https://img.shields.io/github/license/Ci303/monitor-switcher-native)](LICENSE)

Source repository: https://github.com/Ci303/monitor-switcher-native

## Download and Run

Download the self-contained Windows x64 ZIP and its `.sha256` file from the [latest release](https://github.com/Ci303/monitor-switcher-native/releases/latest). Verify the archive before extracting it:

```powershell
$zip = '.\MonitorSwitcher-v0.4.1-win-x64.zip'
$expected = ((Get-Content -LiteralPath "$zip.sha256" -Raw).Trim() -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'The downloaded ZIP does not match its published checksum.' }
Expand-Archive -LiteralPath $zip -DestinationPath '.\MonitorSwitcher'
```

Run `MonitorSwitcher.exe` from the extracted folder. There is currently no installer; updating does not replace an existing copy or retarget its shortcuts automatically.

> [!IMPORTANT]
> Release executables are not Authenticode-signed. Windows Defender SmartScreen is therefore likely to show **Windows protected your PC**, **Unknown publisher**, or an unrecognised-app warning for a new download. Download only from this repository and verify the ZIP against its attached `.sha256` file. After successful verification, a standard unmanaged Windows installation normally allows **More info** → **Run anyway**. Smart App Control or an organisation's policy may block unsigned applications without offering that option. See [Microsoft's SmartScreen guidance](https://learn.microsoft.com/windows/apps/package-and-deploy/smartscreen-reputation).

## What It Does

- Detects arbitrary numbers of active, disabled, added and disconnected monitors using Windows display APIs.
- Shows currently connected monitors, including disabled but still-present displays, with their activity state and a quick enable or disable action.
- Supports smooth drag-and-drop ordering from each card's three-line handle and retains that order by physical monitor identity.
- Saves and applies named monitor profiles that control which physical displays are enabled.
- Uses Windows Display Settings as the sole authority for position, resolution, orientation and primary display.
- Supports a fallback primary display, dark mode, always-on-top and notification-area operation.
- Opens saved monitor registry keys and records recent display actions for diagnosis.

Monitor Switcher does not download, install or execute a third-party monitor utility. Detection and topology changes use Windows CCD (`QueryDisplayConfig` and `SetDisplayConfig`) directly.

## Requirements

- 64-bit Windows 10 or Windows 11
- GitHub release zip: no separate .NET installation required
- Local development: .NET 8 SDK

The application is designed for an extended desktop. Cloned or mirrored paths are rejected because they do not provide one unique desktop source per monitor. Display drivers, docks, KVM switches and DisplayLink devices can expose unstable or duplicate identities; ambiguous matches fail closed instead of changing an unrelated display.

## Detection and Identity

Monitor Switcher scans displays at start-up, when Windows reports a display or device change, and when **Refresh** is selected. Windows CCD supplies the active and available paths. Device-interface paths and reliable EDID serials are used for exact matching, so saved aliases can follow a physical display when Windows changes `\\.\DISPLAYn` names. Displays that report missing or duplicate identities fail closed where a physical match cannot be proved.

Disconnected hardware cannot always be distinguished from a driver-retained inactive path. The main monitor list therefore shows only displays that Windows currently reports as present, including present displays that are disabled. Saved identity and alias metadata remain stored when a monitor is absent so it can be recognised after reconnection. Reconnection never causes Monitor Switcher to overwrite the arrangement chosen in Windows Display Settings.

## Settings

Settings is split into compact **General**, **Monitors** and **Profiles** sections. Monitor information appears beneath the attached-monitor list, which grows with the detected monitor count and scrolls after eight rows. Settings includes:

- Editable monitor aliases.
- Fallback primary selection for safely disabling the current Windows primary display.
- Dark mode and always-on-top toggles.
- Minimise-to-tray, start-with-Windows, apply-profile-on-app-start and confirm-before-disable options.
- **Open Registry**, including double-click support for a monitor registry-key cell.
- **Check for Updates** for downloading the latest stable GitHub release. It requires the exact release archive and matching `.sha256`, validates both, and extracts one verified copy per version under `%LOCALAPPDATA%\MonitorSwitcher\Updates`. The verified archive is retained so a cached copy can be checked again without trusting mutable local metadata. It does not replace the running installation or change Start-menu or start-up shortcuts.
- A monitor identity details panel, recent diagnostics and a guarded option to clear the saved diagnostics log.

## Layout Profiles

Use the profile selector on the main window to choose a saved monitor set. Selection alone does not change any monitors. **Apply** is highlighted and enabled only when reliable detection confirms that the selected profile differs from the active physical monitor set. Press **Save** to name and capture the currently enabled monitor set. Selecting a different profile in Settings changes the Settings action to **Save & Apply**, which saves the settings and applies that monitor set as one guarded operation.

Native profiles are versioned, app-owned configuration documents. Each active display records its strong physical identity and CCD route. Existing geometry fields remain in the file format for compatibility, but applying a profile does not use them. Profile replacement is transactional: an interrupted write is recovered or rolled back as a matched unit, and the previous valid profile is retained as a `.bak` file.

Profile application does not identify a display by EDID serial alone, because some monitors report the same serial. If a monitor is moved to a different connector and all of its native Windows identities change, save that profile again; the app fails closed rather than enabling a possibly different display.

Apply validates the complete requested physical monitor set, asks Windows to use its own persisted arrangement for that set, then queries Windows again to verify the active set. It does not apply saved coordinates, rotation, resolution or preferred-primary state. Rearrange monitors in Windows Display Settings; those Windows settings remain authoritative.

When **Confirm before disabling** is enabled, applying a profile that changes the active display set asks before continuing. When it is disabled, an explicit **Apply** or **Save & Apply** proceeds without that confirmation.

Profiles created by the previous external-tool version must be saved once with this version before they can enable a disabled monitor, because native CCD route identities are required. Legacy files are not migrated at start-up. An explicit **Save** upgrades the selected profile transactionally.

Enable both **Start with Windows** and **Apply monitor profile on app start** if the selected enabled-monitor set should be applied shortly after sign-in. Position and orientation are still taken from Windows, not from the profile.

## Privacy

Monitor Switcher has no telemetry or analytics. Profiles, aliases, settings and diagnostics remain on the local computer. **Check for Updates** contacts GitHub only when the user selects it.

Monitor identity data can include EDID serial numbers, Windows device-instance identifiers and registry paths. Redact those values before sharing diagnostics or screenshots publicly.

## Storage

Per-user data is stored under:

```text
%APPDATA%\WorkMonitorSwitcher
```

The default layout remains at `monitor-layout.cfg` for compatibility; named profiles are in the `layouts` directory. The file extension is historical—the native configuration format is versioned and human-readable. Settings, aliases, the profile index and monitor identity data are also written atomically and recovered from a valid backup when possible.

Verified releases downloaded by **Check for Updates** are stored separately under:

```text
%LOCALAPPDATA%\MonitorSwitcher\Updates
```

The current installation may therefore be read-only. Before a downloaded release is reused, its retained release archive is hashed against a freshly downloaded published checksum and every extracted file is compared with the file tree derived from that trusted archive. The local cache manifest is also checked, but is not the trust anchor.

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

GitHub releases are self-contained Windows x64 ZIP archives. Tagged `vX.Y.Z` builds use `X.Y.Z` in the application metadata and retain the tag in the archive name. See [Download and Run](#download-and-run) for checksum verification and the expected SmartScreen warning. **Check for Updates** automates the download, verification and extraction stages only.

Code signing requires a separately protected publisher certificate and is planned as release infrastructure rather than being simulated in the application.

Release automation builds and tests the tag before creating a draft release. It attaches and verifies both assets while the release is still a draft, then publishes it as the final operation. An already-public release is never overwritten, which keeps the workflow compatible with immutable GitHub releases.

Release archives include `LICENSE`, `README.md`, `RELEASE_NOTES.md`, `THIRD-PARTY-NOTICES.md` and the exact resolved .NET runtime notices in `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`, with a matching `.sha256` checksum asset. They contain Monitor Switcher and the Microsoft .NET runtime required for a self-contained build; no separate monitor-control executable or package dependency is included.

## Licence

Copyright (c) 2026 Ci303.

Monitor Switcher is free software licensed under GNU General Public License v3.0 only (`GPL-3.0-only`). See `LICENSE` for the full terms. Runtime attribution for self-contained releases is described in `THIRD-PARTY-NOTICES.md`; the complete resolved runtime notices are in `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`.

## Repository Notes

- Main application code: `MonitorSwitcher`
- Regression runner: `MonitorSwitcher.Tests`
- User settings and aliases: `%APPDATA%\WorkMonitorSwitcher`
- Publishing profile: `MonitorSwitcher/Properties/PublishProfiles`
