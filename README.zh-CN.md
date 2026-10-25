# TopViewport

[English](README.md) · [简体中文](README.zh-CN.md)

**让完整的 Windows 桌面，只显示在屏幕还正常的区域里。**

显示器底部损坏后，任务栏、软件按钮和游戏界面可能被坏掉的部分挡住。单纯降低分辨率，显卡或显示器往往仍会把画面拉伸到整个屏幕，内容依然落在坏区里。

TopViewport 创建一个尺寸更小的虚拟桌面，再将它投射到实体屏幕上方的正常区域。任务栏、窗口、鼠标，以及运行在这个桌面上的游戏都会完整显示；底部避开的区域填充为黑色。避让高度可以按像素调整。

## 主要功能

- 将完整 Windows 桌面及任务栏放进屏幕上方的可用区域。
- 自选实体显示器、底部避让像素数和虚拟屏刷新率。
- 使用 Windows Graphics Capture 与 Direct3D 11 进行 GPU 捕获和呈现，不向游戏进程注入代码。
- 最小化后在系统托盘运行，可打开控制面板、重新连接、恢复显示或退出。
- 保存原显示布局，并由独立恢复进程监控；未确认的显示变更会自动到期恢复。
- 唤醒后重新连接显示器并重建捕获资源；睡眠时长不计入恢复进程的超时判断。

应用界面目前为中文，项目提供中英文说明文档。

## 工作原理

```text
Windows 桌面 + 任务栏 + 应用
                  ↓
尺寸较小的虚拟显示器，设为主显示器
                  ↓  Windows Graphics Capture + Direct3D 11
实体显示器上方的正常区域
─────────────────────────────────────────
底部避让区域，填充为黑色
```

实体屏幕为 `W × H`、底部避让高度为 `B` 像素时，虚拟桌面的尺寸为 `W × (H − B)`。实体显示器保留完整的显示模式，用于承载投射窗口。鼠标限制在虚拟桌面内，投射出的鼠标位置与实际点击位置对应同一个桌面。

运行期间，TopViewport 会调整主显示器和显示布局；其他已启用的显示器会临时移到虚拟桌面旁边。恢复或退出时，程序会尝试还原之前的布局。

## 运行要求

- **x64** 版 Windows 10 1903 或更新版本，或 Windows 11。
- [.NET 8 Desktop Runtime，Windows x64 版](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)。只有从源码编译才需要 SDK。
- 支持 Windows Graphics Capture 和 Direct3D 11 的显卡。
- 安装、配置随附虚拟显示驱动时需要管理员确认，Windows 会弹出 UAC 提示。

随附驱动来自 [Virtual Display Driver 25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23)。安装前，TopViewport 会核对固定的文件哈希，并通过 Windows 验证签名信任。安装使用上游已签名驱动，不开启测试签名、不导入证书，也不关闭 Secure Boot。

## 开始使用

1. 从 [Releases](https://github.com/huangfuhuijie/TopViewport/releases/latest) 下载 Windows x64 ZIP，完整解压，保留与 `TopViewport.exe` 同目录的 `driver` 文件夹。
2. 运行 `TopViewport.exe`，在 **实体屏幕** 中选择需要避开坏区的显示器。
3. 将 **底部避让** 调整为坏区的像素高度，以刚好避开损坏区域为宜；将 **虚拟屏刷新率** 设为支持的刷新率。
4. 点击 **安装 / 配置虚拟屏（需管理员）**，在 Windows UAC 提示中确认。
5. 点击 **刷新显示器**，在 **虚拟主屏** 中选择虚拟显示器。
6. 点击 **启用上方显示区域**。画面可用时，在 20 秒内点击 **画面正常，保留设置**；否则等待自动恢复，或使用下方的紧急恢复快捷键。
7. 最小化控制面板后，程序继续在系统托盘运行。玩游戏时选择虚拟主屏，优先使用窗口或无边框模式。

修改避让像素数或刷新率后，如果提示所需模式不可用，再次执行 **安装 / 配置虚拟屏** 添加该模式，然后刷新显示器列表。

## 托盘操作与恢复

单击或双击 TopViewport 托盘图标，或右键选择 **显示控制面板**，即可重新打开面板。Windows 可能会将图标收进托盘的隐藏图标菜单。

| 托盘菜单 | 用途 |
| --- | --- |
| 显示控制面板 | 打开设置窗口 |
| 重新连接显示区域 | 运行中重建显示与捕获会话；未启用时刷新显示器列表 |
| 恢复原显示设置 | 停止投射，恢复保存的显示布局 |
| 恢复并退出 | 恢复显示布局后退出程序 |

图标颜色表示待机（蓝色）、运行（绿色）、重新连接或恢复（黄色）、异常（红色）。将鼠标停在图标上可查看状态。

**紧急恢复：按 `Ctrl + Alt + F12`。** 也可以运行随包的 `恢复显示设置.cmd`，或在程序目录执行：

```powershell
.\TopViewport.exe --recover
```

主进程异常退出或停止响应时，独立恢复进程会尝试还原布局。恢复仍依赖 Windows 和显示驱动可用。启用期间请保留程序文件和恢复快照；日志与保存的状态位于 `%LOCALAPPDATA%\TopViewport`。

要移除本工具创建的虚拟设备，先恢复布局，再点击 **移除本工具安装的虚拟屏** 并确认 UAC。移除操作针对本工具记录的设备身份；驱动存储中的包和配置备份会保留。

## 游戏与兼容性边界

- **建议先在虚拟主屏上使用窗口或无边框模式。** 部分游戏还需要在游戏设置里切换显示器。
- 捕获和再次呈现会增加工作量，延迟与帧时间可能不同于直接输出到显示器。选择某个虚拟刷新率，不代表游戏一定能以同样的帧率稳定呈现。
- 当前输出为 SDR。HDR、VRR/G-SYNC、受保护视频、远程会话及独占全屏在这条显示路径上的效果不作保证。
- TopViewport 不向游戏进程注入代码，但尚未建立各款游戏和反作弊系统的兼容性结论。
- 游戏或系统意外改变显示拓扑时，程序可能恢复原布局，避免继续使用错误的投射区域。
- 当前支持避让屏幕**底部**的一条区域；不支持任意矩形、左右边缘坏区或多个独立投射区域。

## 验证与诊断

已验证完整桌面投射、任务栏位置、底部黑色区域、GPU 捕获，以及强制结束主进程后的布局恢复。回归检查还覆盖恢复计时、会话隔离、模拟睡眠与唤醒通知、重连，以及恢复后再次启用。

0.1.3 还通过了投射前与投射中的多轮最小化、重新打开测试，以及 Windows 最小化窗口坐标（`-32000`）导致的屏外位置恢复、误放到实体投射屏后移回虚拟桌面、重连过程中与重连完成后打开面板等检查。测试同时确认窗口句柄和紧急恢复快捷键保持可用。

**尚未完成真实整机睡眠与唤醒验证。** 模拟电源通知不能替代这项测试。各款游戏也需要分别测试，桌面测试通过不代表所有游戏兼容。

诊断可以在投射运行时收集。**执行任何测试命令前，都请先停止活跃的投射会话，包括 `--selftest`**；测试窗口可能接管已保存的会话并恢复其显示布局。

```powershell
# 保存显示器和驱动诊断信息，可在投射运行时执行
.\TopViewport.exe --diagnose diagnostics.json

# 以下所有测试都需要先停止活跃的投射会话。
.\TopViewport.exe --selftest test-results

# 集成检查：会临时切换桌面布局
.\TopViewport.exe --live-test live-test-results
.\TopViewport.exe --power-test power-test-results
.\TopViewport.exe --tray-test tray-test-results
```

诊断文件可能包含显示器名称、设备路径和原布局，提交问题前请先检查内容。

## 从源码编译

在安装了 .NET 8 SDK 和 Windows Desktop 目标支持的 Windows 电脑上，于仓库根目录执行：

```powershell
dotnet publish .\TopViewport.csproj -c Release -r win-x64 --self-contained false
```

发布目录会包含随附的 `driver` 文件夹。此构建方式要求目标电脑安装 .NET 8 Desktop Runtime。应用没有额外 NuGet 包依赖。

## 许可与致谢

TopViewport 使用 [MIT 许可证](LICENSE)。

随附的 [Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) 为 [25.7.23](https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/tag/25.7.23) 版本，同样采用 MIT 许可。项目保留了它的[许可证](driver/LICENSE-VirtualDisplayDriver.txt)，以及[来源、签名说明与固定文件哈希](driver/manifest.json)。
