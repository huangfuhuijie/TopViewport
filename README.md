# TopViewport

[English](README.md) · [简体中文](README.zh-CN.md)

**Keep your whole Windows desktop inside the usable part of a damaged monitor.**

When a monitor's bottom edge fails, it can hide the taskbar, application controls, and game UI. Lowering the resolution often leaves the GPU or monitor stretching the image across the entire panel, including the damaged area.

TopViewport creates a smaller virtual desktop and projects it into the healthy upper part of the physical screen. The taskbar, windows, mouse cursor, and games running on that desktop stay within the visible area; the unused strip at the bottom is black. The size of that strip is adjustable in pixels.

## What it does

- Moves the complete Windows desktop, including the taskbar, into the usable upper area.
- Lets you choose the physical monitor, reserved bottom pixels, and virtual display refresh rate.
- Uses Windows Graphics Capture and Direct3D 11 for GPU rendering, without injecting code into games.
- Runs in the notification area, with controls to reopen the panel, reconnect, restore, or exit.
- Saves the previous display layout and runs a separate recovery watchdog. Unconfirmed changes expire automatically.
- Reconnects the display and capture resources after wake. Sleep time is excluded from watchdog timeouts.
- Automatically brings ordinary application windows back when they open behind the physical projection surface, with a manual recovery action in the tray.
- Uses a temporary display layout, so the virtual primary display is not saved as the layout to use at the next sign-in.

The application interface is currently in Chinese. This repository provides both English and Chinese documentation.

## How it works

```text
Windows desktop + taskbar + applications
                  ↓
Smaller virtual display, set as the primary display
                  ↓  Windows Graphics Capture + Direct3D 11
Healthy upper area of the physical monitor
─────────────────────────────────────────
Reserved bottom strip, filled with black
```

For a physical screen of `W × H` and a reserved strip of `B` pixels, the virtual desktop is `W × (H − B)`. The physical monitor keeps its full display mode and hosts the projection window. The mouse is confined to the virtual desktop, so its displayed position and clicks refer to the same desktop.

While active, TopViewport changes which display is primary and rearranges the display layout. Additional active monitors are moved beside the virtual desktop. Restoring or exiting attempts to put the previous layout back.

Starting with 0.1.5, projection uses a temporary Windows display configuration and leaves the saved startup layout unchanged. Earlier versions could save the virtual primary display, leaving a black sign-in screen or an empty physical desktop after a restart when projection was no longer running. The application also restores the original layout synchronously when Windows ends the user session. After restarting Windows, open TopViewport and enable projection again.

**Windows sees two logical displays even when you have only one physical monitor.** The virtual primary display is the usable desktop; the physical display is used only to present that desktop. Some applications remember their old monitor and can reopen behind the projection window. Starting with 0.1.4, automatic window recovery moves eligible ordinary windows from that hidden output area back into the visible virtual desktop. It leaves windows on other accessible monitors alone and does not bring minimized windows to the foreground.

## Requirements

- Windows 10 version 1903 or later, or Windows 11, **x64**.
- [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). The SDK is only needed for building from source.
- A graphics device supporting Windows Graphics Capture and Direct3D 11.
- Administrator approval to install or configure the bundled virtual display driver. Windows shows a UAC prompt for these operations.

The bundled driver is from [Virtual Display Driver 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23). TopViewport checks its pinned file hashes and Windows trust signatures before installation. Installation uses the upstream signed driver; it does not enable test signing, import a certificate, or disable Secure Boot.

## Getting started

1. Download the Windows x64 ZIP from [Releases](https://github.com/huangfuhuijie/TopViewport/releases/latest) and extract the entire archive. Keep the `driver` folder beside `TopViewport.exe`.
2. Run `TopViewport.exe` and choose the physical monitor under **实体屏幕**.
3. Set **底部避让** to the height of the damaged strip in pixels. Use the smallest value that clears the damage. Set **虚拟屏刷新率** to a supported refresh rate.
4. Click **安装 / 配置虚拟屏（需管理员）** to install or configure the virtual display, and approve the Windows UAC prompt.
5. Click **刷新显示器**, then select the virtual display under **虚拟主屏**.
6. Click **启用上方显示区域**. If the picture is usable, click **画面正常，保留设置** within 20 seconds. This keeps projection active for the current session. Otherwise, wait for automatic restoration or use the recovery shortcut below.
7. Minimize the control panel to keep TopViewport running in the notification area. For games, select the virtual primary display and start with windowed or borderless mode.

If you change the reserved pixels or refresh rate and the requested mode is unavailable, run **安装 / 配置虚拟屏** again to add it, then refresh the display list.

## Tray controls and recovery

Click or double-click the TopViewport notification-area icon, or right-click it and choose **显示控制面板**, to reopen the panel. Windows may place the icon in the hidden-icons overflow menu.

| Tray action | Purpose |
| --- | --- |
| 显示控制面板 | Open the control panel |
| 找回不可见窗口 | Bring eligible windows hidden behind the projection back into the visible desktop |
| 自动找回跑屏窗口 | Enable or disable automatic window recovery; enabled by default |
| 重新连接显示区域 | Rebuild an active display/capture session; refresh the display list when inactive |
| 恢复原显示设置 | Stop projection and restore the saved display layout |
| 恢复并退出 | Restore the display layout and exit |

The icon indicates idle (blue), active (green), reconnecting/restoring (amber), or error (red). Hover over it for status.

If a normal application appears on the taskbar but its window cannot be seen, choose **找回不可见窗口**. Recovery uses window position and state, without reading webpage or file contents. It preserves minimized state and does not activate recovered windows. Shell surfaces, menus and captionless fullscreen windows are not recovery targets. Special applications may manage their own window placement and still need manual adjustment.

For Windows' built-in `Win + Shift + S` snipping overlay, TopViewport temporarily hands pointer coordinates to the visible upper area of the physical output. Version 0.1.5 also recognizes the foreground snipping window directly, including Windows UWP windows omitted from normal top-level window enumeration, and displays a small crosshair that follows the pointer. The marker does not take focus or intercept selection clicks. After the overlay closes and mouse buttons are released, normal virtual-desktop pointer confinement resumes. A background screenshot process or an ordinary application does not release that confinement. Compatibility with other screenshot tools is not established.

**Emergency recovery: press `Ctrl + Alt + F12`.** You can also run the bundled `恢复显示设置.cmd` or this command from the application directory:

```powershell
.\TopViewport.exe --recover
```

A separate watchdog attempts recovery if the main process exits unexpectedly or stops responding. Recovery depends on Windows and the display driver remaining available. Keep the application files and recovery snapshot while a session is active. Logs and saved state are stored in `%LOCALAPPDATA%\TopViewport`.

To remove the virtual device created by TopViewport, restore the layout first, then choose **移除本工具安装的虚拟屏** and approve UAC. The removal targets the device identity recorded by this application; the driver-store package and configuration backups are retained.

## Games and limitations

- **Start with windowed or borderless mode on the virtual primary display.** A game's own monitor setting may need changing.
- The projection adds capture and presentation work, so latency and frame pacing can differ from direct display output. A selected virtual refresh rate is not a guarantee of the same delivered game frame rate.
- Output is SDR. HDR, VRR/G-SYNC, protected video, remote sessions, and exclusive fullscreen are not guaranteed to work through this path.
- TopViewport does not inject into game processes, but compatibility with individual games and anti-cheat systems has not been established.
- If a game or the system changes the display topology unexpectedly, TopViewport may restore the original layout to avoid continuing with an incorrect projection.
- Lock screens, sign-in screens, and UAC prompts use a secure desktop that this projection cannot capture. Leaving the saved startup layout unchanged addresses restart persistence; it does not establish secure-desktop compatibility during an active projection session.
- The current tool reserves a strip at the **bottom**. Arbitrary rectangles, side-edge damage, and multiple independent projection regions are not supported.

## Validation and diagnostics

Desktop projection, taskbar placement, the black bottom strip, GPU capture, and restoration after a forced main-process exit have been tested. Regression checks also cover watchdog timing, session isolation, simulated sleep/wake notifications, reconnecting, and enabling again after restoration.

Version 0.1.3 also passed repeated minimize/reopen checks before and during projection, recovery from Windows' off-screen minimized coordinates (`-32000`), moving a panel accidentally placed on the physical projection monitor back into the virtual desktop, and opening the panel during and after reconnecting. These checks verify that the window handle and emergency recovery hotkey remain available.

Window-recovery checks use synthetic application windows to exercise placement and minimized/maximized state. File Explorer and browser window recovery have also been confirmed manually on the development machine. Preservation of windows on another accessible monitor is covered by geometry checks; an additional physical monitor was not used. Pointer-coordinate round trips, confinement during the application heartbeat, and the crosshair's focus and click-through behavior have regression checks. A user manually confirmed that the pointer is visible and selection works with the built-in `Win + Shift + S` tool in 0.1.5 on one Windows 10 machine. This result does not establish compatibility with every system, application, or screenshot tool.

The 0.1.5 shutdown check compares Windows' saved display configuration before and during projection, exercises the session-ending recovery handler, and checks that canceling shutdown returns to an idle desktop that can be enabled again. It simulates notifications without shutting down Windows. **A real reboot with 0.1.5 still needs manual verification.**

**Real whole-machine sleep/wake has not yet been validated.** Simulated power notifications do not replace that test. Individual games also need testing; desktop tests do not establish universal game compatibility.

Diagnostics can be collected while projection is running. **Stop any active projection session before running any of the test commands**, including `--selftest`; test windows can otherwise attach to the existing saved session and restore its layout.

```powershell
# Save display and driver diagnostics; safe to run during projection
.\TopViewport.exe --diagnose diagnostics.json

# All tests below require the active projection session to be stopped first.
.\TopViewport.exe --selftest test-results

# Integration checks: temporarily change the desktop layout
.\TopViewport.exe --live-test live-test-results
.\TopViewport.exe --power-test power-test-results
.\TopViewport.exe --tray-test tray-test-results
.\TopViewport.exe --window-test window-test-results
.\TopViewport.exe --shutdown-test shutdown-test-results
```

The window-recovery integration check temporarily changes the desktop layout and moves only its own test windows. The shutdown check also changes the layout temporarily, but does not restart, shut down, or sign out of Windows.

Diagnostics can contain monitor names, device paths, and the saved layout. Review them before sharing an issue report.

## Build from source

On Windows with the .NET 8 SDK and Windows Desktop targeting support, run from the repository root:

```powershell
dotnet publish .\TopViewport.csproj -c Release -r win-x64 --self-contained false
```

The publish output includes the bundled `driver` directory. This build requires the .NET 8 Desktop Runtime on the destination computer. The application has no additional NuGet package dependencies.

## License and acknowledgements

TopViewport is released under the [MIT License](LICENSE).

The bundled [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver), release [25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23), is also MIT-licensed. Its [license](driver/LICENSE-VirtualDisplayDriver.txt), [source information, signatures, and pinned hashes](driver/manifest.json) are included with this project.
