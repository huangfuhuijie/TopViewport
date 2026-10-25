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
6. Click **启用上方显示区域**. If the picture is usable, click **画面正常，保留设置** within 20 seconds. Otherwise, wait for automatic restoration or use the recovery shortcut below.
7. Minimize the control panel to keep TopViewport running in the notification area. For games, select the virtual primary display and start with windowed or borderless mode.

If you change the reserved pixels or refresh rate and the requested mode is unavailable, run **安装 / 配置虚拟屏** again to add it, then refresh the display list.

## Tray controls and recovery

Click or double-click the TopViewport notification-area icon, or right-click it and choose **显示控制面板**, to reopen the panel. Windows may place the icon in the hidden-icons overflow menu.

| Tray action | Purpose |
| --- | --- |
| 显示控制面板 | Open the control panel |
| 重新连接显示区域 | Rebuild an active display/capture session; refresh the display list when inactive |
| 恢复原显示设置 | Stop projection and restore the saved display layout |
| 恢复并退出 | Restore the display layout and exit |

The icon indicates idle (blue), active (green), reconnecting/restoring (amber), or error (red). Hover over it for status.

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
- The current tool reserves a strip at the **bottom**. Arbitrary rectangles, side-edge damage, and multiple independent projection regions are not supported.

## Validation and diagnostics

Desktop projection, taskbar placement, the black bottom strip, GPU capture, and restoration after a forced main-process exit have been tested. Regression checks also cover watchdog timing, session isolation, simulated sleep/wake notifications, reconnecting, and enabling again after restoration.

Version 0.1.3 also passed repeated minimize/reopen checks before and during projection, recovery from Windows' off-screen minimized coordinates (`-32000`), moving a panel accidentally placed on the physical projection monitor back into the virtual desktop, and opening the panel during and after reconnecting. These checks verify that the window handle and emergency recovery hotkey remain available.

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
```

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
