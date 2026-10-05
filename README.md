# Codex Wallpaper Bridge

把本机 **Wallpaper Engine 壁纸库**接入 **Codex Desktop**，通过原生 Windows 控制器选择、切换和关闭背景。

这是基于 [Fei-Away/Codex-Dream-Skin](https://github.com/Fei-Away/Codex-Dream-Skin) 的独立扩展，控制器版本 **3.6.5**。软件不是 OpenAI、Valve 或 Wallpaper Engine 的官方产品。[English](README.en.md)

## 功能

- 扫描 Steam Workshop、Wallpaper Engine 自建项目、内置项目及其他 Steam 库中的已下载壁纸；显示完整列表并支持搜索。
- 视频、场景和网页壁纸保持动态效果；已有预设在原壁纸资源齐全时可用。应用壁纸会显示不可用原因。
- 将 Wallpaper Engine 当前桌面壁纸用于 Codex，也可为 Codex 单独选择其他壁纸。
- 已有可连接的 Codex 会话支持热切换；切换对话、进入首页或 Dots 时保留最后一帧，在页面绘制前同步皮肤标记；相同壁纸热更新复用播放画布。
- 普通 **Codex** 桌面与用户开始菜单快捷方式冷启动时预开本地接口，并先保持皮肤关闭；当前会话可随时热启用、热关闭。开关、切换和后台恢复按顺序执行，接口短暂未就绪时重新检测。
- 采集和传输以 30 FPS 为目标，复用绘图与编码资源，跳过重复帧；接收较慢的窗口不会阻塞其他窗口。已开始播放的隐藏页面暂停传输，恢复可见后继续播放。
- 带缩略图的原生 WinForms 控制器、自定义图标、关闭皮肤和可选登录启动。
- 自动发现本机连接与新版安装位置；重新绑定变更的会话，后台检查进程和连接、定期验证背景，失败时退避恢复。
- 不复制大视频或场景包。屏幕外辅助窗口保持静音，不抢焦点、置顶或显示在任务栏。
- 壁纸启动、设置、关闭命令检查退出状态和超时；窗口或进程失效时立即安全暂停，持久保留失败来源，防止后台服务或换回同一壁纸再次循环报错。

## 要求与限制

- Windows 10/11 **x64**，Windows 自带 .NET Framework 4.x。
- 官方 Microsoft Store 版 Codex Desktop。其他发行方式目前没有验证。
- **Node.js 22 或更新版本**，`node` 位于 `PATH`。此仓库不包含 Node.js 或任何官方应用二进制。
- Steam 版 Wallpaper Engine 已安装并正在运行；相应壁纸资源已完整下载。
- Codex 会话需要开放受支持的本机调试连接。请通过安装后的普通 **Codex** 快捷方式或 **Codex（可随时换肤）** 入口启动。关闭皮肤保留本次会话的接口，之后可直接再启用。
- 官方 AppsFolder 应用入口、直接运行应用程序或其他绕过这些快捷方式的冷启动没有预开接口的保证。已运行且没有接口的会话无法由外部控制器补开；控制器会说明原因并保持 Codex 运行。配置启动入口不会重启当前会话。

动态画面由 Wallpaper Engine 在显示器之外渲染，通过已验证的本机连接传给 Codex 背景画布。采集和传输节奏目标为 **30 FPS**，实际帧率取决于壁纸渲染、本机采集与编码、页面解码的速度；复杂场景可能低于目标。这是一套背景采集方式，场景鼠标互动、桌面音效和原生高帧率不会直接映射到 Codex。特殊网页或场景能否正常渲染仍取决于 Wallpaper Engine。

常见端口、安装路径、会话变化会自动恢复；若官方取消调试能力或彻底修改页面结构，仍需要更新兼容代码。项目不会保证所有未来版本都零维护。

## 安装

1. 下载 Release ZIP 或克隆此仓库，解压到准备长期保留的目录。不要把个人壁纸、截图或凭证复制进仓库。
2. 打开 PowerShell，在该目录运行：

   ```powershell
   powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
   ```

3. 从桌面或用户开始菜单的普通 **Codex** 快捷方式打开 Codex，先显示原生界面。需要壁纸时打开 **Codex 皮肤控制器**，选择皮肤并点击“启用选中皮肤”，之后可在当前会话中随时开关。也可以打开 **Codex（可随时换肤）**，自动使用当前桌面壁纸。

安装只部署到 `%LOCALAPPDATA%\CodexDreamSkin`，创建桌面与开始菜单入口，并记录实际解压目录。**安装本身不启动、关闭或重启 Codex，也不修改 `config.toml`、凭证、WindowsApps 或 `app.asar`。** 源码安装会使用 Windows 自带编译器；Release ZIP 可以附带预编译控制器。

启动入口配置只处理当前用户的快捷方式：根据已注册的官方应用目标或标识识别普通 Codex 入口，新建桌面和用户开始菜单的 `Codex.lnk`，并配置匹配的任务栏快捷方式。自定义参数、其他项目同名入口及专用换肤入口保留。原快捷方式字节和属性保存在 `control\launcher-backups`；需要恢复时运行：

```powershell
& "$env:LOCALAPPDATA\CodexDreamSkin\control\configure-launchers.ps1" -Mode Restore
```

恢复只处理已配置且内容未被后来修改的快捷方式。`-Mode DryRun` 可查看计划，`-ShortcutPath` 可指定要处理的完整 `.lnk` 路径。官方注册 AppsFolder 入口不变。

升级已有安装时，先退出皮肤控制器及其后台服务，再运行安装。安装检测到文件正被占用时会停下并提示；不会关闭你的 Codex。保留解压目录，因为配置会引用它。已有保存主题和活动壁纸保存在用户状态目录中。

## 使用

- “全部”：恢复完整皮肤列表；启动器选中当前壁纸时不会把列表筛成一项。
- 搜索框：按壁纸标题查找。
- “使用当前桌面壁纸”：读取 Wallpaper Engine 当前用户、显示器的选择。
- “启用选中皮肤”：只替换 Codex 背景，桌面当前壁纸继续由 Wallpaper Engine 管理。
- “关闭皮肤”：实时移除背景并停止传输，保留 Codex 和本地接口；随后可以再次热启用。
- “检测并连接已打开的 Codex”：检查本次会话是否有真实可用的页面连接。
- 新下载的壁纸可刷新库。预设缺少原壁纸资源时，先在 Wallpaper Engine 下载相应原壁纸。

动态媒体接口只监听 `127.0.0.1` 并要求运行时随机令牌，图像帧经过内存。壁纸只从经过验证的本机库路径读取。不要把本机调试端口转发到网络，也不要公开 `%LOCALAPPDATA%\CodexDreamSkin` 的状态、日志或令牌。

## 壁纸故障与安全

安全暂停会写入 `paused` 和 `control\motion-safety.json`。列表保留故障壁纸并说明原因；选择正常的其他壁纸可以继续使用，故障记录不会因为切换、重启控制器或后台恢复而自动清除。窗口归属无法确认或定向关闭失败时会暂停所有后续窗口操作。请先检查原壁纸和 Wallpaper Engine，确认故障解决后再人工审核故障记录；不要反复清除记录重试。

预设按原项目的属性定义校验类型、范围、选项和附加资源路径，拒绝网络路径、重解析点、备用数据流及控制字符。原生设置分为每批不超过 1536 UTF-8 字节的 JSON，等待窗口稳定并产生可用帧后才应用。媒体请求同时最多 8 个，请求行、请求头和配置大小均有限制。命令采用正在运行的 Wallpaper Engine 架构及其安装目录，只定向操作本进程的随机辅助窗口。详见 [安全设计与审核范围](SECURITY.md) 和 [Wallpaper Engine 官方命令文档](https://help.wallpaperengine.io/en/functionality/cli.html)。

这些保护能阻止失败循环，不能修补第三方壁纸或 Wallpaper Engine 的原生崩溃；触发原生崩溃的壁纸会被隔离。场景和网页内容由本机 Wallpaper Engine 执行，桥接器没有提供额外的内容沙箱。

## 开发与验证

```powershell
# 输出到仓库之外
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -OutputDirectory ..\artifacts

# 不操作当前 Codex 或真实壁纸的测试
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-SmokeTests.ps1

# 引擎的完整回归检查，使用隔离的临时状态
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\tests\run-tests.ps1
```

本仓库只发布壁纸桥接与已保存主题控制，不包含本机 AI 生图工作流、个人请求、壁纸库、对话截图、运行日志或凭证。默认抽象背景与控制器图标是项目原创资产。

## 许可与署名

软件与原创默认资产使用 [MIT License](LICENSE)。运行时、样式、主题库与安全检查来自 [Codex Dream Skin](https://github.com/Fei-Away/Codex-Dream-Skin)，保留上游版权声明。完整资产和产品边界见 [NOTICE.md](NOTICE.md)。各壁纸仍由其作者和原许可控制，本项目不提供其再分发权。
