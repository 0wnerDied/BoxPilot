BoxPilot for macOS — 安装与首次运行
====================================

安装
----

1. 将 BoxPilot.app 拖到 Applications。
2. 从“应用程序”文件夹打开 BoxPilot，不要直接从磁盘映像中运行。

首次运行
--------

当前自动构建使用临时（ad-hoc）签名，未经过 Apple 公证。从网络下载后，
macOS 可能提示无法验证开发者。

请先尝试 Apple 推荐的方法：

1. 尝试打开一次 BoxPilot。
2. 打开“系统设置”>“隐私与安全性”。
3. 在“安全性”区域点击“仍要打开”，然后确认打开。

如果仍被阻止，并且你确认 DMG 来自可信来源，请打开“终端”并执行：

  xattr -dr com.apple.quarantine "/Applications/BoxPilot.app"
  open "/Applications/BoxPilot.app"

如果安装到了其他位置，请相应修改命令中的路径。以上命令不需要 sudo。
不要全局关闭 Gatekeeper。

TUN 系统服务
------------

首次启动 TUN 时，BoxPilot 会请求管理员授权并安装受保护的 LaunchDaemon。
这是一次性操作；后续启动 TUN 不再要求输入密码。退出应用会停止 sing-box，
但保留空闲服务。若不再使用 TUN，可在 BoxPilot 的“设置”中点击
“移除 TUN 系统服务”。


BoxPilot for macOS — Installation and First Launch
==================================================

Installation
------------

1. Drag BoxPilot.app to Applications.
2. Open BoxPilot from Applications, not directly from the disk image.

First launch
------------

The current automated build uses an ad-hoc signature and is not notarized
by Apple. macOS may therefore report that the developer cannot be verified.

Try Apple's supported flow first:

1. Try to open BoxPilot once.
2. Open System Settings > Privacy & Security.
3. In Security, click Open Anyway and confirm.

If the app is still blocked and you trust the source of this DMG, open
Terminal and run:

  xattr -dr com.apple.quarantine "/Applications/BoxPilot.app"
  open "/Applications/BoxPilot.app"

Adjust the path if you installed the app elsewhere. Do not use sudo and do
not disable Gatekeeper globally.

TUN system service
------------------

The first TUN start asks for administrator approval and installs a protected
LaunchDaemon. This is a one-time operation; later TUN starts do not ask for a
password. Quitting BoxPilot stops sing-box but leaves the idle service ready
for the next launch. Use Remove TUN service in Settings when it is no longer
needed.
