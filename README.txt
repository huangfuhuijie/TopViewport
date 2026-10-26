TopViewport
===========

让完整的 Windows 桌面、任务栏和应用，只显示在屏幕上方的正常区域。
用于避开显示器底部坏区；避让高度可按像素调整。

中文说明：README.zh-CN.md
English documentation: README.md

Keep your whole Windows desktop, taskbar, and applications inside the healthy
upper area of a monitor with a damaged bottom edge. The reserved strip is
adjustable in pixels.

Windows 会看到虚拟主屏和实体投射屏两个逻辑屏幕。普通窗口跑屏时，可在托盘选择“找回不可见窗口”；自动找回默认开启。
Windows sees a virtual primary display and a physical projection display. Use
the tray's window-recovery action if an ordinary application opens behind the
projection. Automatic recovery is enabled by default.

0.1.5 使用临时显示布局，不将虚拟主屏保存为重启后的默认布局。重启后请重新打开程序并启用投射。真实重启仍需手动验证。
Version 0.1.5 uses a temporary display configuration without saving the virtual
primary display as the startup layout. Open the application and enable projection
again after restarting Windows. A real reboot still needs manual verification.

Win + Shift + S 截图时会临时切换鼠标坐标，并显示不阻挡点击的十字标记。
During recognized Win + Shift + S snipping, pointer coordinates temporarily use
the physical output, with a crosshair that does not intercept selection clicks.

项目 / Project: https://github.com/huangfuhuijie/TopViewport
下载 / Downloads: https://github.com/huangfuhuijie/TopViewport/releases/latest
许可 / License: MIT; see LICENSE.
