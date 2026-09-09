# Release Notes

## Unreleased

## v0.4.2 - 2026-09-09

### Fixed

- Correct unintended movement of retained monitors after a profile change or Enable action using the live pre-switch arrangement, not old profile coordinates.
- Allow Windows time to settle before attempting correction; retain strict target, orientation, size and arrangement verification with rollback if correction fails.
- Added regression coverage for position drift, portrait-flipped orientation, primary removal, newly enabled displays and repeated correction. The profile-switching fix was also tested on a physical three-monitor setup.

## v0.4.1 - 2026-08-25

### Fixed

- Embedded one canonical application icon and use it consistently for the executable, main window, Settings, profile-name dialog, themed messages and notification area.
- Added regression coverage that fails when the managed icon resource is missing or unreadable.

## v0.4.0 - 2026-08-19

### Fixed

- Replaced the external monitor-control backend with direct Windows CCD detection and topology application; no separate monitor utility is required.
- Profiles now control only which physical monitors are enabled. Profile changes, reconnects and direct Enable actions no longer reapply saved position, resolution, rotation or preferred-primary state.
- Windows Display Settings is now authoritative for monitor arrangement. Profile application asks Windows to use its persisted configuration for the requested physical monitor set and is a no-op when that set is already active.
- Profile application still requires and verifies an exact physical monitor set, so a partial activation cannot be reported as successful.
- Monitor activation now also verifies retained displays' orientation, effective size, relative placement and retained primary selection, and rolls back if Windows changes them unexpectedly.
- Profile previews now use the latest reliable physical detection and list present disabled saved monitors under **Enable** instead of **Unavailable**.
- Native profiles no longer treat a serial number alone as physical identity, preventing an absent saved monitor from being replaced by a connected twin that reports the same serial.
- Added a main-window profile selector. The Apply button is highlighted only when the selected valid profile differs from the reliably detected active monitor set.
- The notification-area **Apply Selected Profile** action now uses the same profile validity, detection and busy-state preflight as the main Apply button.
- All profile-application routes now honour display rollback quarantine, including **Save & Apply** from Settings, without blocking ordinary preference changes.
- Native profile validation, cancellation and invalid topology results are no longer reported as successful saves or restores.
- Start-up restores are cancelled when a manual display action begins, and launching a portable or rollback copy no longer retargets the existing Windows start-up entry.
- Settings now shows a different or unrecognised Windows start-up entry explicitly and leaves it untouched unless the user changes that option.
- Corrupt or missing JSON settings recover from valid backups without replacing the good backup.
- Profile deletion now removes exact sidecars/backups transactionally and cannot resurrect a deleted index entry during backup recovery.
- Profile deletion now recovers or refuses an outstanding save journal before removing any profile artefact.
- Removed automatic profile overwrites before disabling a monitor. Profiles now change only when explicitly saved.
- Settings no longer discards preferred-primary metadata merely because that field is not currently editable in the monitor table.

### Improved

- Monitor detection and layout work now runs asynchronously with shutdown cancellation and a visible degraded-detection warning.
- Added versioned native profiles. Legacy profiles are upgraded by an explicit save while retaining the previous valid profile for rollback.
- Reorganised Settings into compact themed General, Monitors and Profiles sections with consistent Save and Cancel actions. Selected-monitor information now sits below a monitor-count-aware list and expands to show its complete identity record without an internal scrollbar, while the title bar retains its theme when inactive.
- Displays that share a credible monitor serial now use unique Windows instance or native-target identities; detection still fails closed when Windows cannot disambiguate them safely.
- Alias migration requires corroborating CCD, PnP or registry identity; a serial reported by a disconnected duplicate cannot claim another monitor's alias.
- Large monitor lists are vertically scrollable and bounded to the current working area.
- Refreshed the light and dark themes with clearer hierarchy, accessible primary actions, semantic status badges, softer monitor cards, and a proper degraded-detection banner.
- Repeated monitor refreshes now release dynamic tooltip registrations and rounded-card drawing resources immediately.
- Added a confirmed Clear Diagnostics action that removes both the saved log and its temporary exported copy, with partial failures reported to the user.
- Added explicit Windows CI/release regression execution, deterministic release versioning, SHA-256 release sidecars, and expanded rollback-focused coverage.
- Renamed **Update App** to **Check for Updates** and made the complete network operation cancellable and time-bounded. Verified releases are deduplicated under per-user local application data, with progress and cleanup failures shown without freezing Settings.
- Cached updates retain the verified release archive and are rechecked against the current published checksum; extracted files are verified from that archive rather than trusting writable local metadata.
- Release automation now builds and tests tagged code before attaching assets to a draft; publishing is the final step and public release assets are never replaced.
- Improved Settings keyboard navigation, accessible section state, high-DPI action wrapping and working-area clamping.
- Simplified the selected-profile status to **Current profile** or **Click Apply to use this profile**, while retaining the detailed monitor changes in tooltips and confirmations.
- The in-app update result now explains that checksum verification does not provide a signed publisher identity and warns about the likely Windows SmartScreen prompt.

## v0.3.9 - 2026-07-06

### Added

- Added an optional startup profile setting so Monitor Switcher can reapply the selected enabled-monitor set shortly after sign-in.

## v0.3.8 - 2026-07-04

### Fixed

- Reduced redundant display topology changes during monitor enable and saved-layout restore to avoid destabilising Wallpaper Engine.

## v0.3.7 - 2026-07-03

### Added

- Added a Settings option to control whether Monitor Switcher automatically saves the selected layout profile before disabling a display.
- Added timestamped backups before any automatic layout-profile overwrite.

### Fixed

- Fixed profile application when Windows reassigns `\\.\DISPLAYn` names by matching saved physical monitor identities before changing the active set.
- Stopped automatic disable actions from overwriting saved layout profiles by default.

### Improved

- Added diagnostics logging when automatic layout-profile saves are skipped, attempted, backed up, or fail.
- Added regression coverage for display-name reassignment restore and automatic layout-profile backup naming.

## v0.3.6 - 2026-06-26

### Improved

- Hardened settings and layout-profile persistence with atomic JSON writes and backup files.
- Serialised monitor-changing actions so enable, disable, profile apply and profile save cannot overlap.
- Added diagnostics logging for Windows display detection failures before falling back to the active-screen view.

## v0.3.5 - 2026-06-26

### Improved

- Extracted alias settings mapping and primary monitor preference resolution out of the main form to reduce future monitor-action risk.
- Settings now prevents the same monitor being selected as both primary and fallback primary.
- Settings now greys out disabled fallback primary checkboxes when the same monitor is selected as primary.
- Renamed the Settings confirmation button from OK to Save.

## v0.3.4 - 2026-06-26

### Added

- Added a per-monitor fallback primary setting used when disabling the current primary monitor.

### Improved

- Settings now opens at a content-aware size so the grid, details pane, and action buttons fit without manual resizing.
- Reduced duplication in settings and enable-monitor orchestration code.

## v0.3.3 - 2026-06-26

### Fixed

- Fixed duplicate phantom `DEV:` monitor rows appearing after disabling displays.
- Fixed inactive monitor enable targeting so the app prefers the current live inactive display identity before stale saved display aliases.
- Fixed disabling the current primary monitor by applying and verifying the Windows display topology directly.
- Fixed profile application after re-enabling monitors while leaving Windows-managed position and primary-display state unchanged.
- Fixed notification-area icon initialisation so the tray icon is assigned before it is shown.

### Improved

- Added focused regression coverage for monitor target resolution, duplicate detected rows, disconnected-state presentation, and primary-disable topology positioning.

## v0.3.2 - 2026-05-26

### Fixed

- Fixed an intermittent tray-restore repaint issue where the main window could reopen with the toolbar and status text visible but the monitor rows blank.

## v0.3.1 - 2026-05-19

### Added

- Added single-instance startup behavior so launching Monitor Switcher again restores the existing window instead of opening a second copy.

## v0.3.0 - 2026-05-19

### Added

- Added named monitor profiles with prompted **Save** and explicit **Apply** actions.
- Added notification-area support with a tray menu for open, refresh, save, apply profile, settings, and exit.
- Added Settings options for minimize-to-tray, start-with-Windows, and confirm-before-disable.
- Added a monitor identity details panel in Settings.
- Added a **Diagnostics** button that opens recent monitor action and layout profile events.

### Improved

- Automatic startup profile application now uses the selected monitor profile.
- Settings now shows shortened registry class paths in the grid while preserving full registry keys for tooltips, copying, details, and Regedit opening.
- Layout profile names are sanitized before storage to avoid invalid filename characters and profile-file collisions.
- Stale saved `DEV:` display aliases are now merged into the current stable monitor entries instead of appearing as duplicate offline monitors.
- Release zips are now self-contained Windows x64 packages.
- Release workflow now has the permissions needed to attach generated release zips.
- Manual release workflow runs only attach assets to a GitHub release when a tag is supplied.

## v0.2.0

### Added

- Refreshed main monitor switcher UI with clearer monitor rows, status badges, device hints, and hover tooltips.
- Added a monitor summary to the main window showing active, present, and saved monitor counts.
- Added a settings-window title-bar icon that matches the main app.
- Added **Open Registry** in Settings.
- Added support for double-clicking a **Registry Key** cell to open Regedit at that monitor key.
- Added tooltips for settings and monitor actions.

### Improved

- Renamed the main layout action to **Save Layout** for clarity.
- Improved Settings layout with clearer grouping for monitor tools versus save/cancel/remove actions.
- Improved Settings grid sizing, row presentation, and dark-theme rendering.
- Documented automatic monitor detection and settings tools.

### Validation

- Built successfully with `dotnet build MonitorSwitcher.sln -c Release`.
