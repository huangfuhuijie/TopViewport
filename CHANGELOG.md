# Changelog

## 0.1.3

### Fixed

- Reopening the control panel from the system tray now restores it inside the usable desktop area. Minimized coordinates and positions on the physical output display are no longer reused.
- Repeated minimize/restore cycles preserve the control panel's native window handle and the emergency recovery hotkey.

### Documentation and distribution

- Added English and Simplified Chinese project documentation explaining the damaged-screen use case, the virtual-display approach, setup and compatibility limits.
- Added Windows build automation that publishes an x64 package without changing the runner's display layout.

## 0.1.2

### Fixed

- Sleep time no longer consumes the watchdog heartbeat or display-confirmation timeout.
- Resuming reconstructs GPU capture and presentation resources after displays become available.
- Displays are matched by stable identity when Windows changes their display numbers after reconnecting.
- Restore and reconnect operations run asynchronously so the control panel and tray menu can remain responsive.
- Display choices refresh after recovery, allowing another attempt without restarting the application.

### Added

- A tray menu action to reconnect the display area.
- A dedicated tray icon with idle, active, reconnecting and error states.
- Regression checks for recovery timing, session isolation, display identity and simulated power notifications. Simulated notifications do not establish real hardware sleep/resume compatibility.

## 0.1.1

- Minimize the control panel to the system tray and restore it from the tray menu or by double-clicking the icon.
- Allow configuration of the reserved bottom strip in pixels.
- Handle enabling an inactive virtual display without relying on a mode test that its driver rejects.
- Verify that restoring a saved display layout actually restores the expected modes and positions.

## 0.1.0

- Introduced a Windows Forms control panel for using a monitor with a damaged bottom edge.
- Run the complete desktop on a virtual primary display and present it in the usable upper region of a physical monitor through Windows Graphics Capture and Direct3D 11.
- Include the taskbar and captured pointer in the projected desktop, with the real pointer constrained to that desktop.
- Configure a bundled, signed virtual-display driver with upstream provenance and pinned file hashes.
- Save the original display layout and provide an independent recovery watchdog, a restore command and the Ctrl+Alt+F12 emergency hotkey.
