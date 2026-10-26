# Changelog

## 0.1.5

### Fixed

- Projection now uses a temporary Windows display configuration without saving the virtual primary display as the next startup layout. Older versions could leave a black sign-in screen or an empty physical desktop after a restart when projection was not running.
- Windows session-ending notifications synchronously restore the saved layout before returning. Pending enable and reconnect work is gated during shutdown; canceling shutdown leaves a safe idle desktop that can be enabled again.
- Windows snipping-overlay detection now checks the foreground window directly, including UWP windows omitted from ordinary top-level enumeration. A click-through crosshair follows the physical pointer during snipping without taking focus.

### Validation

- Added comparisons of the saved display configuration before and during projection, session-ending recovery checks, canceled-shutdown checks, and crosshair focus and click-through checks.
- A user manually confirmed a visible pointer and working selection with the built-in `Win + Shift + S` tool in 0.1.5 on one Windows 10 machine. Shutdown notification checks are simulated and do not restart Windows; a real reboot still needs manual verification. Secure-desktop and universal screenshot-tool compatibility are not established.

## 0.1.4

### Fixed

- Ordinary application windows that reopen on the physical output display can be moved into the visible virtual desktop, instead of remaining hidden behind the projection surface.
- Window recovery preserves minimized state, avoids activating recovered windows, and leaves windows on other accessible monitors in place. Shell surfaces, menus and captionless fullscreen windows are excluded.
- Recognized foreground Windows snipping overlays can temporarily use pointer coordinates in the visible physical output area. Virtual-desktop confinement resumes after the overlay closes and mouse buttons are released; ordinary applications and background screenshot processes do not release it.

### Added

- A tray action to recover inaccessible windows and a toggle for automatic recovery, enabled by default.
- Synthetic-window regression checks for placement and state handling, plus a `--window-test` integration command that moves only its own test windows.
- Pointer-coordinate round-trip and heartbeat-confinement checks. Actual `Win + Shift + S` and third-party screenshot-tool compatibility still require validation; additional-monitor preservation is checked with geometry only.
- Documentation explaining why Windows shows two logical displays and how to recover an application that remembers the hidden output display.

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
