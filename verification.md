# Verification

## 2026-11-17 — 主机头精简、内存百分比、实时故障红块与起停联动、四档时段重构及历史段缓存提速（R130）

- **用户需求与已确认规格（`A1`–`A3` + `Inherit` 路径）**：
  1. 移除主机详情页计算机名/系统版本下方 `SSH · ...` 连接行（`MonitoringView.xaml` 删除 `HostConnectionText`）。
  2. 主机内存图右上角大读数改为占用百分比（`HostMemoryPercentText`，零总内存守卫显示 `--`），底部保留 `2.4 GB / 3.3 GB` 容量文本；服务页内存读数继续保持 `MB` + 副标题 `物理内存 xx.x%`（已确认 `A1`：仅主机改百分比）。
  3. 进程故障/停止时即时呈现红色故障块并联动右上角主控按钮：`AgentCommandDispatcher` 在执行 `start`/`stop`/`restart`/`start-missing`/`start-on-app-launch` 后立即触发 `AgentMonitor.RequestImmediateHealthCheck()`；`AgentMonitor.SampleResourcesOnlyAsync` 检测托管进程存活数跳变（`HasProcessLivenessChange`）在 500 ms 内即刻补采健康与落盘；`AgentMonitor.RunConcurrentAsync` 在启动首轮先完成首次主机采样再调度首次持久化（消除空任务进程刚启动时第 1 帧 `Host = null` 的竞态）；`MainWindowViewModel.Monitoring.cs` 与 `LiveHealthOverlay()` 在服务状态变化时同步叠加实时健康区间并限制仅在实态分歧（故障或恢复边沿）时补入覆盖；`DashboardPlot.DrawHealth`、`DrawHealthPulse` 与 `ResolveHealthHover` 对含有效未覆盖 `"failed"` 的可用率竖格按整格全红块渲染，悬停提示同步报 `"故障"`。
  4. `ServiceActions` 主开关按钮（`ServicePowerButton`）随服务运行状态（`SelectedTask?.Status == "OK"`）自动切换 `Stop` / `停止服务` 与实心 `Play` / `启动服务`，并在更多菜单（`ServiceMoreButton`）补充 `"停止"` 条目以保证直达入口齐全。
  5. 统一服务页左侧与垂直文本基线对齐：`ServiceHeader` 采用独立 `28×28` 图标槽使标题与下方 `可用率` / `CPU` / `内存` 左缘严格对齐于同一纵线，服务详情各数字/元信息统一 `Semantic.Font.Interface` + `Typography.NumeralAlignment="Tabular"`，同步微调 [LineIcon](<src/SystemAutorun.App/Controls/LineIcon.cs>) 中 `LegendHelp`（`?`）坐标使其与 `15×15` 视口光学居中；[MainWindow.xaml.cs](<src/SystemAutorun.App/MainWindow.xaml.cs>) 构造函数先切至 `WorkloadsPage` 再调用 `InitializeDashboard()`，消除构造期对默认 `OverviewPage` 的无效初始 `resource-history` 请求；`MainWindowViewModel.History.cs` 对在途手动选行/切窗历史查询加保护，不被 1 秒自动后台轮询覆盖为自动态而意外取消。
  6. 移除所有趋势图右下角 `{start} — {end}` 时间区间文字（`DashboardPlot.DrawTrend`）；清理冗余双标题绑定。
  7. 将时间窗口选择器重构为 `实时`（`0`，可用率与资源同为最近 5 分钟 · 1 分钟刻度）、`最近1天`（`24`）、`最近1周`（`168`）、`最近1月`（`720`）四档可勾选菜单；在 [DashboardPlot](<src/SystemAutorun.App/Controls/DashboardPlot.cs>) 增加 `IsLoading` 状态与红色往复加载扫描条（`"正在载入…"`），并在 [MonitoringHistoryStore](<src/SystemAutorun.Core/Monitoring/MonitoringHistoryStore.cs>) 引入有界 `512` 段 SHA-256 内容校验段缓存（`ArrayPool<byte>` + `SHA256.HashData` + SIMD 行切分）、3.5 s 读锁/5 s 预算及 `SaveMarkerAsync` Windows 短时占用重试。
- **ENG（全部在最终代码改动后实测通过）**：
  - `dotnet build SystemAutorun.sln -c Release --no-restore`：0 警告、0 错误。
  - `dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj -c Release --no-build`：**1152 / 1152** 通过（0 失败、0 跳过，3 s；含新增 [R130 历史段缓存校验用例](<tests/SystemAutorun.Core.Tests/>)）。
  - R130 专项及全部受影响 `IntegrationTests` 整轮复跑（`R130DashboardAndHistoryTests`、`ServiceSelectionPerformanceTests`、`DataChangePulseTests`、`ResourceHealthLifecycleWindowTests`、`DashboardHealthRenderingTests`、`MonitoringWindowTests`、`ResourceDashboardWindowTests`、`ServiceSearchLayoutTests`、`DashboardMonitoringTests`、`DashboardReadModelTests`、`GuiRemoteHostControlTests`、`RemoteHostStreamingAndUiTests`、`DashboardPlotInteractionTests`、`ToolWindowLifetimeTests`、`AgentMonitorConcurrentTests`、`AgentMonitorTests`、`RealAgentRestartRetainsActualHostResourcesWithoutBackfillingTheNewSecondRing`）：**204 / 204** 全数通过（0 失败、0 跳过，5 m 46 s；说明：同步更新 `ServiceSelectionPerformanceTests.History` 间隔以适配默认 5 分钟窗口内 8192 区间、`ResourceHealthLifecycleWindowTests` 将合成基准锚点置于 `"baseline"` 时刻、`DashboardPlotInteractionTests.HoverGuideReversesAndRespectsMotionLifetime` 用确定性 `!plot.HoverAnimationActive` 等待替代 220 ms 固定休眠，以及 `AgentProcessContractsTests.Resources.cs` 通过 `recent-resources-v2` 精确校验 500 ms 首样本 `DiskReadBytesPerSecond == null`）。
  - `CliMonitoringTextTests` 独立复跑：**34 / 34** 通过（0 失败、0 跳过，16 s）。
- **VIS（离屏 `RenderTargetBitmap`）**：通过 `SA_R130_EVIDENCE` 逐张查看了 Dark/Light 双主题共 9 张 `1152×648` PNG（失败服务 `实时` 与 `最近1天`、运行正常服务 `Play`/`Stop` 切换、加载扫条态、以及移除 `SSH` 行并将 `73%` 作为内存主读数的远程 Linux 主机页 `VM-12-15-ubuntu`），确认左侧对齐、实心 `Play`/`Stop` 与整格故障红块、无右下角时间区、加载扫条与零焦点外框全部清晰；`scratch\r130-vis` 已于检查后清理。注：上述为离屏渲染检查，桌面实窗连续交互手感待从正式入口打开亲测。
- **DEPLOY & DATA（授权原地发布 + 实盘 `.autorun-state` 缓存基准）**：经确认 `A3` 后优雅停止运行中旧版 GUI（PID `25664`）与 Agent（PID `16168`），未触碰任何托管工作负载；执行 [publish-native.ps1](<scripts/publish-native.ps1>) `-NoRestore` 原地更新 `artifacts\publish\win-x64`（目录创建时间保持 `2026-11-16 14:55:37`，`876 / 876` 文件 SHA-256 0 失配）。随后用新版 CLI 拉起新版 Agent 并直连命名管道实测本机真实历史存储（`113` 文件、合计 `63.85 MB`）：`720` 小时持久化主机 `resource-history` 首次冷读耗时 **`2188.3 ms`**（Truncated=true），第 2/3 次命中 SHA-256 段缓存降至 **`168.26 ms` / `182.53 ms`**（约 **12–13× 提速**）；`720` 小时服务 `monitoring-history`（`docker-desktop`，8192 区间 + 120 桶）在暖缓存下耗时 **`233.98 ms` / `181.2 ms`**。测毕 `daemon stop` 退出，残留 `SystemAutorun*` 进程数 **`0`**。

## 2026-11-16 — 正式包原地更新修复、远程状态预热与无闪烁切换补强（R129b）

- 根因定位：核查实机进程（`Win32_Process`）发现桌面正在运行的 GUI（PID `23928`）与 Agent（PID `13320`）实际来自上一轮 `Move-Item` 改名产生的备份目录 `artifacts\publish\win-x64.rollback-r129`（由文件管理器 `dopus.exe` 自动跟随目录句柄切换所致），加载的仍是 R128 GUI 与 11 月 15 日旧版 Agent，导致继续走单次 SSH 探测且无远程四图数据。现已优雅停机该旧实例、复制归档至 [R128 全量归档](<artifacts/rollback/r128-full/>) 并移除 `artifacts\publish\win-x64.rollback-r129` 与 `win-x64.previous`（保留既有历史备份目录未动），同步将 [publish-native.ps1](<scripts/publish-native.ps1>) 改为备份至 `artifacts\rollback\win-x64.previous` 并对 `artifacts\publish\win-x64` 执行原地覆盖同步（`Sync-SafeDirectoryInPlace`），避免再次触发目录句柄跟随切换。
- 同步修复两项实装细节：在 [AgentMonitoringSnapshot](<src/SystemAutorun.Core/Models/AgentMonitoringSnapshot.cs>)、`AgentRemoteHostMonitor.GetFreshSnapshots` / `AgentMonitor.GetSnapshot` 与 [MainWindowViewModel.Monitoring.cs](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Monitoring.cs>) 中通过被动快照预热全部已启用远程主机最近在线状态，并在 [OnSelectedHostTabChanged](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L717-L735>) 复用尚未过期的缓存快照以避免首次或来回切页时先出现“等待当前主机的后台观测”与 `UNKNOWN` 骨架闪屏；同时将 [SshRemoteHostReader](<src/SystemAutorun.Windows/Health/SshRemoteHostReader.cs>) Linux 网卡列表在取前 32 活跃接口后按 `(hasIp, name)` 稳定排序，防止累计流量反超引发行序跳动。
- ENG：`dotnet build SystemAutorun.sln -c Release --nologo` 0 警告 0 错误；`Core.Tests` 通过 **1152/1152**（0 失败、0 跳过）；在 [RemoteHostStreamingAndUiTests.cs](<tests/SystemAutorun.IntegrationTests/RemoteHostStreamingAndUiTests.cs>) 新增开启动效的 `RemoteHostPreWarmedSwitchWithMotionEnabledHasZeroSkeletonFlashAndDrawsAllCharts` 回归，受影响 9 组核心 `IntegrationTests`（`RemoteHostStreamingAndUiTests`、`GuiRemoteHostControlTests`、`HostControlPlaneTests`、`SshRemoteHostReaderTests`、`ResourceHistoryRpcTests`、`CliMonitoringFilterTests`、`DashboardMonitoringTests`、`ResourceDashboardWindowTests`、`R126DashboardAndChromeTests`）通过 **146/146**（0 失败、0 跳过）；`CliMonitoringTextTests` 单独复跑通过 **34/34**（0 失败、0 跳过；说明：与前述 9 组在默认并行集合下混跑的两次中，`MainWindowAndIndependentConsoleShareExactHistoryWindowsWithoutSamplingOnRead(restart: True)` 的 124 次快速连续 `Seed` 写入在 `%TEMP%` 上出现瞬时 `UnauthorizedAccessException: Access to the path is denied.`，单独运行稳定 34/34 全过）。
- VIS（离屏 `RenderTargetBitmap`）：经 `SA_R129_EVIDENCE` 重新截取并逐张核对 Dark/Light 双主题（`24小时` `1152×648` 顶部、滚动底部 `/` 与 `/data` + `enp0s6` / `zt0`、`7天` `1320×820`）共 6 张 PNG 像素，Linux 图标、四宫格红线/渐变与零外框状态均清晰完整；真实桌面手感与实机鼠标悬停待重新从 `artifacts\publish\win-x64\SystemAutorun.exe` 启动后体验确认。
- DEPLOY & DATA（原地正式包更新 + 全三台实机流式核验）：`pwsh -File scripts/publish-native.ps1 -NoRestore` 原地激活 [artifacts/publish/win-x64](<artifacts/publish/win-x64/publish-manifest.json>)（原目录创建时间 `2026-11-16 14:55:37` 保持不变，`876/876` 文件 SHA-256 0 失配）。随后用新版 CLI 拉起 [SystemAutorun.Agent.exe](<artifacts/publish/win-x64/runtime/SystemAutorun.Agent.exe>)（PID `20260`）静待 6 秒实测三台已配置远程主机：`monitor status --json` 全部在线（`cutclass-vps` `Ubuntu 22.04.4 LTS`、`oci-arm` `Ubuntu 24.04.4 LTS`、`oci-arm-public` `Ubuntu 24.04.4 LTS`），命名管道直查 `recent-resources-v2`（`500 ms`）三机均真实入环（分别返回 `11` / `6` / `5` 个有效点，CPU/内存/磁盘 IO/网络速率全非空），随后 `daemon stop` 干净退出。

## 2026-11-16 — 远程主机常驻流式采集、历史轨迹、同频无闪烁刷新与全量包更新（R129）

- 源码改动覆盖后台采集到 UI 展示全链路（`Core` 模型/身份哈希/历史投影/存储校验、`Windows` `SshRemoteHostReader` 速率与常驻流、`Agent` `AgentRemoteHostMonitor` 后台常驻连接与 10 秒落盘、`App` 远程读数/四宫格曲线/四档同频/原位无闪烁更新），未改动任何受保护配置文件内容，未停用任何用户工作负载。
- ENG：`dotnet build SystemAutorun.sln -c Release --nologo` 0 警告 0 错误；`dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj -c Release --no-build --nologo` 通过 **1152/1152**（0 失败、0 跳过，含新增 8 例 `RemoteHostResourceHistoryTests` 身份隔离、Schema 1/2 `RemoteHostId` 校验、10 秒持久存储、断线分段与损坏边界清理）。受影响 12 组 `IntegrationTests`（`RemoteHostStreamingAndUiTests`、`GuiRemoteHostControlTests`、`HostControlPlaneTests`、`SshRemoteHostReaderTests`、`SshInputFileLeaseTests`、`ResourceHistoryRpcTests`、`DashboardMonitoringTests`、`ResourceDashboardWindowTests`、`DataChangePulseTests`、`R126DashboardAndChromeTests`、`ChartLegendAlignmentTests`、`RemoteHostsWindowTests`）整轮一次性通过 **113/113**（0 失败、0 跳过；说明：其中修正了 `R126DashboardAndChromeTests` 第 328 行外框边界选择器由按钮内层无边框 `Surface` 改回外层 chrome 1 DIP 边框，以匹配 150% DPI 下同级边框对齐语义）。
- VIS（离屏 `RenderTargetBitmap` + 实机 SSH 探针抽检）：经由 `SA_R129_EVIDENCE` 逐张查看了 Dark/Light 双主题下的远程 Linux 主机视图（`1152×648` 顶部、滚动至底部的多挂载存储卷 `/` + `/data` 与双网卡 `enp0s6` + `zt0`、以及 `1320×820` `7天` 历史视图），确认四宫格 `CPU`/`内存`/`磁盘IO`/`网络` 顶部数值、红线与红色面积渐变、多卷与多接口均清晰完整且零焦点外框；真实界面连跑 `0.5 秒` 与 `10 秒` 刷新期间录得 `0` 次 `HostStatusAvailable` 跌落、`0` 次 `"正在查询"` 闪烁与 `0` 次 `Drives`/`Networks` 清空重置，断线异常仍准确回退 `UNKNOWN`。另在沙盒内短时启动已发布 `Agent`（PID `12328`）通过 ZeroTier 实采用户实际配置的 `oci-arm`（`arm-server-1` / `Ubuntu 24.04.4 LTS`），观测到连续 `500 ms` 流式样本包含 `/ ` 与 `/boot` 存储卷、真实速率（`networkReceiveBytesPerSecond` `100444.26` → `15011.14`）及完整网卡清单，随后 `daemon stop` 干净停机。
- DEPLOY（用户授权完整正式包更新）：经 `scripts/publish-native.ps1` 构建并校验候选包（`876/876` 文件 SHA-256 0 失配），优雅停止原正式 GUI 与 `SystemAutorun.Agent`（未触碰任何托管工作负载），原正式目录已完整备份至 [R129 全量回滚包](<artifacts/publish/win-x64.rollback-r129/publish-manifest.json>)（保留原有 `win-x64.previous` 不动），并将新版全量程序集切换至 `artifacts/publish/win-x64`。因命令执行沙盒退出时会自动回收其拉起的独立守护子进程，部署后已确认旧 GUI/Agent 锁释放且配置文件 `autorun.yaml`、`autorun.overrides.json` 与 `autorun.gui.settings.json` SHA-256 与基线严格一致；待从桌面打开 `artifacts/publish/win-x64/SystemAutorun.exe` 即可按内置启动逻辑自动拉起新版 `SystemAutorun.Agent.exe` 并开始全天候背景采集。

## 2026-11-16 — 红线曲线、系统图标、服务头部重构、切换延迟绘制与灰红选中态（R128）

- 源码改动严格限定于 GUI 层（`src/SystemAutorun.App`：主题色板、`Components`/`Dashboard` 样式、`DashboardPlot`/`DashboardPlot.Pulse`/`LineMotion`/`LineIcon`/`PlotLegendHelp`/`SidebarAssist`、`ColorStateAssist`、`MainWindowViewModel`/`MainWindow`、`MonitoringView`/`HostResourceCharts` 及八类工具弹窗），未改动 `Core` / `Windows` / `Agent` / `Cli` / `Elevated` 运行时及受保护根配置。
- ENG：`dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj -c Release` 通过 `1144/1144`（0 失败、0 跳过）；全量受影响 20 组 GUI `IntegrationTests`（`R126DashboardAndChromeTests`、`ServiceSearchLayoutTests`、`RemoteHostsWindowTests`、`DialogChromeTests`、`ColorStateAssistTests`、`CompactSidebarTests`、`MonitoringWindowTests`、`ToolWindowMotionTests`、`ChartLegendAlignmentTests`、`DashboardHealthRenderingTests`、`DashboardPlotInteractionTests`、`PointerInteractionWindowTests`、`AnimationRegressionTests`、`DataChangePulseTests`、`ResourceDashboardWindowTests`、`ResourceDataPulseWindowTests`、`DisclosureMotionTests`、`FormWindowMotionTests`、`PresenceMotionTests`、`StartupGuiWindowTests`）整轮通过 `255/256`，唯一负载调度计时抖动项 `DashboardPlotInteractionTests.HoverGuideReversesAndRespectsMotionLifetime` 随后独立连跑 `3/3` 全过（覆盖 `256/256`，包含 R128 红线像素与增量脉冲定位、OS 图标识别与 `HostName`/`HostOs` ≤ 5 DIP 紧距、服务 CPU/内存头部全宽布局、真实 `SelectTab` 跨页延迟扫绘含滚动视口外折叠区子图及灰底红字/红底白字对比度）。另独立旧基准测试 `ServiceSelectionPerformanceTests` 因存在早于 R128 的 R125 方法签名/异步历史时序不匹配，未纳入 R128 范围且已保持原样未改。
- VIS（离屏 `RenderTargetBitmap`）：通过 `SA_R127_EVIDENCE` 逐张审查了 Dark/Light 双主题下的服务页（`920×580`、`1152×648`、`1320×820` 与 `45%` 扫绘帧）、主机页（`920×580`、`1152×648` 顶/底、`Ubuntu 24.04.1` Linux OS 图标页及 `45%` 扫绘帧）、异常日志页（`1152×648`）、`AddProgramWindow`（含 `ArgumentsTextBox` 红色悬停底线）与 `StartupManagerWindow`。确认了全红主/次趋势曲线与红色渐变投影协调、`服务页右侧详情区 6 DIP` 与重构后 `CPU PID`/`内存 物理内存 xx.x%` 上栏紧凑满宽对齐、`Windows`/`Linux` 系统图标与缩小间隙生效，且激活态灰底红字与悬停红底白字对比清晰。上述为离屏合成渲染验证，桌面实窗上的实时帧率手感与真实鼠标悬停交互待用户亲手运行核验。
- DEPLOY（受控仅 GUI 热替换）：`dotnet publish src/SystemAutorun.App/SystemAutorun.App.csproj -c Release -r win-x64 --self-contained true` 确认 `SystemAutorun.exe` 与 `SystemAutorun.deps.json` 与正式包哈希完全一致，已备份旧版 [R127 系统程序集与清单](<artifacts/rollback/r128-gui/>)。已停止原桌面 GUI PID `2188` 以释放文件锁，热替换 `artifacts\publish\win-x64\SystemAutorun.dll`（`727,040` B → `732,672` B，SHA256 `fe111cd66fd9bb6f9e940b8ce1665547097fa23c8d786a11e951fb86089eaa73`）并同步更新 `publish-manifest.json`（全量 `876/876` 文件 0 失配）。新版 `SystemAutorun.exe --background --root C:\_MY_WORK\systemautorun` 5 秒冷启动探针（PID `18932`）确认正常存活（工作集 `120 MB`）后已清理退出，正式 GUI 保持关闭就绪态（原运行窗口已停止，待从桌面手动重新打开 `artifacts\publish\win-x64\SystemAutorun.exe`）；`SystemAutorun.Agent.exe` 始终保持原 PID `13636`（启动时间 `2026/11/15 18:29:50`）运行不中断，三份受保护根配置 `autorun.yaml`、`autorun.overrides.json`、`autorun.gui.settings.json` SHA256 与基线严格一致；所有临时截图、发布缓存目录与测试进程已清理完毕。

## 2026-11-16 — 红色图表渐变、全端 6 DIP 边距、后置入场绘图、红线弹窗背景与柔和深色（R127）

- 源码改动严格限定于 GUI 层（`src/SystemAutorun.App`：主题色板、`Components`/`Dashboard` 样式、`DashboardPlot`/`DialogBackdrop`/`PresencePanel`/`LineMotion`/`SidebarAssist`、`MainWindow`、`MonitoringView`、`HostResourceCharts` 及工具弹窗），未改动 `Core` / `Windows` / `Agent` / `Cli` / `Elevated` 运行时及受保护根配置。
- ENG：`dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj -c Release` 通过 `1144/1144`（0 失败、0 跳过）；全量受影响 UI 集成测试队列（`R126DashboardAndChromeTests`、`ServiceSearchLayoutTests`、`RemoteHostsWindowTests`、`DialogChromeTests`、`ColorStateAssistTests`、`CompactSidebarTests`、`MonitoringWindowTests`、`ToolWindowMotionTests`、`ChartLegendAlignmentTests`、`DashboardHealthRenderingTests`、`DashboardPlotInteractionTests`、`PointerInteractionWindowTests`、`AnimationRegressionTests`）共 `224` 项中整轮通过 `223/224`、唯一并发调度计时抖动项 `DashboardPlotInteractionTests.HoverGuideReversesAndRespectsMotionLifetime` 随后单独复跑 `5/5` 通过（最终覆盖 `224/224` 全通过，含 R127 实测红渐变区域像素采样、420 ms 后置入场曲线延迟、`DialogBackdrop` 7s 红色斜线纹理及 `AddProgramWindow` 自定义模板 `TextBox` 红色悬停校验）。
- VIS（离屏 `RenderTargetBitmap`）：通过 `SA_R127_EVIDENCE` 逐张查看了 Dark/Light 双主题下的服务页（`920×580`、`1152×648`、`1320×820` 及 `45%` 扫绘帧）、主机页（`920×580`、`1152×648` 顶/底及 `45%` 扫绘帧）、异常日志页（`1152×648`）、遮罩背景（`scrim-dark/light-1152x648`）、`AddProgramWindow`（展开高阶字段并触发 `ArgumentsTextBox` 悬停红底线）与 `StartupManagerWindow`。确认了趋势曲线下方红色投射渐变层次清晰、服务页左右与顶/底 `6 DIP` 边距对称、`DialogBackdrop` 呈现清晰红色斜纹、深色 `#141417` 背景与 `#D4D4DC`/`#E6E6EC` 灰白文本柔和自然，以及文本框悬停红边无黑线覆写残留。实际鼠标指针连续悬停响应、图表与斜纹 2× 动画体感帧率尚待用户在桌面实窗运行体验确认。
- DEPLOY（受控仅 GUI 热替换）：Release `dotnet publish` 确认 `SystemAutorun.deps.json` 与 `SystemAutorun.exe` 哈希完全未变、`Core`/`Windows` 源码零修改，旧版正式 [R126 系统程序集与清单](<artifacts/rollback/r127-gui/>) 已备份。已停止原桌面 GUI PID `3688` 以释放文件锁，热替换 `artifacts\publish\win-x64\SystemAutorun.dll`（`723,456` B → `727,040` B，SHA256 `74e347026df74559e9868cf714efb06a43d23a7b56c9cfd62e454274986dac17`）并同步更新 `publish-manifest.json`（全量 `876/876` 文件哈希与大小 0 失配）。新版 `SystemAutorun.exe --background` 经 5 秒冷启动稳定性探针验证存活正常（工作集 `139.2 MB`）；持续监测守护进程 `SystemAutorun.Agent.exe` 保持原 PID `13636`（启动时间 `2026-11-15T18:29:50`）全程不间断，`autorun.yaml`、`autorun.overrides.json`、`autorun.gui.settings.json` SHA256 与基线严格一致；所有临时截图、发布缓存目录与测试进程已全部清理，正式 GUI 待用户直接从桌面打开 `artifacts\publish\win-x64\SystemAutorun.exe`。

## 2026-11-16 — 图表绘制动效、对齐居中与红白交互统一（R126）

- 源码改动严格限定于 GUI 层（`src/SystemAutorun.App`：绘图控件/动效、视图模型文案、主题/样式、主页与各工具弹窗），未改动 `Core` / `Windows` / `Agent` / `Cli` / `Elevated` 运行时及受保护根配置。
- ENG：`dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --nologo` 通过 `1144/1144`（0 失败、0 跳过）；涵盖 [R126 专项回归](<tests/SystemAutorun.IntegrationTests/R126DashboardAndChromeTests.cs>) 及受影响的动效/对齐/着色/窗口交互 `IntegrationTests` 套件通过 `170/170`（0 失败、0 跳过；含 `620 ms` 真实左到右裁剪像素对比、服务/主机跨页切换与快速折返、异步滞后数据触发与轮询不重播、无截断/10 秒源多余文案、几何与垂直居中断言、双主题红白语义及浅色展开头对比度验证）。
- VIS（离屏 `RenderTargetBitmap`）：通过 `SA_R126_EVIDENCE` 逐张审查了 Dark/Light 双主题下服务页（`920×580`、`1152×648`、`1320×820`）、主机页（`920×580`、`1152×648` 顶部及滚到底部）、异常日志页（`1152×648`）、`AddProgramWindow`、`StartupManagerWindow` 与 `SweepProgress = 0.45` 绘制中截帧像素；通过实际视觉检出并修复了浅色主题下展开后的 `CompactDisclosureHeader` 白字隐身缺陷，并复验重截像素确认可读性恢复及零底部红色扫光残留。上述为后台合成数据与离屏窗口渲染证据，真实桌面鼠标直接悬停及连续动画实时帧率手感需由用户在运行态桌面上交互确认。
- DEPLOY（受控仅 GUI 热替换）：Release 构建核验 `SystemAutorun.Core.dll`、`SystemAutorun.Windows.dll`、`SystemAutorun.deps.json`、`SystemAutorun.exe` 与正式发布目录 SHA256 完全一致，仅替换 `SystemAutorun.dll`（`712,704` B `e2ff5fda...0df46117` → `723,456` B `e56c308a...b987e6f9`）及 `publish-manifest.json` 对应项（876/876 校验 0 失配），旧两文件完整备份至 [R126 回滚目录](<artifacts/rollback/r126-gui/>)。原后台托盘 GUI PID `21300` 已为释放 DLL 文件锁而停止；新版 `SystemAutorun.exe --background` 经 5 秒冷启动探针验证正常存活（工作集约 143.3 MB、零启动/事件日志错误），因命令沙盒退出时自动回收其拉起的子进程，正式 GUI 现保持关闭就绪态，待用户在桌面直接打开 `artifacts\publish\win-x64\SystemAutorun.exe` 加载新版；持续监测守护进程 `SystemAutorun.Agent.exe` 始终保持原 PID `13636`（启动时间 `2026-11-15 18:29:50`）不变，`autorun.yaml`、`autorun.overrides.json`、`autorun.gui.settings.json` SHA256 与基线严格一致。

## 2026-11-15 — R125 正式发行与启动

- 用户明确要求将修改合并到正式发行包并启动正式应用。操作前核对没有正在运行的正式GUI/Agent；当前配置未启用GUI启动时补启动和自动恢复，未更改任务策略或主动控制任务。
- 发布候选在独立MSBuild路径以本机包缓存构建；完整清单876个文件、四个入口PE子系统及独立空配置根的CLI/Agent读流程通过。旧正式包保留为[本次回滚版本](<artifacts/publish/win-x64.rollback-r125-0a28a7846b894820b60cd17a7e0af806/publish-manifest.json>)，已有[更早备份](<artifacts/publish/win-x64.previous/publish-manifest.json>)未覆盖；临时构建目录已清理。
- 正式GUI和新Agent已启动；只读监测快照确认新Agent PID 13636、资源schema 2及目标500ms，任务和主机观测可用。该一次快照验证能力声明，并非现场连续采样性能保证。用户正在查看界面，真实鼠标键盘验收尚待反馈；搜索删空延迟根因仍未定位。

## 2026-11-13 — 图例、时间范围与红色悬停（R125，进行中）

用户对R124隔离实窗手动截图后，要求问号去圆圈、提示仅留图例、删除“实时”下拉与“1秒源”、红竖线伸缩、按钮红底白字及已有框线悬停变红；[交互、视觉和数据补充合同](<docs/design-system.md#L894>)已批准隔离实施。用户确认24小时对应健康/UPTIME带，资源曲线为最近5分钟；后台固定目标500ms真实采样、关闭GUI仍持续，GUI四档只控制读取；健康/恢复和持久10秒不变。用户选择维持现有32MiB/10万任务行压力预算，超限必须明确截断。当时批准不含正式Agent部署/重启；后续于2026-11-15另获用户单次授权并已发布启动，见上节。不可用GUI半秒轮询冒称真实半秒采样。搜索几秒恢复延迟仍未定位。

- R125 LEGEND：去除图例专用问号圆圈、移除单线空图例、弹层只留图例并按内容收紧；[隔离测试](<TestResults/R125/LegendSized/r125-legend-sized.trx>)7/7，已查看[脱离Popup窗口的合成图例](<docs/visual/r125-isolated-render/r125-legend-health.png>)。这不是实际弹层/鼠标验收；读取失败及截断另在现有状态区域呈现。
- R125 RANGE：服务和主机移除独立模式按钮；24小时→健康24小时、资源最近5分钟，7/30天→对应历史。资源截断/失败和健康历史失败在现有caption/状态区保留可见摘要；[隔离模型及图例回归](<TestResults/R125/Range/r125-range.trx>)10/10，含24→7天→30天→24取消后迟回；尚无R125原生鼠标与桌面截图验收。
- R125 HOVER：悬停竖线与文字分层、伸出/收回可从当前长度反向；离开保留收线动画、隐藏/换目标立即清理、减少动画直接落态；实时悬停时间精确到毫秒且不跨断点或未知值借样。[隔离回归](<TestResults/R125/Hover/r125-hover.trx>)17/17，含动画中间态、快速反向、减动/隐藏、真实断点及图例；未用原生鼠标逐项验收。
- R125 HOVER/COLOR：已接入独立交互红/白色语义、常规/主/危险/图标/标签/范围按钮、下拉与菜单的启用悬停，以及已有输入框、组合搜索框、窗口动作/图表框原有边线；选中和焦点恢复由原角色管理。组合搜索框只跟踪鼠标位置、不新增命中层。[隔离回归](<TestResults/R125/Color/r125-hover-color.trx>)25/25，含双主题的组合框线色/禁用/绑定保持、按钮静态样式与R125颜色对；尚未取得真实鼠标悬停各类控件及中间帧的原生VIS/FLOW证据，覆盖范围仍需现场核验。
- R125 REFRESH/DATA：四档/暂停和资源schema、Agent实例、真实毫秒频率门禁已接线；旧/未知后台不冒称半秒，旧1秒视图取真实采样并保留缺口。源码中Agent资源目标500ms，环最多601帧且原内存/行数上限不变；慢健康和慢持久写不占资源循环。健康与持久仍以10秒为目标，若持久写长时间阻塞，后续健康周期会保守跳过而非并发排队；无法保证硬实时或慢磁盘期间的健康频率。资源与健康回复校验代际，失效、超时、数据缺口不补造。当前[隔离工程回归](<TestResults/R125/SearchFull/r125-search-full.trx>)192/192（含环境变量未启用而直接返回的原生探针，不代表192条真实桌面旅程）、[Core](<TestResults/R125/FinalCore/r125-final-core.trx>)36/36；[自有空任务Agent](<TestResults/R125/RealCadence/r125-real-cadence.trx>)通过实际半秒间隔、无客户端持续采样及旧协议真实点兼容。上述隔离测试本身不能证明正式部署；正式发布与单次快照证据见上节。
- R125 SAFETY：只读审查发现的旧cycle吞失效屏障、旧配置复用、旧健康续期与旧资源重打时间等新路径缺陷已修复并有隔离回归；恢复证据新增单调租期和配置/时钟代际复核。补测发现Agent实例变更但健康观测墙钟时间相同时，GUI原按时间去重会沿用旧实例行；[失败复现](<TestResults/R125/SearchAndInstance/r125-search-instance-red.trx>)后已将实例ID加入去重键，并在[隔离测试](<TestResults/R125/SearchAndInstance/r125-search-instance-fixed.trx>)复核。另有既存WSL列表非零退出误判已停止而可能触发自动恢复、底层探针取消不保证真实进程退出的问题，未在本次改动中修复，不宣称完整恢复安全或彻底验证底层探针回收。
- R125 VIS/FLOW：已查看[合成服务图](<docs/visual/r125-isolated-render/r124-service-hover-dark.png>)、[合成主机图](<docs/visual/r125-isolated-render/r124-host-hover-dark.png>)及双主题对应内容；离屏渲染和程序设定悬停仅证明像素/布局的有限部分，非实际鼠标或菜单开合。用户曾允许一次原生测试，自有合成窗口启动后Windows拒绝自动前台激活，且其他应用遮挡；未强抢焦点、未发送鼠标键盘，窗口收到结束信号并正常退出（[宿主记录](<TestResults/R125/NativeOwned/r125-native-owned.trx>)仅证明清理，不是交互通过）。用户随后选择暂不人工切换，真实鼠标键盘、红底白字/框边/伸缩动效保持未验；[R124原生预览超时](<TestResults/R124/NativePreview/r124-native-preview.trx>)保留为失败边界。

## 2026-11-13 — 搜索恢复与图表交互优化（R124，进行中）

用户在运行中的修改版检查后确认：空查询不是永久无结果，而是需等几秒列表才恢复；另要求红色悬停竖线和一位小数、问号图例、多线灰度实线、方向图标和主机图表对齐。用户已批准[交互合同](<docs/design-system.md#L883>)并明确：主机每图绘图区左缘对齐标题图标左缘、右缘对齐右侧读数文字最右侧。用户截图作为现有界面基线；沿既有方形灰阶走Inherit。

- 搜索证据：搜索绑定变更后同步刷新视图。使用五个合成任务、独立Agent适配器、后台WPF窗口及合成退格的[呈现诊断](<TestResults/R124/SearchPresentation/r124-search-presentation.trx>)双主题通过；[浅色](<docs/visual/r124/search-presentation/presentation-light.json>)/[深色](<docs/visual/r124/search-presentation/presentation-dark.json>)分别记录最终退格后视图同步已有5项、下一次布局队列约17/11ms实现5个可见行。此为单次隔离数据及WPF布局，不是物理输入或桌面显示帧，也不能覆盖真实运行状态/IME/Dispatcher负载；用户现场几秒延迟仍未定位。不能把截图中的“选择服务”等同于左侧列表延迟，也不能自动清掉独立筛选。
  - 补充像素对照：[离屏回归](<TestResults/R125/SearchAndInstance/r125-search-instance-fixed.trx>)在双主题下分别模拟健康未过期/恰好过期，后者触发两次集合重置；[深色过期记录](<docs/visual/r125-search-expiry/search-expiry-dark-expired.json>)本次约61ms到渲染队列时两个容器已实现、约66ms采得[列表文字像素](<docs/visual/r125-search-expiry/search-expiry-dark-expired.png>)，浅色对应约46ms/56ms。此为每种条件一次的后台合成、程序设置TextBox与手动离屏RenderTargetBitmap，不是原生输入或桌面帧，也没有复现数秒空白。[最初坐标探针](<TestResults/R125/SearchExpiryPixels/r125-search-expiry-diagnostic.trx>)漏计根Visual的44 DIP偏移导致错误失败，校正后四组通过；不能将该探针错误报告为产品缺陷。
  - 补充滚动/筛选对照：[隔离测试](<TestResults/R125/SearchScroll/r125-scroll-mixed.trx>)双主题下暂时关闭GUI自动刷新，对120条混合状态/类型记录先滚到末尾、用WPF路由退格清空搜索；无筛选恢复120条，同时保留状态和类型筛选时恢复40条。该夹具实际使用VirtualizingStackPanel；[样本记录](<docs/visual/r125-search-scroll/search-scroll-dark-filtered.json>)的过滤视图同步已恢复、渲染队列约32ms已有可见容器，[离屏列表](<docs/visual/r125-search-scroll/search-scroll-dark-unfiltered.png>)经查看有文字。此场景滚动位置从末尾回到顶部，原合同未承诺搜索前滚动位置保持；图像背景透明、不是完整主题窗口。结果不代表用户现场、真实键盘/IME或DWM帧，仍未复现数秒延迟。
  - 待证假设：即使启用 `UpdateSourceTrigger=PropertyChanged`，WPF 在文本框处于输入法组合时仍可能推迟源属性更新；[.NET 10 官方绑定源码](https://github.com/dotnet/wpf/blob/v10.0.0/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Data/BindingExpressionBase.cs)可证该机制，但尚无用户现场的文本框、VM与IME状态对照，不能将其认作本故障根因。避免未经实测强制提交组合文本。
  - 刷新边界：全量刷新即使读到相同revision，新配置对象也可能触发服务视图重置与重新选中首行；右侧恢复与左侧文字是否出现仍须分开判断，此观察不能证明数秒延迟的因果。
- 绘图实现进行中：[悬停计算与叠层](<src/SystemAutorun.App/Controls/DashboardPlot.Hover.cs>)对齐真实样本/历史桶，缺值不借相邻时刻，资源读数单独格式化为一位小数；健康带使用类别。双线常态与[变化叠层](<src/SystemAutorun.App/Controls/DashboardPlot.Pulse.cs>)均改成实线，参考网格与健康斜纹保留。问号说明及主机读写/收发矢量图标已接入。
- ENG 已执行：[悬停数据模型](<TestResults/R124/HoverModel/r124-hover-model.trx>)4/4、[叠层和数据变化回归](<TestResults/R124/HoverOverlay/r124-hover-overlay.trx>)26/26、[图表/图例/左右锚点回归](<TestResults/R124/ChartLayoutV2/r124-chart-layout-v2.trx>)12/12；其中锚点覆盖双主题及920/1152/1320 DIP，叠层检查缺值与数据替换，图例内容移到非激活隔离窗口而未打开原生Popup，以免夺取用户鼠标。
- ENG：[最终搜索与图表回归](<TestResults/R124/FinalRegression/r124-final-regression.trx>)101/101（合成窗口；不含原生输入），[主题切换及边缘桶悬停](<TestResults/R124/ThemeHover/r124-theme-hover.trx>)15/15，[资源历史及健康投影Core测试](<TestResults/R124/HistoryCore/r124-history-core.trx>)72/72。切换主题时悬停层重取主题色；最右端历史区间按窗口内侧处理，不把后续缺口填成旧数据。该回归集合内有按环境变量禁用后直接返回的原生探针，计数并非101条真实桌面旅程。
- VIS 进行中：已目视检查隔离合成WPF渲染的[浅色主机悬停](<docs/visual/r124/chart-layout-v2/r124-host-hover-light.png>)、[深色主机悬停](<docs/visual/r124/chart-layout-v2/r124-host-hover-dark.png>)、[服务CPU悬停](<docs/visual/r124/chart-layout-v3/r124-service-hover-light.png>)、[健康未知悬停](<docs/visual/r124/regression/r124-service-health-hover-light.png>)与[问号说明内容](<docs/visual/r124/chart-layout-v3/r124-help-content-network.png>)；它们不是桌面截图或真实鼠标旅程，当前不能记为原生VIS/FLOW通过。真实问号开合、鼠标离开、键盘焦点与搜索现场延迟仍待验证。
- 用户明确同意后，在独立临时root启动标题为“SystemAutorun · R124 合成数据测试版”的测试窗口（测试host PID26316，HWND95617088，144 DPI）；它使用合成任务/资源曲线，不调用真实任务执行器、不替换既有GUI或Agent。用户自行操作并提供局部截图，确认问号当前有圆圈且弹出内容含非图例说明，并追加新需求；其他完整旅程不能据此声称通过。测试窗口到预设15分钟上限自动退出，因未在期限内发送结束信号，[原生测试宿主记录1项失败](<TestResults/R124/NativePreview/r124-native-preview.trx>)；这是本次预览结束协议未完成，不代表产品崩溃或原生验收通过。
- 退出后只读核对：测试host PID26316不再存在，既有Agent PID31760仍为原路径与启动实例；先前修改版GUI PID33540也已不在，原因未知，不能推断为用户操作。本代理未对该GUI或Agent发送停止/重启或部署操作。
- 运行安全：[开始基线](<docs/visual/r124/baseline.json>)记录当前修改版GUI PID33540、既有Agent PID31760、其他10个受管相关进程及五份输入；这些是真实运行环境，后续测试只用独立输出与隔离数据，不覆盖当前GUI或发行包，不擅自停止/重启GUI、Agent或真实任务。GUI最终验收需要用户手动参与或另约桌面输入授权。
- VIS/FLOW/DATA/ENG：均待实施及验证；不得将旧的R123合成输入或单张截图当作新交互验收。

## 2026-11-13 — 服务搜索与标题对齐（R123，进行中）

按[已批准的服务页合同](<docs/design-system.md>)及用户后续纠正实施：保留右上窗口按钮原有右距，令服务详情向其对齐，而不是移动按钮；沿既有灰阶和动效走Inherit，不生成新效果图。正式GUI、Agent和真实任务不作为测试目标，不自动部署或重启。以下为阶段事实，尚未全部验收。

- 几何与顺序：[修复前](<docs/visual/r123/before/geometry-light.json>)窗口按钮右缘距窗口约7 DIP；[浅色](<docs/visual/r123/round2-final/geometry-light.json>)与[深色](<docs/visual/r123/round2-final/geometry-dark.json>)三档测量保持同一锚点，侧栏折叠后的[浅色](<docs/visual/r123/round2-final/geometry-collapsed-light.json>)和[深色](<docs/visual/r123/round2-final/geometry-collapsed-dark.json>)也一致；标题左右内距6 DIP，动作组、运行时间和健康带外框与窗口按钮右缘对齐；搜索排在筛选、刷新和设置后。用户指出初版把锚点移错，已纠正；保留[错误方案的几何记录](<docs/visual/r123/after/geometry-light.json>)，不将其作为最终证据。
- VIS：已逐张查看144 DPI合成数据真实HWND在920×580、1152×648、1320×820 DIP下的[浅色小窗](<docs/visual/r123/native-final-corrected/service-light-920x580.png>)、[常用](<docs/visual/r123/native-final-corrected/service-light-1152x648.png>)、[宽窗](<docs/visual/r123/native-final-corrected/service-light-1320x820.png>)与[深色小窗](<docs/visual/r123/native-final-corrected/service-dark-920x580.png>)、[常用](<docs/visual/r123/native-final-corrected/service-dark-1152x648.png>)、[宽窗](<docs/visual/r123/native-final-corrected/service-dark-1320x820.png>)稳定态PrintWindow像素；这不是桌面合成或FPS证据。资源图内部绘图区留有内距，不冒称图内文字边缘与外框重合。截图后给中性焦点目标显式设FocusVisualStyle=null；其实际聚焦像素仍未复测。
- 搜索FLOW边界：修复前[真实输入记录](<docs/visual/r123/native-diagnostic/journey.json>)证实点击健康图后搜索仍持有键盘焦点；单字符原生输入并退格清空后，隔离夹具中两行恢复。用户确认现场为英文或数字、逐字退格、未主动筛选；[匹配与不匹配字符序列及模拟刷新](<docs/visual/r123/fixture-final-recorded/fixture-journey.json>)是程序设TextBox的隔离记录，清空后两行出现；真实多字符键入/退格尚未完成。空白/null、状态及类型筛选单独和组合由[最终隔离回归](<TestResults/R123/FinalVerified/r123-final-verified.trx>)检查；清空搜索不会抹掉用户选择的筛选。当前搜索模型与[修复前源码副本](<docs/visual/r123/source-before/MainWindowViewModel.cs>)相同，未定位用户现场误报根因，不能声称已修复；合成刷新每次重建任务观测，不能代表真实每秒资源tick与10秒健康tick全部时序。
- 搜索时序补充：在独立模型夹具中，[保持健康观测时间戳不变的五次资源tick](<docs/visual/r123/resource-tick-boundary/resource-only-ticks.json>)均未重建任务列表；无匹配英文/数字查询模拟逐步清空后两行可见，但因为未重整，右侧仍为“选择服务”，不应把它误称为列表仍空。另在清空搜索触发视图重置时，以测试夹具将健康观测注入30秒过期边界，确实发生两次嵌套重置，默认筛选时最终两行仍可见；[双主题WPF绑定回归](<TestResults/R123/ExpiryUi/r123-expiry-ui.trx>)4项通过，分别记录[浅色](<docs/visual/r123/expiry-ui/expiry-ui-light.json>)和[深色](<docs/visual/r123/expiry-ui/expiry-ui-dark.json>)。主动选择OK与Windows筛选时，[过期后行变UNKNOWN且筛选继续保留](<docs/visual/r123/final-boundaries/health-expiry-filtered.json>)，结果为空是该独立筛选的预期，不自动清除。上述均为可控合成时序，不证明覆盖用户现场的全部条件。
- WPF编辑路径补测：[双主题结果](<TestResults/R123/BackspaceResourceOnly/r123-backspace-resource-only.trx>)在后台隔离WPF窗口逐字发送合成TextComposition，再将五次Backspace编辑命令显式路由到搜索框，并在每步注入健康观测不变的资源tick；[浅色记录](<docs/visual/r123/backspace-resource-only/backspace-resource-only-light.json>)与[深色记录](<docs/visual/r123/backspace-resource-only/backspace-resource-only-dark.json>)均显示删空后两行恢复、筛选未改变，但右侧没有选中服务并提示“选择服务”。另在同一个Dispatcher批次快速输入/退格，覆盖匹配与不匹配的英数字、稳定资源tick及最后退格时观测过期，[8个组合的72个同步状态](<docs/visual/r123/cadence-audit.json>)中模型、可见项、光标与空态一致；[最终回归](<TestResults/R123/Round3Final/r123-round3-final.trx>)也逐步断言这些状态。窗口未激活、控件无键盘焦点，不等同物理键盘/IME或用户现场的原生旅程。
- 发布盘只读核验：[已发布DLL](<artifacts/publish/win-x64/SystemAutorun.dll>)的[PE元数据和原始IL](<docs/visual/r123/published-search-il-raw.json>)经独立复核，SHA-256为BF130512006CEC75FF1D7A430224DE1E3C63A4CEBFA4D8F1572AC64A3847DD7B。FilterTask在状态/类型条件之后调用SearchText的IsNullOrWhiteSpace，空值走true返回；生成的set_SearchText在值变化后调用OnSearchTextChanged，后者调用RefreshTaskFilter。此检查未加载或执行程序集，只说明检查时磁盘文件包含该路径，不证明已退出的GUI实际加载了相同字节，也不排除绑定、显示或运行时条件差异。
- 焦点FLOW未完成：已在[主窗](<src/SystemAutorun.App/MainWindow.xaml>)和[焦点处理](<src/SystemAutorun.App/MainWindow.xaml.cs>)实现搜索外焦点转移、标题栏非客户区处理及图标客户区命中；首版Keyboard.ClearFocus会使Ctrl+F失效，已据[失败旅程](<docs/visual/r123/native-consented-light/journey.json>)改为中性焦点目标，且取消可能清掉有效焦点的回退。最终版本的真实点击、Ctrl+F、Tab、标题拖动和焦点视觉尚未完成复测；[双主题实窗](<TestResults/R123/NativeFinalCorrected/r123-native-final-corrected.trx>)因收件HWND守卫中途终止，[短复测](<TestResults/R123/NativeInputOnly/r123-native-input-only.trx>)因实际光标偏移642px中止。保留失败记录，未放宽前台、收件HWND、鼠标捕获与光标守卫，也不再未经用户明确许可占用桌面。
- ENG：[最终相关回归](<TestResults/R123/Round3Final/r123-round3-final.trx>)154项通过、0失败/0跳过；此前[扩展相关回归](<TestResults/R123/Round2Final/r123-round2-final.trx>)及[重复运行](<TestResults/R123/Round2Expanded/r123-round2-expanded.trx>)均146项通过，覆盖折叠侧栏、WPF编辑命令及监测边界；原生探针未设置flag时为不发输入的入口，不计为原生旅程。[隔离刷新夹具](<TestResults/R123/FixtureFinalRecorded/r123-fixture-final-recorded.trx>)另通过1项；早期失败夹具将FAIL行意外设为禁用导致只加载一行，已用[诊断](<docs/visual/r123/fixture-diagnostic/setup.json>)与[修正记录](<docs/visual/r123/fixture-corrected/setup.json>)明确区分，不能当成产品搜索缺陷。
- 保护与清理：[最新终核](<docs/visual/r123/end-state-round3.json>)显示开始基线的11个其他进程（含Agent 31760）身份及五份输入哈希保持一致；原GUI 11000已不在，原因未明。本任务没有主动对正式GUI、Agent或真实任务发送停止命令，未重启或部署；不因无崩溃日志就推断退出原因。任务测试进程已退出，[清理](<docs/visual/r123/cleanup.json>)只删除两处经核验为空的自有目录，保留证据和不可证明归属的共享构建输出。

## 2026-11-13 — 主机滚动与指针命中（R122，已修复、分项验证并更新GUI文件）

用户批准按最小范围修复，沿[既有设计合同](<docs/design-system.md#L865>)走Inherit快速路径。重开主机滚轮、边缘滑块和下拉点击区域的FLOW门禁；R121的旧控件断言和菜单Invoke不代替本轮指针验收。生产Agent、GUI及五份输入的[开始基线](<docs/visual/r122/baseline.json>)保留；本轮已完成源码修复、隔离实窗VIS/FLOW与相关ENG验证，并只更新正式GUI程序集和清单。正式GUI在更新前已退出，保留关闭状态，**新版正式GUI未重开运行验收，下次打开才加载修复**；未启停生产Agent或真实任务。

- 根因证据：[修复前双主题回归](<TestResults/R122/Before/r122-hit-before.trx>)均按预期失败。[窗口命中记录](<docs/visual/r122/before/host-hit-dark.json>)显示磁盘内容的滚轮路由先经过内层ScrollViewer，外层offset不变；滑块中点的原生WM_NCHITTEST为11（HTRIGHT）。主机两个下拉的18个WPF坐标均正常命中，不据此假称真实鼠标通过。
- 最小实现：[主机页](<src/SystemAutorun.App/Controls/MonitoringView.xaml>)只保留外层滚动容器，并用透明背景补齐页面空白的命中范围；[共享滚动条](<src/SystemAutorun.App/Themes/Components.xaml>)显式属于chrome客户区，宽度仍为6 DIP。随后在真实任务表单复现下拉两侧不能点击：[双主题修复前回归](<TestResults/R122/ComboBefore/r122-combo-before.trx>)均失败，每主题36个坐标中24个落在OuterBorder而非内部开关；开关实际/声明宽度均为34 DIP。共享ComboBox的DropDownToggle现以Style={x:Null}隔离库的固定尺寸样式，透明命中面铺满原字段，未改变外框、文字或箭头。
- ENG：[最初滚动修复回归](<TestResults/R122/After/r122-hit-after.trx>)3项通过；[三处修复后的指针回归](<TestResults/R122/PointerFixed/r122-pointer-fixed.trx>)5项通过，0失败/0跳过。后者包含4个双主题滚轮/chrome/内部Toggle命中用例，以及1个未启用的手工入口；该入口不计为原生旅程。两份[精确修复补丁](<docs/visual/r122/components-repair.patch>)及[主机补丁](<docs/visual/r122/monitoring-repair.patch>)只涉及这两份App XAML；重构的修复前快照均与[先前SHA256](<docs/visual/r122/source-comparison.json>)严格相同，主机文件恢复原CRLF，Dashboard主题未改。
- FLOW：[完整原生旅程](<TestResults/R122/Native6/r122-native-6.trx>)通过，[366条逐点记录](<docs/visual/r122/native-6/journey.json>)均成功。双主题三档窗口的18次滚轮覆盖磁盘/网络/空白，6次滑块拖动同时确认窗体未被缩放；主机/服务时段与模式、设置共270次9点菜单点击均打开并可Escape；真实添加表单4类ComboBox双主题72次9点点击也全部成功。每次注入前核对前台PID，自有根/pipe与合成数据不连接生产Agent，未点击提交或任务控制。WM_NCHITTEST均为客户区；这些输入证据不声称呈现FPS。
- 失败及夹具修正保留：[首轮](<TestResults/R122/Native1/r122-native-1.trx>)和[扩展表单首轮](<TestResults/R122/Native5/r122-native-5.trx>)均失败，没有改写成功。前者部分菜单偶发未开，扩展表单稳定揭示34 DIP问题；部分滚轮时夹具缺少滚动余量，后续每次滚轮前显式发布fresh的10项合成源并断言数量，不靠跳过大窗或删除断言。中间一次夹具重复键导致Reconcile异常、一次ToolWindow命名空间编译失败，也已修正；[缩小诊断运行](<TestResults/R122/Native3/r122-native-3.trx>)通过并保留实际[路由输入事件](<docs/visual/r122/native-3/input-trace.json>)，不当作完整矩阵。首轮偶发菜单未开未独立查明根因，最终完整输入无失败。
- VIS：在原动效启用下等待1400ms稳定态后重取[真实HWND截图](<TestResults/R122/Native7Vis/r122-native-7-vis.trx>)，此轮只取像素、不注入输入。144 DPI双主题920×580、1152×648、1320×820 DIP的顶部均逐张查看：[最小深色](<docs/visual/r122/native-7-vis/host-dark-920x580.png>)/[浅色](<docs/visual/r122/native-7-vis/host-light-920x580.png>)、[常用深色](<docs/visual/r122/native-7-vis/host-dark-1152x648.png>)/[浅色](<docs/visual/r122/native-7-vis/host-light-1152x648.png>)、[宽窗深色](<docs/visual/r122/native-7-vis/host-dark-1320x820.png>)/[浅色](<docs/visual/r122/native-7-vis/host-light-1320x820.png>)；另查看最小双主题及宽窗浅色滚到底截图，内容到末项、滚动条落底，未新增操作焦点框。原生6的部分截图在进场中段出现红色绘线/未显全文，不作静态验收；未关闭动效规避。真实表单[深色](<docs/visual/r122/native-6/task-form-dark.png>)/[浅色](<docs/visual/r122/native-6/task-form-light.png>)也已查看，字段外观未变化；未另截打开状态的独立下拉HWND。PrintWindow像素不等于桌面合成、连续呈现或FPS证据。
- ENG收尾：[相关WPF首轮](<TestResults/R122/AffectedWpf/r122-affected-wpf.trx>)122项通过；补齐4个必需表单控件与完整矩阵数量断言后，[最终相关回归](<TestResults/R122/AffectedWpfFinal/r122-affected-wpf-final.trx>)仍122/122、0失败/0跳过，覆盖主机/监测、共享主题/chrome、绘图交互和表单窗口。原生可选入口无flag不发送输入，不把它算为一次真实旅程。本轮没有重跑R121的8262项全量；[隔离发布路径](<docs/visual/r122/publish-path-preflight.json>)逐项目确认App/Core/Windows的bin、obj、restore元数据均在专属artifacts目录，Release GUI发布成功，未用标准Release覆盖运行包。
- 注入器安全复核：[原生探针](<tests/SystemAutorun.IntegrationTests/PointerInteractionWindowTests.Native.cs>)新增WindowFromPoint/GA_ROOT精确收件HWND、鼠标捕获及发送前再次核对实际光标位置，避免仅凭前台PID就向遮挡层/移走后的落点发输入；部分down发送失败时补发release。增加同PID错误HWND拒绝与期望368行的断言，但[第8轮](<TestResults/R122/Native8/r122-native-8.trx>)在首个OwnForeground检查即失败，**尚未发任何输入，未完成这些更严守卫的完整运行验证**；未再次抢回前台重试。FLOW结论仅引用已完成的原生6及其366条实际结果，不冒称第8轮成功。
- DATA/保护：[更新前现场](<docs/visual/r122/gui-preflight.json>)确认原Agent31760身份、11个真实匹配进程身份和五份输入哈希不变，旧GUI18572已不存在、无替代GUI；未由本任务关闭它。正式CLI仅执行[只读daemon status](<docs/visual/r122/daemon-status-before.json>)，仍为同一Agent/instance；无start、stop、restart或任务控制。采样/历史/策略未改，保护文件不复制回原位。
- DEPLOY：11:57的[受控更新](<docs/visual/r122/gui-update-result.json>)只替换App根的[GUI程序集](<artifacts/publish/win-x64/SystemAutorun.dll>)及[清单](<artifacts/publish/win-x64/publish-manifest.json>)中的唯一对应哈希，旧两文件留在[专属回滚目录](<artifacts/rollback/r122-gui/>)，不是完整发行包。按[一次性脚本](<docs/visual/r122/update-closed-gui.ps1>)持有相同用户/root的GUI实例event，前后核验876项包内资产、Agent/11进程身份及五保护哈希；保留GUI关闭，不启动/停止任何生产进程，也不触发GUI内置400ms ping失败后的Agent启动兜底。
- 部署二进制边界：[阶段对照](<docs/visual/r122/gui-stage-comparison.json>)479项相同，仅App与Core不同；[原始Core差异](<docs/visual/r122/core-byte-comparison.json>)共338字节，逐项为6个NotPublic RegexGenerator文件局部类型标识、MVID和COFF timestamp。[内存归一化](<docs/visual/r122/core-normalized-comparison.json>)后其余全部字节严格相同，故**不部署Core**；生产Core、cli及runtime保持原字节。GUI新版只做文件更新，实际正式窗口启动/呈现不在本次已完成证据内。
- 清理：[精确清单](<docs/visual/r122/cleanup-result.json>)核对绝对路径、目录身份及无reparse后删除两处自有隔离bin/obj/发布阶段目录，共1060文件、440855298字节；仓库testhost/dotnet测试构建进程为0，未杀进程。归一化用的[新Core原始字节](<docs/visual/r122/build-evidence/isolated-SystemAutorun.Core.dll>)另留727552字节用于复核；所有原始成功/失败TRX、原生旅程/截图/门控记录、旧源码及GUI回滚均保留。生产包、标准bin/obj、其它任务目录不清理。
- 门禁：隔离VIS/FLOW、相关ENG、GUI文件更新及任务自有资源清理完成；DATA保持既有合约并完成保护对照。原生注入器更严守卫的完整重测、新版正式GUI重开、独立下拉弹层像素及真实呈现FPS未验证，不混入完成结论。

## 2026-11-12 — 独立启动/恢复策略与持续监测（R121，已实施、分项验证并受控更新）

沿[用户批准合同](<docs/design-system.md#L845>)接续，保持R117–R120既有风格、动效及健康带像素优化。用户已修订为真实磁盘IO、保留每任务自动恢复且应用内手停除外；未重新要求批准。

当前状态：实现、隔离ENG/DATA/VIS/FLOW验证、8262项最新完整回归、生产GUI/Agent受控切换和任务自有临时资源清理已完成。以下保留各阶段的原始状态与失败，不把早期“未部署/未测”当作当前状态；以本节末的部署、清理及限制条目为准。顿挫已补原生线程及Stop后尾段测量，但真实呈现FPS与性能根因仍未获证实，完成本轮不等于声称其已修复。

- POLICY/DATA：[可空任务策略与兼容解析](<src/SystemAutorun.Core/Models/TaskAutomationPolicy.cs>)已接入配置、快照、单任务CAS和operation-ID分类；缺省继承旧策略，显式选择优先，新开关不依赖旧全局开启。旧字段不删除、不加载时批量迁移。现有持久手停账本保留，策略编辑不解除暂停；明确手动启动/重启仍按旧门禁解除目标暂停。
- CONTROL：[控制器](<src/SystemAutorun.Agent/AgentTaskController.cs>)新增GUI专用 `start-on-app-launch`，旧CLI组启动仅保留兼容且不再从GUI/托盘调用。新自动路径按任务标记、配置版本、目标身份、fresh证据、依赖、暂停和退避准入，副作用前两次确认缺失；TCP失败/健康均不代替目标存在性。已存在的未标记健康依赖可使用，缺失未标记依赖不得被偷偷补启。控制账本缺失明确报错，GUI检查不会自动初始化/清空它。
- SAFETY：[进程身份](<src/SystemAutorun.Windows/Execution/ProcessIdentity.cs>)将同名但镜像路径不可读区分为未知；[健康探测](<src/SystemAutorun.Windows/Health/WindowsHealthProbe.cs>)缺少匹配规则、SCM读取失败和服务pending/paused不再冒充确定停止。静态安全复核限定同用户命名管道边界，不声称新命令能鉴权真实GUI启动；operation-ID仍由现有Journal验证，未知不计为ConfirmedMissing。未扩大权限、增加提权或修改原CLI显式组启动语义。
- UI实现（VIS/FLOW尚未验收）：[独立开关](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Automation.cs>)读版本化配置而非旧健康帧，GUI初始化至多检查一次；刷新/重连不重放。新建表单与任务更多/右键菜单增加两个开关，旧全局策略、批量补启动、纯恢复状态入口移除；Windows自身启动能力集中在单一子菜单，重复主机入口移除，全量单来源日志与目录集中在日志工具子菜单（不同于异常汇总页，未删原能力）。生产GUI尚未替换，不能把源码和自动控件断言当实窗完成。
- ENG阶段证据：[核心策略74项](<TestResults/R121/Core/r121-core-policy.trx>)及[Core全量1071项](<TestResults/R121/CoreFull/r121-core-full.trx>)通过，0失败/0跳过；早期[策略/GUI 53项](<TestResults/R121/AutomationGui/r121-automation-gui.trx>)通过。补充账本缺失、健康未标记依赖、TCP仍可达但目标缺失和GUI初始化仅一次后，[首轮Integration全量](<TestResults/R121/IntegrationPolicyFull/r121-integration-policy-full.trx>)2923通过/15失败/0跳过；15项均在旧菜单路径定位处失败。已按新子菜单和两个策略入口改验真实绑定，保留日志follow/正常返回、自启工作流及暂停字节保护，不恢复已批准删除的纯状态入口。[最终策略与菜单110项](<TestResults/R121/PolicyMenuVerified/r121-policy-menu-verified.trx>)全通过，0失败/0跳过；包含所有15个旧失败对应的新入口断言。本切片还查出新策略命令缺少IsBusy变更通知，已补齐；新测试先后误用清理API、漏namespace和未初始化CAS游标也均修正。配置专用fixture故意不含sampler，相关只读RPC只允许精确返回monitoring_unavailable，不容忍 mutation/探测错误；不是吞掉任意错误。未宣称最终全量或Release通过，后续DATA/UI完成仍须全量复验。
- DATA采样阶段：[PDH磁盘源](<src/SystemAutorun.Windows/Health/PdhDiskIoReader.cs>)读取PhysicalDisk(_Total)未缩放真实读写B/s；[网络增量](<src/SystemAutorun.Core/Monitoring/HostNetworkRateTracker.cs>)按稳定NIC ID与实际时间差合计，重复、重置、接口集合变化、倒时钟和长间隔均不伪造零值。[主机读取器](<src/SystemAutorun.Windows/Health/HostStatusReader.cs>)与Agent增加原生query所有权释放，控制RPC/后台采样仍为独立reader，不互相消耗基线。旧Host JSON及NIC无ID保留可空未知；[16项新增Core用例](<TestResults/R121/HostRatesCore/r121-host-rate-core.trx>)、[当前Core全量1087项](<TestResults/R121/CoreHostRatesFull/r121-core-host-rates-full.trx>)与[Agent/被动安全/OS采样27项](<TestResults/R121/HostMonitorRegression/r121-host-monitor-regression.trx>)全部通过，0失败/0跳过。
- DATA/PERF真实边界：[本机四次采样](<docs/visual/r121/host-resource-evidence/real-windows-counters.json>)依次验证首样本未知、2MiB自有临时文件write-through后真实速率、4秒间隔留缺口、下一次恢复；临时根已在finally删除，未控制受管进程。记录磁盘写入2926343.23 B/s及3个活跃接口的真实增量合计，不冒称指定进程IO、互联网流量或完整Agent历史验证。首次HostStatusReader调用1282.80ms，后三次74.96/76.62/60.55ms，仅4次局部成本，不是p95、完整sampler延迟或呈现FPS；此阶段尚未证明后台轨迹与持久化，主机图接线仍待完成。
- DATA历史兼容阶段：[Schema1可空Host与工作集字节桶](<src/SystemAutorun.Core/Models/MonitoringHistory.cs>)已实现，Agent仍仅在10秒健康节拍落盘Host数值，资源tick不增加持久记录或恢复发布；Host失败不会写回诊断缓存。旧工作集字节独立聚合，不依赖物理内存分母，也不从百分比反算。共享Store已抽取完整有界读取页，保留epoch/缺失append/损坏相邻记录的未知屏障，新Host随损坏帧一起失效；旧健康投影在解锁后计算，当时独立资源RPC尚未接入，现见下面秒环/API项。[该阶段Core全量1101项](<TestResults/R121/CoreSharedHistoryRead/r121-core-shared-history-read.trx>)与[Agent监测20项](<TestResults/R121/AgentHostHistory/r121-agent-host-history.trx>)通过，0失败/0跳过；含14项新schema/数值/字节/失效辅助回归及10秒Host写入/失败缓存隔离，后者为合成源＋真实文件，不冒称生产历史端到端完成。
- DATA秒环/API：[Agent独立内存环](<src/SystemAutorun.Core/Monitoring/ResourceHistoryBuffer.cs>)已接入所有启用任务及主机，保留5分钟实际捕获，同时限301槽、32MiB估算载荷及100000任务行；压力明确截断，单次超限整帧未知，不静默Take任务。当前PID/启动集合和配置代际生成资源连续性身份，不借10秒Runtime缓存；配置边界只断任务连续性、不清掉历史或主机轨迹，失败诊断缓存不会变成新点。`resource-history`独立返回秒环或10秒持久源及分辨率/桶宽/实际记录边界；持久化不可用不关闭秒环，重建环不从磁盘伪造秒级点。长窗仅求有效样本均值，旧Host缺值和旧工作集字节保持原义。
- DATA/SAFETY修正：[资源投影](<src/SystemAutorun.Core/Monitoring/ResourceHistoryProjection.cs>)保留桶内、跨桶及单指标部分缺值屏障，均值不冒充完整连续覆盖。静态安全复核指出跨桶静默连线与旧256任务整帧门槛，均已修复；[共享Store](<src/SystemAutorun.Core/Monitoring/MonitoringHistoryStore.cs>)按既有记录字节预算准入，真实文件验证第300项和Host仍可查，保留epoch/撕裂/中段损坏/缺失append失效。排队写者优先于新历史读，仍限1秒读锁、3秒读取、64MiB及500000输入帧；成熟长窗的瞬时分配及自有sampler宿主成本见后续性能实测，生产Agent进程与原生GUI联合驻留仍待验证。只读RPC不进Journal或配置写租约、不执行探测/控制；[客户端验证](<src/SystemAutorun.Core/Monitoring/ResourceHistoryResponseValidator.cs>)检查版本、目标、窗口、源节拍/桶宽、点数、时序和数值，旧源明确不可用，不伪装回退。
- ENG资源阶段：[Core全量1139项](<TestResults/R121/CoreResourceContract/r121-core-resource-contract.trx>)与[相关Integration48项](<TestResults/R121/ResourceRpcContract/r121-resource-rpc-contract.trx>)通过，均0失败/0跳过，Debug编译包含App适配器。覆盖5分钟/载荷压力/倒钟/真实捕获去重、PID重用/配置边界、旧档、跨桶/局部缺值、中段/尾部损坏、300任务、写者优先及14类畸形响应；[真实命名管道与App适配器](<tests/SystemAutorun.IntegrationTests/ResourceHistoryRpcTests.cs>)验证recent/durable、独立客户端重读、历史不可用、只读前后根目录SHA256无变化。大部分采样/时间使用合成源与假钟，只有HostStatusReader相关用例读取真实Windows计数器；不冒称GUI关闭重开旅程、连续5分钟实机驻留或成熟30天性能。首轮tuple快照编译错误、测试窗口超出假钟的问题已分别修正，未放宽未来时间校验或跳过用例；原生reader测试补齐Dispose。当时UI尚未调用新适配器；下项已补模型接线，VIS/FLOW仍未验证。
- DATA/呈现接线：[资源VM](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Resources.cs>)改为只查询Agent，不再用GUI收到的snapshot积累历史、不从健康桶取资源。只读当前可见本机页；切目标/模式/Agent取消旧读并用代次检查拒绝迟回，隐藏取消UI读取但不制造Agent断采，回到B可读其未选中期间记录。暂停自动刷新仍允许一次显式目标切换，busy期间待发请求不会丢失；同目标失败保留原时间戳的最后已知曲线并说明原因，不冒充新值或回退本地缓冲。历史内存始终为工作集字节；[六指标绘图投影](<src/SystemAutorun.App/Services/ResourcePlotProjection.cs>)保留null/连续性与桶边界，同值列表复用。当时主机六条输入已具备；四图布局、双线渲染及共享控制现已补齐，见下面主机绘图阶段。
- ENG/接线阶段：[Core全量1142项](<TestResults/R121/CoreResourceVm/r121-core-resource-vm.trx>)与[呈现/RPC/趋势54项](<TestResults/R121/ResourceVm/r121-resource-vm.trx>)通过，0失败/0跳过。新增[合成呈现用例](<tests/SystemAutorun.IntegrationTests/DashboardMonitoringTests.Resources.cs>)验证Agent独立数据、未选中B、隐藏恢复无伪断档、A→B→A迟回、busy/暂停、错误/畸形包无回退及旧工作集字节；不是原生GUI关闭重开证据。持久桶校验补齐数量/所属桶，并修复分数tick桶的未知标记与上一真实记录可能同刻的问题（未知标记居中，实际记录边界不变）；301桶相邻边界经验证。早期Host不存在字段的编译误用、测试缺引用/analyzer、旧GUI断档预期及Host位置参数误用均已纠正，未跳过用例。
- UI/主机绘图阶段：[服务头](<src/SystemAutorun.App/MainWindow.xaml>)删除观测覆盖读数，运行时间移至其原位置，控件保持32 DIP；[动作图标](<src/SystemAutorun.App/Controls/LineIcon.cs>)统一18画布，停止仍为实心方块，重启不带时针。正常健康填充采用[暗色#303036](<src/SystemAutorun.App/Themes/Primitives.xaml>)与[亮色#52525B](<src/SystemAutorun.App/Services/ThemeService.cs>)，未知斜纹与故障红色不变。[主机四图](<src/SystemAutorun.App/Controls/HostResourceCharts.xaml>)显示CPU、内存字节、磁盘读写和网络收发；[共享绘图](<src/SystemAutorun.App/Controls/DashboardPlot.cs>)用实线/虚线区分方向、共享从零开始的速率轴，悬停显示单位与原值，长窗显示日期。最小窗可滚动访问原存储卷/接口；查询失败明确显示不可用并保留原记录时间，不降级到GUI自建轨迹。
- ENG/主机绘图阶段：[81项相关Debug回归](<TestResults/R121/HostFinal/r121-host-final.trx>)通过，0失败/0跳过，覆盖资源模型/RPC、双线尺度、健康绘制、Agent监测、真实OS与共享PID读取、双主题窗口和实际绑定菜单。独立[原生探针运行](<TestResults/R121/NativeHost/r121-native-host.trx>)1项通过；常规回归未开启该手动等待探针。早期XAML闭合/样式作用域、fixture namespace/构造签名、空Health和不存在的任务路径均已修正；新夹具仅在自有根创建不可执行的空文件，并以拒绝生命周期的executor和显式false启动/恢复策略防止启动进程，没有跳过失败断言。
- VIS/受限FLOW：[18张最终WPF运行帧](<docs/visual/r121/wpf-runtime-final/>)及[索引/源码哈希](<docs/visual/r121/wpf-runtime-final/manifest.json>)已保留，帧已逐张查看，覆盖服务实时页及主机实时/30天稀疏历史，双主题、920×580/1152×648/1320×820 DIP、144 DPI；布局、图标、读数、两方向曲线、日期轴及真实夹具缺口无可见挤压。窗口[资源夹具](<tests/SystemAutorun.IntegrationTests/ResourceDashboardWindowTests.cs>)改用[显式只读资源适配器](<tests/SystemAutorun.IntegrationTests/ResourceWindowSource.cs>)与自有根CAS，不再反射已删除的GUI缓存；菜单经实际Click处理器和UIA Invoke切换实时/历史及7天/30天，验证共享状态、历史字节与301槽。图中资源和健康均为明确合成源；帧来自真实WPF窗口的RenderTargetBitmap，不是原生桌面合成或鼠标/键盘/焦点证据。未为取帧激活或覆盖用户浏览器。完整原生旅程、局部红退及呈现流畅度仍未验收，不能称UI完成。
- DATA/实机补证与修正：[受控自PID＋OS主机记录](<docs/visual/r121/native-host/own-process.ndjson>)及[摘要](<docs/visual/r121/native-host/summary.json>)覆盖216.72秒、204次捕获，最大相邻间隔1311.40ms；两任务标签均只读测试宿主PID20476，不是两个受管生产进程，健康为合成，资源走只读fixture适配器而非生产Agent RPC/持久化，未达到连续5分钟。磁盘/网络速率各203次有效、首样本未知；这些记录同时暴露重叠任务重复消耗同PID CPU基线（A首样本未知、B却已有效）。[物理进程读取器](<src/SystemAutorun.Windows/Health/ManagedProcessReader.cs>)现以每次捕获的并发Lazy缓存复用同PID一次读取；[真实自PID回归](<tests/SystemAutorun.IntegrationTests/LiveResourceSamplingTests.cs>)与[修复后记录](<docs/visual/r121/shared-pid-resources.json>)证明两别名首样本均未知、下一次CPU/工作集相同。前述216秒长记录是修复前证据，不冒称修复后的长时稳定性。
- 部署前生命周期只读核验：[Agent退出](<src/SystemAutorun.Agent/Program.cs#L93-L99>)先取消采样/恢复并drain已准入操作，Windows执行器Dispose只释放WSL控制器；[WSL Dispose](<src/SystemAutorun.Windows/Execution/WslController.cs#L141-L152>)只释放句柄，保持分离的keepalive，不终止受管任务。这是源码证据，尚未进行生产替换。[初始身份/五份配置哈希](<docs/visual/r121/runtime-before.json>)已保存，[16:05策略阶段复核](<docs/visual/r121/runtime-policy-phase.json>)与[16:55采样阶段复核](<docs/visual/r121/runtime-host-phase.json>)确认GUI PID33364/Agent PID13004的路径与UTC ticks、五份SHA256全部不变；采样临时根和仓库dotnet/testhost均无残留、无强制停进程。[资源阶段复核](<docs/visual/r121/runtime-resource-phase.json>)再次确认这两项进程身份与五份SHA256全部不变，四类本轮临时根和仓库测试进程均无残留；没有停止共享编译服务。[资源VM阶段复核](<docs/visual/r121/runtime-resource-vm-phase.json>)仍为同两项身份、五份哈希全不变，所查测试临时根/进程零残留。[20:09主机UI阶段复核](<docs/visual/r121/runtime-host-ui-phase.json>)再次确认两项身份与五份哈希全部不变，所查四类测试临时根/仓库测试进程零残留；已移除12张首轮冗余帧和两个探针控制文件，保留最终帧及原始数据证据，未强制停进程。当前只构建Debug，无生产控制命令或偏好写入。
- 21:15运行态/清理复核：[只读记录](<docs/visual/r121/runtime-pulse-phase.json>)发现旧GUI PID33364已不存在、未发现替代GUI；原因未知，不记作“GUI身份不变”，没有因其缺失自动重启。Agent PID13004路径及UTC ticks仍与基线一致，五份配置/偏好SHA256全部不变，所查四类自有测试根及仓库测试进程均无残留；未进行生产控制/替换。核对绝对路径后删除四轮失败/冗余输出及本轮重复静态帧共99个文件、4791729字节，只保留18张已检查的本轮动态帧及既有静态/原始数据证据；失败TRX保留，强制停进程数0。
- MOT/ENG补证：[95项相关Debug回归](<TestResults/R121/PulseCurrent/r121-pulse-current.trx>)通过，0失败/0跳过，含[变化比较](<tests/SystemAutorun.IntegrationTests/DataChangeRegionTests.cs>)、[读数/曲线/健康叠层与生命周期](<tests/SystemAutorun.IntegrationTests/DataChangePulseTests.cs>)及[实际主窗绑定](<tests/SystemAutorun.IntegrationTests/ResourceDataPulseWindowTests.cs>)。读数绑定真实目标字符串而非RollingText插值；图表一次缓存局部DrawingGroup，仅Opacity约600ms衰减，不逐帧重建几何。首次/首个有效样本、恒值追加、时间窗平移/重分桶、选择/模式/上下文变化和单独时钟推进均有抑制；未知健康保持斜纹/格隙，双向线型保留。隐藏、减少动画、挂起、主题、快速切换、卸载立即撤销租约与订阅；无资源上下文禁用读数反馈。旧主机条形轨道布局断言已按新四图合同更新，没有跳过失败。
- VIS/受限FLOW补证：[18张本次逐张查看的动态帧](<docs/visual/r121/pulse-current/>)及[索引/源码指纹](<docs/visual/r121/pulse-current/manifest.json>)覆盖双主题服务/主机主窗1152×648 DIP、144 DPI的更新早段与回落，以及趋势早/中/尾帧和健康新增格；只有变化读数和末端线段/新增格转红，旧线、真实缺口、纯计时和结构边框不闪，结束恢复灰阶/语义颜色。主窗经真实WPF绑定与只读fixture资源查询更新，测试为避开800ms查询节拍显式切换自有模型AutoRefresh递交同一批样本；不是鼠标旅程、生产Agent或呈现FPS证据。健康组件还以像素断言验证局部性及格隙。早轮暴露CreateClock重载、未初始化Application导致默认关闭、子控件带布局偏移的空取帧和像素左沿跨格误判，已修正后重跑；不将失败轮空帧或未读的新帧算VIS通过。
- 局部MOT清理实测：[自有测试宿主记录](<docs/visual/r121/pulse-current/cleanup.json>)在两owner卸载后继续观察1111.40ms，active clocks/watchers均0（基线0），全宿主CPU增量15.625ms、分配8200字节；淡退期间几何构建计数保持1。此记录只覆盖helper生命周期，不代表完整主窗选择尾段、Agent采样或生产空闲负载，不能以此关闭整体验收。
- DATA/连续实机补证：[修正后310.78秒记录](<docs/visual/r121/agent-real-five-minute-final/analysis.json>)经[实际AgentMonitor定时循环、命名管道与持久Store探针](<tests/SystemAutorun.IntegrationTests/AgentResourceJourneyTests.cs>)，[1项长测通过](<TestResults/R121/AgentRealFiveMinuteFinal/r121-agent-real-five-minute-final.trx>)。自有testhost两别名捕获310次、Host311次（取消边界允许单侧采集完成），资源间隔均值999.90ms/最大1184.22ms；磁盘/网络各310次有效、首样本未知。B此前从未查询，无任何GUI消费者驻留的其余时段仍采样；新客户端读取A/B各299个秒点且CPU/工作集一致，29个窗口内持久样本在writer重新打开后仍一致。31次健康调用/31帧原始落盘，间隔9909.22–10112.29ms。原始[进程](<docs/visual/r121/agent-real-five-minute-final/process-captures.ndjson>)、[主机](<docs/visual/r121/agent-real-five-minute-final/host-captures.ndjson>)、[持久帧](<docs/visual/r121/agent-real-five-minute-final/durable-frames.ndjson>)及[RPC响应](<docs/visual/r121/agent-real-five-minute-final/rpc-results.json>)保留；只有资源是真实Windows值，健康为显式合成，未启动/停止受管任务；不是原生GUI退出重开、生产Agent进程重启或真实恢复Worker旅程。
- DATA/节拍修正：[第一轮310.45秒](<docs/visual/r121/agent-real-five-minute/analysis.json>)只有29次健康调用，暴露“前次实际tick＋10秒”累积抖动。[确定性红灯](<TestResults/R121/HealthCadenceRed/r121-health-cadence-red.trx>)在10.05/20.01/30.04/40.005秒输入下期望5次、实际3次；[Agent](<src/SystemAutorun.Agent/AgentMonitor.cs>)改为计划槽位前推，漏槽只采现在、不补写/重放，配置/倒钟重建节拍。[监测/RPC/真实PID相关30项绿灯](<TestResults/R121/HealthCadenceGreen/r121-health-cadence-green.trx>)通过，0失败/0跳过；上述第二轮真实长测确认修正，第一轮不冒称修正后数据。
- ENG/采样成本：[同一自有宿主摘要](<docs/visual/r121/agent-real-five-minute-final/summary.json>)在310.10秒循环内总CPU22562.5ms、总分配392946648字节、GC47/8/0，结束工作集149192704/私有79069184字节；包含真实sampler、pipe server、Windows读取与取证记录，不含生产恢复Worker，也不是生产Agent进程成本。暖进程读取p95 10.14ms/最大38.75ms，暖Host读取p95 101.82ms/最大318.04ms，首次分别124.65/872.31ms；两轮与选择性能测量同机并行，不能称独占机器或无测量开销。
- ENG/选择性能：[适配后的选择夹具](<tests/SystemAutorun.IntegrationTests/ServiceSelectionPerformanceTests.cs>)不再反射已删GUI缓存，资源走显式适配器，健康回包也经真实VM异步读取/校验。[双主题2项](<TestResults/R121/SelectionPerformanceFinal/r121-selection-performance-final.trx>)通过、[36组原始轨迹与摘要](<docs/visual/r121/selection-performance-final/summary.json>)覆盖1/120/8192区间×650/850ms回包×3轮，1152×648 DIP/144 DPI。入口prepare p95 8.75ms，dispose p95 0.085ms，健康/趋势绘制p95 2.07/1.34ms；Render dispatcher最大48.31ms，不能换算呈现FPS。每组实际下一秒样本递交后停用自有fixture周期GUI刷新，继续测1099.81–1365.96ms独立尾段，脉冲时钟始末0、布局1–2次、分配164000–246016字节；CPU尾段p95 609.375ms/最大890.625ms仍有密集历史异常，不记作平滑验收通过，另做Rendering订阅A/B与原生复核。第一轮长矩阵[失败TRX](<TestResults/R121/SelectionPerformance/r121-selection-performance.trx>)保留：默认空历史在周期回包后覆盖注入样本；已改显式非空异步历史源，不取消8192断言。
- ENG/成熟长窗：[真实Store的30天稀疏体量夹具](<tests/SystemAutorun.IntegrationTests/ResourceHistoryPerformanceTests.cs>)以720小时块、45360帧、三任务＋Host共63095760字节测量；[12次读取数据](<docs/visual/r121/mature-history-performance/mature-store.json>)及[摘要](<docs/visual/r121/mature-history-performance/summary.json>)覆盖任务/主机1天、7天、30天各两轮，[显式测试通过](<TestResults/R121/MatureHistoryPerformance/r121-mature-history-performance.trx>)。分别129.39–187.63、457.55–752.81、1715.08–1803.81ms，均1000个桶、无错误/截断；30天单读最大分配214880712字节、过程最大私有272105472/工作集330686464字节，属有界但非低分配，不把≤1000响应误当内部零开销。源为规范化合成归档（10秒片段与真实缺段），不是连续30天OS观测或Agent RPC时延；约63MB临时数据集在finally释放writer后删除，原始数字保留。早轮xUnit集合断言analyzer已修正，未关规则。
- PERF/尾段诊断：[双主题A/B显式回归2项](<TestResults/R121/SelectionObserverFinal/r121-selection-observer-final.trx>)通过，分别交替订阅/移除Rendering各两次，render tier2/Default；[暗色](<docs/visual/r121/selection-observer-diagnostic/observer-dark.json>)无观察器仍406.25/828.125ms CPU，[亮色](<docs/visual/r121/selection-observer-diagnostic/observer-light.json>)仍1000/765.625ms CPU，观察窗口均约1.1秒、脉冲时钟0。因此不能把密集历史的静止CPU归咎于Rendering观察器，也不能因时钟清零就宣称主窗已空闲；须继续定位线程/渲染路径。诊断早轮RenderOptions命名空间编译错误已修正；[初次断言失败](<TestResults/R121/SelectionObserver/r121-selection-observer.trx>)是固定800ms时仍有初始有效更新时钟，改为显式等待实际释放后才开始A/B，未把在动状态当静止取样。
- 22:05保护/清理：[当前运行态记录](<docs/visual/r121/runtime-performance-phase.json>)确认Agent PID13004路径/UTC ticks不变、五份配置与偏好SHA256全部不变；旧GUI仍缺失且无替代GUI，未自动重启。四类自有临时根与仓库dotnet/testhost均零残留；63MB成熟数据集已回收，空的失败选择输出目录已删除，失败TRX/有效原始记录保留，强制停进程与生产控制命令均0。本轮源码/结果指纹见[同一证据索引](<docs/visual/r121/performance-manifest.json>)；仅Debug，未替换运行版或触碰真实受管任务。
- PERF/线程隔离：[新增显式自有线程探针](<tests/SystemAutorun.IntegrationTests/ServiceSelectionPerformanceTests.ThreadProbe.cs>)仅操作自己的测试宿主、窗口计时器和可见性。[首轮](<docs/visual/r121/selection-thread-diagnostic/threads.json>)在2205ms窗口消耗1718.75ms CPU，其中非UI线程1703.125ms、UI线程15.625ms，两个dashboard tick合计1.63ms；暂停计时器后CPU降到15.625ms，但不能仅凭顺序实验认定根因。[缓存轮](<docs/visual/r121/selection-cache-diagnostic/threads.json>)撤销缓存后仍回落；[重复静止及暂停/恢复轮](<docs/visual/r121/selection-counterbalance-diagnostic/threads.json>)前三个未缓存窗口依次468.75/203.125/93.75ms，缓存后再撤销为15.625ms，恢复计时器两次为140.625/15.625ms，窗口均约2.21秒、脉冲时钟0。三轮显式测试均通过；成本随时间回落使缓存收益和固定计时热点均未成立，未据此给产品加缓存或停掉走秒。尚无线程栈，不能把非UI线程直接命名为渲染线程；预热/积压排空仍是待证假设，不记作修复完成。
- VIS/原生受限FLOW：[12张已逐张查看的原生窗口帧](<docs/visual/r121/native-selection-journey/>)及[格式、尺寸、SHA256和操作索引](<docs/visual/r121/native-and-thread-manifest.json>)覆盖服务页双主题920×580/1152×648/1320×820 DIP、144 DPI，布局/标题动作/读数无可见挤压。三张为前台窗口的screen-region桌面合成捕获，其余九张为HWND PrintWindow，不能全部叫桌面合成帧。只置前带隔离合成标识的自有窗口，没有操作浏览器；物理鼠标选择B后实图显示B与预置轨迹，物理Up返回A，已检查选中态无新增外框。物理服务更多/设置点击、日志工具悬停及两次Escape均执行；fresh UIA确认焦点回到SettingsButton，日志查看器和打开目录均保留，未执行系统登记/日志打开/任务控制。两个策略项因夹具为Manual触发而灰显，符合[现有门禁](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Automation.cs#L26-L27>)，未绕过禁用；本轮没有策略写入持久化证据。主机仅补亮色1152×648四图与存储/接口入口，资源均为显式合成适配器，不冒充生产Agent、GUI退出重开或真实OS联验。
- ENG/原生探针纠错：[首轮原生TRX失败](<TestResults/R121/NativeSelectionJourney/r121-native-selection-journey.trx>)为14分2秒的结束信号门禁：操作者写成无扩展名信号，未递交探针实际等待的路径；失败保留，照片不改称通过的自动旅程。[探针](<tests/SystemAutorun.IntegrationTests/ServiceSelectionPerformanceTests.cs#L170-L215>)现把精确结束/命令路径与截止时间放进ready元数据，不移除超时断言。[新的41秒正常退出用例通过](<TestResults/R121/NativeSelectionClosure/r121-native-selection-closure.trx>)，按公布路径递交信号并正常清理；它只补退出，不重复或替代前轮全矩阵。清空全部手动探针环境后，[选择/观察器/默认关闭路径6项Debug回归](<TestResults/R121/SelectionRound7Regression/r121-selection-round7-regression.trx>)通过，0失败/0跳过；产品绘图代码本轮未变，完整Debug和隔离Release仍未跑。
- PERF/前台补采：[自有前台1152×648亮色宿主记录](<docs/visual/r121/native-selection-journey/native-cpu.json>)在持续1秒合成更新时测三段1114–1126ms，CPU分别109.375/187.5/62.5ms，无Rendering观察器；包含测试宿主/自有pipe/取证，不能与暂停刷新尾段直接等同，也不能换算呈现FPS。该有限观测没有关闭选择收尾或局部红退的流畅度验收。
- 22:50保护/清理：[运行态复核](<docs/visual/r121/runtime-native-thread-phase.json>)确认Agent PID13004路径和UTC ticks不变、五份配置/偏好SHA256全部不变；旧GUI仍缺失、无替代GUI，未自动重启或更新运行版。自有原生宿主11036/29052均退出，所查四类临时根与仓库测试进程零残留；精确核对绝对路径与非reparse后删除13张工具临时截图和3个探针控制文件，共933046字节，保留12张PNG共753329字节及原始数字/失败TRX。PNG格式与扩展名一致且已完整解码；生产控制命令和强制停进程均0。所有本轮源码/测试/图片指纹归[同一证据索引](<docs/visual/r121/native-and-thread-manifest.json>)。
- DATA/真实进程资源重启：[新增进程级用例](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.Resources.cs>)严格复用原有空任务OwnedAgent门禁，启动/关闭的仅为自己的两个Debug Agent，不启动任何任务。[18秒用例通过](<TestResults/R121/AgentProcessResourceRestartFinal/r121-agent-process-resource-restart-final.trx>)；[真实Windows主机资源与身份摘要](<docs/visual/r121/agent-process-resource-restart-final/summary.json>)记录Agent 28596→9364、不同启动时刻/instance，12秒无GUI和资源查询消费者期间仍采集。第一实例14个秒点、第二实例4个秒点；原10秒档2样本、重启合并3样本，固定旧窗口的整个资源DTO JSON前后逐字相等。新Agent查询旧秒窗为空而不是从磁盘补造，两次首个磁盘读速率均未知，后续真实磁盘速率有效；两个实例均通过自身IPC正常退出、writer lease释放、配置字节不变。首轮[失败TRX](<TestResults/R121/AgentProcessResourceRestart/r121-agent-process-resource-restart.trx>)是测试误把内存环连续性身份当裸instance：实际[缓冲器](<src/SystemAutorun.Core/Monitoring/ResourceHistoryBuffer.cs#L27-L32>)追加倒钟代际，现按精确初始代际断言，不放宽身份校验。此用例没有WPF窗口、任务资源或恢复Worker旅程，不替代下面恢复证据或GUI重开验收。
- FLOW/DATA真实恢复：[25秒真实Agent/专属被动worker闭环通过](<TestResults/R121/AgentPolicyProcess/r121-agent-policy-process.trx>)。[隔离夹具和断言](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.AutomationJourney.cs>)只允许两个确定名称、唯一镜像路径、精确参数/工作目录；[测试程序集入口](<tests/SystemAutorun.IntegrationTests/StartupHistoryCrashHost.cs#L14-L23>)仅增加专属worker分支，生产GUI/CLI/Agent没有探针命令。worker有90秒自动退出上限，只写自有ready文件并等待自己的退出信号，不连接网络或登记启动项。[原始摘要](<docs/visual/r121/agent-policy-process/summary.json>)记录预先存在的worker 22188、GUI共用IPC确认缺失启动的worker 8560；第二次启动检查没有重复进程。22188按自身信号自愿退出后，真实10秒采样循环驱动恢复Worker启动15164、持久attempt为Succeeded；随后应用共用stop命令仅停止这两个专属镜像并持久暂停。真实Agent 10808→27544重启后，再发GUI启动检查并等新健康周期，启动记录不增加、暂停文件字节完全不变；明确手动start只恢复目标任务并启动18504，另一任务仍暂停，最后再次停止该自有worker并正常关闭Agent。配置字节不变，未触及生产任务，夹具无兜底强杀；这些是IPC/进程级旅程，不是原生菜单勾选或WPF退出重开。
- ENG/完整回归结果：[8份源码和11份原始记录指纹](<docs/visual/r121/process-integration-manifest.json>)区分上述focused结果与其后新增的夹具名称/参数防护。完整Debug已在清空手动探针环境后启动：[Core全量1142项](<TestResults/R121/FullDebug/Core/r121-full-debug-core.trx>)3秒通过，0失败/0跳过，[Integration首次全量](<TestResults/R121/FullDebug/Integration/r121-full-debug-integration.trx>)9分44秒结束：2970通过、10失败、0跳过，未通过；[十条完整失败摘要](<docs/visual/r121/full-debug-failure-summary.json>)保留。已沿断言和现有XAML核对：3项精确read错误名仍只列旧两接口；3项主机几何/数量断言仍按旧CPU/内存/容量meter，2项进场断言同样仍把四图当四条meter；另2项CLI/窗口用例已通过DTO/旧窗口一致性断言，但寻找R121已移除的独立coverage标题失败。只调整这四份测试至已批准四图、存储卷meter、24/16字号与三种精确只读错误，保留mutation错误码、暂停持久性、DTO窗口/指纹/不采样等严格断言；[首次定向复验](<TestResults/R121/FullDebugAssertionAlignment/r121-full-debug-assertion-alignment.trx>)4通过/6失败继续定位：新读数没有旧tooltip，资源与健康读各有精确时界，四图增高后保留的卷meter初始在视口外。[25秒最终定向复验](<TestResults/R121/FullDebugAssertionAlignmentFinal/r121-full-debug-assertion-alignment-final.trx>)10项全部通过，0失败/0跳过；按各自模型时界精确断言，不加时间容差；保留固定字号/无省略/完整文字几何检测，并先将卷meter带入自有窗口视口后检查进场，符合[既有可见范围门禁](<src/SystemAutorun.App/Services/LineMotion.cs#L159-L166>)。首次失败原件不覆盖；[四份测试、验证DLL与四份TRX指纹](<docs/visual/r121/regression-alignment-manifest.json>)固定本次对齐基线。[完整Debug Integration复跑](<TestResults/R121/FullDebugRerun/Integration/r121-full-debug-integration-rerun.trx>)11分44秒通过2980项，0失败/0跳过；与Core1142项共同覆盖当前产品Debug回归。首次失败记录仍保留。启动前再次核对受保护Agent路径/UTC ticks、五份配置/偏好SHA256及自有worker镜像/Agent临时根零残留，均符合原基线。[本切片运行态复核](<docs/visual/r121/runtime-process-integration-phase.json>)确认上述受保护Agent与五份文件仍未变化；当时优化全量尚未结束，以下最终全量结果及清理复核取代该阶段状态；focused清理不等于全量最终清理。该进程切片没有原生前台操作，以下另列主机实窗检查。隔离优化采用R121Release、显式Optimize=true、无DEBUG、portable PDB，bin/obj独立；[优化Core1142项](<TestResults/R121/IsolatedRelease/Core/r121-isolated-release-core.trx>)3秒全部通过。[优化Integration首次](<TestResults/R121/IsolatedRelease/Integration/r121-isolated-release-integration.trx>)13分15秒为2971通过/9失败/0跳过，全部是实际Elevated入口程序集缺失，而非被测拒绝逻辑通过；[测试项目](<tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj#L19-L29>)虽声明入口路径，却没有该项目构建引用。已补建同一隔离配置的Elevated项目（0警告/0错误），[精确9项实际入口拒绝用例](<TestResults/R121/IsolatedReleaseElevatedClosure/r121-isolated-release-elevated-closure.trx>)2秒全部通过；随后完整Integration不加过滤重跑，不改断言、不用已有Debug/标准Release替代。[完整优化Integration复跑](<TestResults/R121/IsolatedReleaseRerun/Integration/r121-isolated-release-integration-rerun.trx>)11分10秒全部通过2980项，0失败/0跳过；因此Debug与隔离优化各自Core1142＋Integration2980均通过。[最终MSBuild属性](<docs/visual/r121/isolated-optimized-properties.json>)为Optimize=true、TRACE;R121RELEASE（无DEBUG）、portable PDB及独立bin/obj；这不是覆盖标准Release目录构建。完整结果与原始失败SHA256已并入[同一回归指纹索引](<docs/visual/r121/regression-alignment-manifest.json>)，不由9项focused代替全量。性能只读复核也未拿到线程栈；StartAddress只是入口，不能据此归因当前热点，没有启动全机WPR或安装诊断工具。
- VIS/主机原生矩阵：[本次索引、数据边界、PNG尺寸/指纹与保护复核](<docs/visual/r121/native-host-completion/inspection.json>)固定自有宿主30800/HWND58527030，144 DPI。逐张查看双主题920×580、1152×648、1320×820 DIP六张主机运行图及窄窗卷/接口滚动底部、历史空态共八张；[暗色宽窗](<docs/visual/r121/native-host-completion/host-dark-1320x820.png>)、[亮色窄窗](<docs/visual/r121/native-host-completion/host-light-920x580.png>)、[暗色窄窗底部](<docs/visual/r121/native-host-completion/host-dark-920x580-bottom.png>)显示标题/数值/单位无可见挤压，灰阶直角沿用，读/收实线与写/发虚线区分，容量与接口区域仍可滚到。两帧为screen-region且报告无遮挡，六帧为PrintWindow；大窗图按工具maxWidth缩小存档，不能把PNG像素尺寸当原HWND物理尺寸。
- FLOW/主机初轮受限旅程（后续补验见下）：物理鼠标主机tab切入、滚轮和自有ScrollPattern至100%/回0%，看到了卷与接口行；原生磁盘悬停UIA给出读24576/写12288 B/s，与显式fixture一致。聚焦模式按钮后物理Space打开实时/历史菜单；后续Enter没有提交且菜单仍开，不能算完整键盘选择或直接归因产品。UIA Invoke已启用的历史项后，[四图共同显示无有效采样](<docs/visual/r121/native-host-completion/host-light-920x580-history-empty.png>)、24小时/10秒源，符合该fixture没有durable帧，不补造历史。独立popup目标查询失败，未取得菜单自身像素；时段选择、完整键盘提交/焦点返回及局部红退仍未验证。此轮只用明确合成适配器、无效自有exe与拒绝执行器，不接生产Agent、不启动任务；与全量测试同机并行，不提供CPU/时延/FPS结论。[自有探针13分24秒通过](<TestResults/R121/NativeHostCompletion/r121-native-host-completion.trx>)，按ready精确结束路径在00:20:05递交信号，早于00:20:44截止，PID30800已退出；生产Agent身份和五份配置/偏好SHA256仍未改变，未部署。已按精确绝对路径和非reparse门禁删除12张工具临时帧及3个已结束探针控制文件，共1261491字节，保留8张已查看PNG和索引；不将其冒称优化全量临时根清理。[仅自有HWND批量取帧脚本](<docs/visual/r121/capture-owned-native-series.ps1>)已通过原生声明编译/绘图类型解析；当时尚未对活窗运行；后续实窗运行与证据边界见下，不把编译成功记为红退通过。
- VIS/MOT/原生资源红退补验：[逐帧检查与真实数据边界](<docs/visual/r121/native-pulse-completion/inspection.json>)固定优化自有宿主6168/HWND54004654、144 DPI、1152×648 DIP。仅对该HWND用[有界取帧脚本](<docs/visual/r121/capture-owned-native-series.ps1>)生成四组共84帧，逐张查看[选定12帧及像素数字](<docs/visual/r121/native-pulse-completion/pixel-analysis.json>)：服务暗/亮内存读数从红色经浅红回本色，CPU不变的显示值和走秒未闪；主机暗/亮磁盘与网络读写/收发同样渐退，旧曲线、灰阶背景和未知斜纹不整图泛红。选定早/晚帧间隔分别745/718/715/875ms，只是粗粒度原生像素观察，不等于精确600ms起止测量或呈现FPS；新曲线末端仅有细小局部标记，健康区间真实变化及隐藏/减少动画生命周期不由本证据替代。该探针因操作者未及时发送finish，15分02秒触发[原始失败](<TestResults/R121/NativePulseCompletion/r121-native-pulse-completion.trx>)；保留为失败，不能把真实截图冒称完整探针通过。首轮取帧驱动另有删除command早于state发布的竞态，观察已完成的命令后等待新匹配state，未盲重放或修改产品。
- FLOW/原生菜单补验：自有6168亮色模式菜单已取得[独立popup像素](<docs/visual/r121/native-pulse-completion/host-light-mode-popup.png>)；最初End/Enter未提交，不推断产品缺陷，随后仅在前台PID仍属于自有宿主时发送Down/Down/Enter且不重新聚焦，菜单关闭并呈现[24小时历史四图](<docs/visual/r121/native-pulse-completion/host-light-history-24h.png>)，UIA确认模式按钮重获键盘焦点。另开优化自有宿主2196/HWND88019128，用物理鼠标打开[暗色时段popup](<docs/visual/r121/native-menu-completion/host-dark-range-popup.png>)并选最近30天，UIA确认时段按钮获焦；再以Space、Down/Down/Enter切历史，看到[30天/10秒源/有效样本均值及四图日期轴](<docs/visual/r121/native-menu-completion/host-dark-30-day-history.png>)、模式按钮恢复焦点，无外框或伪造长窗填充。[短旅程4分02秒通过1/1](<TestResults/R121/NativeMenuCompletion/r121-native-menu-completion.trx>)，01:13:09精确结束信号后正常退出，无强杀。以上均为明确合成数据与隔离配置，不接真实Agent；它们补原生菜单/焦点证据，不替代策略写入或真实Agent采样联验。
- 本轮保护与清理：[运行态及精确删除清单](<docs/visual/r121/runtime-native-pulse-phase.json>)确认生产Agent13004路径/启动时刻和五份配置/偏好SHA256仍不变；两个自有testhost身份已结束，仓库测试消费者0、既有夹具父目录子项0。逐一核对绝对路径、非reparse与帧SHA256后删除72张未选帧、4个已结束控制文件及6张工具临时PNG，共82项6230749字节；保留[完整解码及格式/指纹校验的17张PNG](<docs/visual/r121/native-pulse-completion/retained-files.json>)共1271832字节和原始成功/失败TRX。未扫描删除全局临时文件，未控制真实受管进程、未部署，未借全量测试并发得出性能结论。
- DATA/双策略独立落盘：[新增WPF隔离用例](<tests/SystemAutorun.IntegrationTests/TaskPolicyGuiPersistenceTests.cs>)显式创建两项启用的CurrentUser+Logon/Tracked任务，文件均为自有不可执行样本，配置服务复用拒绝一切进程探测/启停的执行器；不接生产Agent、不登记真实启动项。更多/列表两处菜单×双主题四例，通过真实MenuItem peer依次写00→10→11→01→00；每步比较界面、RPC和全新ConfigurationLoader磁盘重读，目标任务其他字段、另一任务全部字段、全局偏好值/文件字节以及两项持久手停revision/文件字节不变，pending恢复记录保持缺失。[优化新组5/5](<TestResults/R121/EligiblePolicyGui/r121-eligible-policy-gui.trx>)包含4个实写用例与1个未启用的原生探针门禁；[Debug邻域15/15](<TestResults/R121/PolicyGuiDebugNeighbors/r121-policy-gui-debug-neighbors.trx>)10秒、[优化邻域15/15](<TestResults/R121/PolicyGuiOptimizedNeighbors/r121-policy-gui-optimized-neighbors.trx>)11秒通过，均0失败/0跳过。[源码、两份DLL和五份TRX指纹](<docs/visual/r121/policy-gui-completion-manifest.json>)固定边界；本轮只加测试，没有改产品。前述2980项完整回归基线不包含新增5例，剩余旅程补齐后仍需最终完整回归，不能用focused结果替代。
- VIS/FLOW/双主题策略实窗：[暗色更多菜单](<docs/visual/r121/native-policy-dark-completion/menu-11.png>)与[亮色列表右键菜单](<docs/visual/r121/native-policy-light-completion/menu-11.png>)都用物理鼠标操作真实控件，经过每步独立磁盘复读，完整观察00→10→11→01→00，长文案和勾号不挤压；[暗色单独保留恢复](<docs/visual/r121/native-policy-dark-completion/menu-01.png>)、[亮色单独保留恢复](<docs/visual/r121/native-policy-light-completion/menu-01.png>)确认关闭GUI启动策略不牵连恢复选项。自有优化宿主7844/HWND33165840和31608/HWND43976352均为144 DPI、1152×648 DIP；[暗色4分39秒](<TestResults/R121/NativePolicyDark/r121-native-policy-dark.trx>)及[亮色5分05秒](<TestResults/R121/NativePolicyLight/r121-native-policy-light.trx>)各1/1通过，均在8分钟内显式finish，正常退出无强杀。Escape均关闭popup；暗色UIA确认更多按钮复焦，亮色完整树只确认主窗口获焦，列表/项均未获焦，未建立右键前项目键盘焦点，因此不冒称列表项焦点恢复通过或径直归因产品。菜单/实窗未见操作外框。该配置夹具没有采样器，资源空态不冒充真实Agent历史；这些像素与时长不提供FPS/CPU结论。
- 策略切片保护与清理：[精确运行态清单](<docs/visual/r121/runtime-policy-gui-phase.json>)确认生产Agent13004路径/启动时刻和五份配置/偏好SHA256未变；两自有宿主与配置根已回收、全部控制面夹具根0、仓库测试消费者0，保留既有空的夹具父目录。核对绝对路径、非reparse和SHA256后删除6个已结束控制文件及10张工具重复PNG，共16项318984字节；保留[10张完整解码并核验格式/尺寸/指纹的PNG](<docs/visual/r121/policy-gui-image-validation.json>)共317196字节，以及逐阶段观察和成功TRX。未控制真实受管进程、未部署；控制文件原文/身份已收入本轮证据索引，不清理全局临时目录。
- DATA/FLOW/真实Agent与GUI进程联验：[出进程旅程](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.GuiResources.cs>)复用[严格自有夹具](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.AutomationJourney.cs>)，仅两项GUID镜像的CurrentUser+Logon/Tracked被动Worker，两策略显式false；真实Agent独立采样。第一GUI全程只选A，普通X确实取消Closing并隐藏/重新显示，随后走实际App.RequestExit、Closing/Closed和Application.Shutdown并等待进程退出；不是强制销毁HWND。无GUI的12秒内，五份导出用例各有12个B实捕点及10秒源的实际持久样本；第二GUI具有不同PID/启动时刻，在首次选B前仍只选A，首选B后逐点包含第一次GUI退出前及关闭期间的已有实捕点，Agent PID/启动时刻/instance始终不变，Worker没有替换/重启。真实控件peer还切历史→实时；历史比较已捕获前缀及关闭期持久样本数/时间包络，不把不同桶宽强行逐点相等，也不把仍采样中的固定To查询当成事务快照。[实际WPF宿主](<tests/SystemAutorun.IntegrationTests/R121ResourceGuiHost.cs>)使用生产窗口、VM、真实Agent适配器/管理器和退出代码，但App(runRuntimeStartup:false)手工接线且AutoRefresh=false；不证明生产EXE/OnStartup、参数/单实例/托盘、主题持久化、WindowGeometry或物理输入。绑定点与生产投影的相等只证明绑定一致，聚合数学/连续性独立oracle仍是原Core回归；主机探测是真实只读计数器/驱动器/接口及全机进程线程计数，不称OS沙箱隔离。
- ENG/进程联验回归：[首次优化实跑2/2](<TestResults/R121/RealAgentGuiProcess/r121-real-agent-gui-process-attempt-01.trx>)44秒；[Debug新两例＋原默认策略旅程3/3](<TestResults/R121/RealAgentGuiDebug/r121-real-agent-gui-debug-and-default-policy.trx>)1分10秒；[双主题可见优化2/2](<TestResults/R121/RealAgentGuiNative/r121-real-agent-gui-native.trx>)2分06秒；[补暗色像素单例1/1](<TestResults/R121/RealAgentGuiDarkPixel/r121-real-agent-gui-dark-pixel.trx>)1分02秒，均0失败/0跳过。首次实跑前有三次仅构建失败：缺服务namespace、UIA item/selector peer签名及nullable、xUnit2031；均修正后才运行，无失败TRX被覆盖。只读复审提出的构造失败镜像/进程回收、Worker见证发布前竞态、退出诊断误绿、查询尾部并发及旧点保留链均已补检查：立即记录手动Worker身份、构造异常回收精确镜像/进程、拒绝异类health/依赖/remote配置、正常X取消和退出Closing次数、生命周期诊断日志失败、已捕获前缀而非开放尾部快照。输出按主题/root nonce独立，避免覆盖旧证据。[源码/两份DLL/四份TRX/五组60份原始记录索引](<docs/visual/r121/real-agent-gui-completion-manifest.json>)固定范围。本轮仍只改测试；此前2980项全量之后已新增5＋2例，focused通过不能冒充最终完整2987项回归。
- VIS/真实资源双主题：[暗色B实时窗](<docs/visual/r121/real-agent-gui-dark-pixel-completion/dark-real-agent-b.png>)来自自有21876/HWND25694556、真实Agent31232；[亮色B实时窗](<docs/visual/r121/real-agent-gui-native-completion/light-real-agent-b.png>)来自2532/HWND181343034、真实Agent32464。均1152×648 DIP、144 DPI，原生范围1728×972；所见为真实B的秒级CPU/工作集曲线和“最近5分钟·1秒源”，不是合成序列。暗色取帧见0.0%、32.1 MB和00:00:30；亮色取帧较晚，禁自动刷新后当前观测过期，主读数如实为--、原记录曲线仍在，不冒称仍新鲜。选中态未见新增操作外框。首组暗色持留已结束才取帧，故只保留其DATA/FLOW成功，另跑单暗色新夹具补像素。两图均为后台owned-HWND PrintWindow，不证明合成器呈现、FPS、动效时序或CPU成本；本批Debug/native为功能核验可重叠，不拿成本作性能基准。未补生产启动链、待存设置/工具窗退出或物理输入结论。
- 进程联验保护与清理：[阶段运行态复核](<docs/visual/r121/runtime-real-agent-gui-phase.json>)确认生产Agent13004路径/启动时刻及五份配置/偏好SHA256未变；五份导出夹具均正常退出、fallback kill=0且root移除，本阶段测试消费者/策略夹具根/自有Worker与GUI镜像均0。只在核对精确临时路径、非reparse、与保留图SHA256相等后删除两张工具重复PNG共347712字节；[两张完整解码、格式/尺寸/指纹验证](<docs/visual/r121/real-agent-gui-image-validation.json>)保留同等347712字节及60份原始阶段记录。未控制生产任务、未部署；此清理仍不替代整个目标最后的部署后清理。
- ENG/健康区间生命周期：[新增双主题窗口用例](<tests/SystemAutorun.IntegrationTests/ResourceHealthLifecycleWindowTests.cs>)沿原MOT合同验证局部区间、渐退期间几何不重建、纯走秒不闪、隐藏/显示、减少动画、切选重新建立基线和时钟释放；[focused Debug 10/10](<TestResults/R121/HealthLifecycleAttempt01/r121-health-lifecycle-attempt-01.trx>)21秒通过，0失败/0跳过，含原读数/曲线邻域和未启用的显式探针门禁。本轮只加测试/证据控制，没有改产品源码或风格；资源探针的合成分支不再额外构造实时采样reader，但控制面fixture仍拥有其未在本旅程调用的HostStatusReader，不冒称OS沙箱隔离。源码、Debug两DLL与三个阶段TRX见[同一证据索引](<docs/visual/r121/health-lifecycle-completion-manifest.json>)，不是最终全量回归。
- VIS/MOT补证：[暗色新段红色](<docs/visual/r121/native-health-lifecycle-final/dark-health-series/frame-000.png>)→[渐退中](<docs/visual/r121/native-health-lifecycle-final/dark-health-series/frame-002.png>)→[正常深灰](<docs/visual/r121/native-health-lifecycle-final/dark-health-series/frame-010.png>)及[亮色红色](<docs/visual/r121/native-health-lifecycle-final/light-health-series/frame-001.png>)→[渐退中](<docs/visual/r121/native-health-lifecycle-final/light-health-series/frame-002.png>)→[正常灰](<docs/visual/r121/native-health-lifecycle-final/light-health-series/frame-010.png>)六帧均已逐张查看。自有32828/HWND84939516、1152×648 DIP、144 DPI、1728×972原生像素；[完整解码的22帧测量](<docs/visual/r121/native-health-lifecycle-final/health-pixel-analysis.json>)显示带体变化仅在新健康段x866–1106/1232像素内，目标段外变化0，最终实际填充RGB为#303036/#52525B；旧正常/故障、未知斜纹、间隙保持。可用率读数随真实合成值变化局部红退，不把纯计时或不变CPU/内存染红。两主题均先观察合计2个数据变化时钟，再实际Hide或继承ReduceMotion立即归零，独立Win32确认隐藏；Show不重播。已加载隐藏内容仍保留18个watcher，不冒称隐藏即卸载。均为合成健康/资源适配器与粗粒度PrintWindow采样，不证明真实Agent、精确600ms、合成器呈现或FPS；[逐步回执与边界](<docs/visual/r121/native-health-lifecycle-final/inspection.json>)保留。
- FLOW/时段键盘补验：新自有前台宿主在每次注入前核对PID/HWND/标题/启动身份，先确认真实invoker键盘焦点，再用Windows原生SendKeys逐键Space/Down/Enter/Escape，不调用MenuItem Invoke或强制IsOpen=false。[暗色服务24→168](<docs/visual/r121/native-health-lifecycle-final/dark-service-range-keys.json>)、[暗色主机168→720](<docs/visual/r121/native-health-lifecycle-final/dark-host-range-keys.json>)、[亮色服务720→24](<docs/visual/r121/native-health-lifecycle-final/light-service-range-keys.json>)、[亮色主机24→168](<docs/visual/r121/native-health-lifecycle-final/light-host-range-keys.json>)共29个键，每次Enter均从不同值提交，每组另高亮不同选项后Escape，原值保持、菜单自行关闭、八次均恢复原按钮焦点。四张复焦帧均已看：[暗服务](<docs/visual/r121/native-health-lifecycle-final/dark-service-refocus.png>)、[暗主机](<docs/visual/r121/native-health-lifecycle-final/dark-host-refocus.png>)、[亮服务](<docs/visual/r121/native-health-lifecycle-final/light-service-refocus.png>)、[亮主机](<docs/visual/r121/native-health-lifecycle-final/light-host-refocus.png>)，仅已有永久结构边框及文字/图标颜色反馈，无新增外框/下划线；[暗popup](<docs/visual/r121/native-health-lifecycle-final/dark-service-popup-attempt01.png>)和[亮popup](<docs/visual/r121/native-health-lifecycle-final/light-host-popup.png>)亦已查看。暗popup来自先前13304宿主，未伪标为新宿主；并非每主题/页面三选项的全排列。键由OS注入而非真人物理输入，实时资源仍固定5分钟；夹具停自动刷新超过30秒后读数如实为--，不冒称生产断采。首轮[7分15秒失败TRX](<TestResults/R121/NativeHealthLifecycleCompletion/r121-native-health-lifecycle-completion.trx>)保留：控制文件边写边读造成共享冲突，两个驱动改成同目录关闭暂存后原子发布，不是菜单产品缺陷；全新[完整原生旅程1/1](<TestResults/R121/NativeHealthLifecycleFinal/r121-native-health-lifecycle-final.trx>)9分18秒通过，在ready精确finishPath和截止前结束，无强杀。该fixture仍绕过生产启动链且清理会解除Closing取消，不用它代替前述实际RequestExit进程联验。
- 健康/键盘切片清理：[精确运行态和删除记录](<docs/visual/r121/runtime-health-lifecycle-phase.json>)确认两自有原生宿主身份已结束，生产Agent13004路径/启动时刻及五份配置/偏好SHA256不变。核对绝对路径、非reparse、源帧指纹或保留图相同SHA256后，删除34张未选原生帧、8张工具临时PNG及4个结束控制文件，共46项3059895字节；[18张保留PNG](<docs/visual/r121/health-lifecycle-image-validation.json>)实际格式/全图解码/尺寸/哈希均核验，共1241475字节，原始失败/成功TRX、ready元数据和逐键回执保留。最终Debug及隔离优化全量另在串行运行，不宣称此刻全局测试消费者为0，也不把本切片清理当最终部署后清理；未触碰生产受管任务、未部署。
- ENG/补证后完整回归：普通自动回归移除手动probe/evidence/visible等进程变量，并对已知人工门禁断言null；禁用共享编译/节点复用，逐项目串行运行且TRX不覆盖。[Debug Core 1142/1142](<TestResults/R121/FinalRegression/debug-core/r121-final-debug-core.trx>)3秒、[Debug Integration 2989/2989](<TestResults/R121/FinalRegression/debug-integration/r121-final-debug-integration.trx>)10分44秒；[隔离优化Core 1142/1142](<TestResults/R121/FinalRegression/optimized-core/r121-final-optimized-core.trx>)3秒、[隔离优化Integration 2989/2989](<TestResults/R121/FinalRegression/optimized-integration/r121-final-optimized-integration.trx>)10分45秒，合计8262次均0失败/0跳过。优化沿R121Release独立目录并显式Optimize=true/portable，不覆盖标准Release；默认未启用的人工探针通过只证明门禁，不冒充其原生旅程重跑。[四TRX指纹和结束运行态](<docs/visual/r121/runtime-final-regression-phase.json>)确认生产Agent13004身份、五份文件哈希不变，已知仓库测试消费者/被动Worker0、自有fixture根0、旧SystemAutorun.Tests父目录子项0并保留，全部本批后台测试job已结束，无强杀、无部署。该批全量对应健康/键盘补证完成当时的源码，不替代后续原生密集历史尾段或生产更新。
- ENG/原生密集尾段：[最小显式扩展](<tests/SystemAutorun.IntegrationTests/ServiceSelectionPerformanceTests.NativeTail.cs>)仅由原NativeSelectionProbe的双门禁进入；正常未开启仍只测门禁，不冒充原生旅程。给已有选择明细容器赋测试侧唯一Name，Observer先于ListBox绑定选中，关联该目标实际resume→dispose并断言IsPlaying=false；同一QPC记录CPU批次读起止、线程ID+启动时间、全部可读/失败行、暂停输入时刻。后台100ms采样独立20秒取消并设Observer保险；正常路径cancel/await后才序列化，末批也不在STA读。不承诺阻塞原生调用或所有外部强杀路径可回收。[补强后的优化原生1/1](<TestResults/R121/NativeDenseTailFinal/r121-native-dense-tail-final.trx>)1分49秒通过，双主题×650/850ms×持续/暂停输入共8组，均8192有效区间及非空CPU轨迹；真实选中明细dispose后3.75–3.90秒结束，CPU整批窗口保守覆盖至少3.37–3.72秒且UI线程身份匹配，超过所需1秒。[原始及逐组验证/完整线程分析](<docs/visual/r121/native-dense-tail-final/analysis.json>)不截Take(8)，41–48个首尾匹配线程；亮650持续组另有2次观测新增/1次消失、1个前端/2个后端无法配对，采样间短命线程仍不可见，读取失败0不等于完整OS线程归因。暂停仅停止显式输入和AutoRefresh，dashboardClock保持运行；持续模式包含测试SetCurrent的false→true切换、Observer及采样开销，不是生产常态轮询基准。首轮[探索1/1](<TestResults/R121/NativeDenseTailAttempt01/r121-native-dense-tail-attempt01.trx>)虽通过但目标关联/冻结时间/有效CPU跨度断言较弱，原数据保留且不认作本次正式补证。
- MOT/测量解释：[逐组CPU](<docs/visual/r121/native-dense-tail-final/analysis.json>)持续更新的累计进程CPU为2656–3406毫秒、UI125–750毫秒；暂停输入为125–938毫秒、UI0–47毫秒，均不是墙钟耗时或FPS。批次读中位约5毫秒、最长12.42毫秒，原始采样和全部匹配线程均保留；组间时间窗略异、固定顺序及机器负荷未控，不能从下降推断根因或修复。两自有HWND均不在前台，IsWindowVisible不证明无遮挡/呈现。[暗色](<docs/visual/r121/native-dense-tail-final/dark-settled.png>)、[亮色](<docs/visual/r121/native-dense-tail-final/light-settled.png>)均已看实际1728×972像素（1152×648 DIP/144 DPI），是在计时结束后抓取的PrintWindow，不污染上述计时、不证明呈现FPS；密集斜纹、Unix样本造成的超长uptime均属合成源，不当生产健康或正常块配色验收。四保留PNG[全图解码/格式/尺寸/SHA256](<docs/visual/r121/native-dense-tail-image-validation.json>)共353011字节。性能根因及真实呈现/FPS仍未验证，不命名渲染线程、不启动全机WPR。
- 原生尾段清理：[身份、源快照与精确删除](<docs/visual/r121/runtime-native-dense-tail-phase.json>)确认10544/11576原宿主均退出、无强杀；三份最终探针源码按执行前SHA256完整快照保留，后续普通回归测试同步调整不会悄改原生补证版本。删除四张与保留帧相同SHA256的工具临时PNG及四个结束握手文件，共8项353271字节；原始探索/最终数据、TRX与ready保留。生产Agent13004路径/启动时刻、五份保护文件哈希仍匹配；未部署或控制真实任务，不将本切片当最终部署后清理。
- ENG/原生补证后的完整批次：[Debug Core1142](<TestResults/R121/PostNativeTailRegression/debug-core/r121-post-native-tail-debug-core.trx>)、[Debug Integration2989](<TestResults/R121/PostNativeTailRegression/debug-integration/r121-post-native-tail-debug-integration.trx>)、[优化Core1142](<TestResults/R121/PostNativeTailRegression/optimized-core/r121-post-native-tail-optimized-core.trx>)均通过；[优化Integration](<TestResults/R121/PostNativeTailRegression/optimized-integration/r121-post-native-tail-optimized-integration.trx>)2988通过/1失败/0跳过，10分29秒，原TRX保留且不重标成功。失败在亮色选择测试固定700ms后仍有1个DataChangePulse时钟，不等于证明产品泄漏或性能根因；测试改为已有3秒有界Until等待实际自然释放，再保留完整1.1秒安静尾段与零时钟断言，并记录700ms时钟数/额外等待，不强停、不改600ms产品时长、不跳过。[focused双主题2/2](<TestResults/R121/SelectionQuietSettlement/r121-selection-quiet-settlement.trx>)2分20秒通过；[36组原始记录汇总](<docs/visual/r121/selection-quiet-settlement/summary.json>)均有≥1099.57ms安静尾段且最终时钟0。此轮700ms时钟均已0、额外等待最长0.1511ms，未直接重现原失败，不据此宣称其根因已确诊或产品性能已修。[后续Debug Core1142](<TestResults/R121/QuietSettlementRegression/debug-core/r121-quiet-settlement-debug-core.trx>)通过；[Debug Integration](<TestResults/R121/QuietSettlementRegression/debug-integration/r121-quiet-settlement-debug-integration.trx>)2987通过/2失败/0跳过、10分57秒，按失败退出而未运行优化套件。原Host:38返回disk-sample-gap；另一个restart=true历史一致性用例:196预期FaultSeconds600、实际590，原始消息未给hours/样本或时序，不能确诊。运行正常finally结束而非强杀，[两TRX、三份当时源码指纹及保护基线](<docs/visual/r121/runtime-quiet-settlement-failed-phase.json>)保留，生产Agent13004身份及五文件哈希不变；后续不覆盖失败TRX。受控更新尚未执行。
- DATA/真实计数器测试协调：本轮只改测试，没有放宽产品`at<=previous`或`>3秒`的缺口规则。独立原生集合按[xUnit官方非并行语义](https://xunit.net/docs/running-tests-in-parallel)避开同程序集其他集合，文件/随机数据/句柄提前准备；冷PDH初始化之后再建立新基线，并从基线返回计算目标1秒剩余等待，不在刷盘后另叠加1秒；所有时间仍为真实UTC/QPC，不重试失败、不强填缺值，成功/失败均保留诊断。[最新五阶段原始记录与精确UTC夹界](<docs/visual/r121/diagnostic-hardening/analysis.json>)覆盖预热、初始化后基线、2MiB WriteThrough+Flush后读数、4秒断档及恢复；有效pair传入时间保守范围为1.012–1.123秒，实测整机读约7675/4329 B/s、写2083834/2232348 B/s，断档为空且恢复正常。夹界不是精确native counter时间，PhysicalDisk(_Total)不是自有文件归因；读数>100不独立证明NOSCALE，缩放口径依据实现的NOSCALE/NOCAP100与API合同，未新增无缓存物理读或独立raw参考校准，原失败无足够时轴故仍未确诊。
- DATA/历史一致性诊断：[测试侧严格输入核验](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.Parity.cs>)逐次检查合成采样调用增3、任务观察/健康、持久化成功与精确假时刻，继而核对原始124个唯一帧的实例/身份/健康/10秒ValidUntil；重启前固定query也明确要求FaultSeconds600，界面失败消息保留hours/query/结果，原600断言未降低。配置捕获失败或单次追加失败都可能合法留下10秒未知，后续成功会恢复当前状态，但原TRX不足以证明哪条路径发生，不归因为墙钟窗口剪裁。[首次Debug/优化、冷栈/原栈四份输入](<docs/visual/r121/parity-seed-diagnostics/analysis.json>)及[最新复验源快照/帧指纹](<docs/visual/r121/diagnostic-hardening/analysis.json>)均每组124帧=60正常/60故障/4未知，没有复现590；仅为合成源＋真实文件/IPC/独立Console/WPF，不冒称真实Agent进程重启或生产任务采样。最新三组失败相关家族[Debug6/6](<TestResults/R121/DiagnosticHardening/debug/r121-diagnostic-hardening-debug.trx>)、[隔离优化6/6](<TestResults/R121/DiagnosticHardening/optimized/r121-diagnostic-hardening-optimized.trx>)各1分4秒通过、0失败/0跳过；它们不替代最新完整回归。此后串行完整批次已结束：[Debug Core1142](<TestResults/R121/DiagnosticHardeningRegression/debug-core/r121-diagnostic-hardening-debug-core.trx>)、[Debug Integration2989](<TestResults/R121/DiagnosticHardeningRegression/debug-integration/r121-diagnostic-hardening-debug-integration.trx>)、[隔离优化Core1142](<TestResults/R121/DiagnosticHardeningRegression/optimized-core/r121-diagnostic-hardening-optimized-core.trx>)及[隔离优化Integration2989](<TestResults/R121/DiagnosticHardeningRegression/optimized-integration/r121-diagnostic-hardening-optimized-integration.trx>)合计8262/8262通过，0失败/0跳过；两份Integration分别10分59秒、10分51秒。[四TRX及执行源码指纹、候选资产与生产基线复核](<docs/visual/r121/runtime-diagnostic-hardening-regression-phase.json>)均通过，原先两次失败根因仍未获证实，不将本批通过改称根因修复或生产部署。
- 受控更新只读预检：[正式loader的选择性投影](<docs/visual/r121/deployment-policy-preflight.json>)确认当前五个启用Windows/CurrentUser/Logon任务的两类有效自动策略均false，GUI全局两开关亦false；读取前后配置/overrides/偏好哈希相同，暂停文件存在，仅取指纹未调用带写租约的RecoveryControlStore.ReadAsync或初始化。临时只读工具首轮误引Core类库目录不存在的Yaml依赖、未进入配置加载；改用已测试App目录依赖后0警告生成并成功，真实配置未变。此投影不是原生存在状态/在途操作或部署准入最终证明，更新前还须新鲜复核，并保持App根/cli/runtime三目录包约束及开发Agent精确选择，不靠mtime猜测。
- DATA/更新前控制态：[补充只读预检](<docs/visual/r121/deployment-operational-preflight.json>)通过官方loader及只读ProcessIdentity记录五任务共11个匹配进程的PID/启动时刻/映像；有效启动/恢复策略均off，原生身份无不可读项，GUI进程0。仅ping两次核对13004及同一instance，直接读5份原始回执，当前/旧owner的Running均0；恢复原字节复制到专属临时根，调用官方Store验证初始化与结构，admitted为0，随后精确删除克隆根。生产暂停账本未取写租约、初始化或重置，五保护文件及原回执/暂停哈希在读前后不变。工具首轮把用户级appearance误要求位于项目内，在任何IPC/克隆前拒绝；改成精确当前用户appearance允许项后生成0警告并成功。[执行过的四份工具源码与限制](<docs/visual/r121/deployment-readonly-source/index.json>)保留。该预检是瞬时观察，不提供其他客户端的原子排他，实际更新前须重跑。
- ENG/正式包隔离：依据[SDK项目分离产物布局](https://github.com/dotnet/docs/blob/main/docs/core/sdk/artifacts-output.md)，[六项目仅求值记录](<docs/visual/r121/package-build-path-preflight.json>)核对OutputPath、IntermediateOutputPath、MSBuildProjectExtensionsPath、ProjectAssetsFile及BaseIntermediateOutputPath均在专属构建根。保持正式发布脚本不改，在本次PowerShell进程内为其四次dotnet publish追加SDK artifacts路径/项目分离属性，禁用共享编译/节点复用；新obj正常restore，未用NoRestore。保留App根/cli/runtime三层，[候选正式包阶段记录](<docs/visual/r121/package-stage-phase.json>)确认生产Agent和五保护文件、标准Release四份关键文件指纹均未改变；随后逐项复核候选清单全部资产的大小/SHA256。这里只完成候选打包，没有启动候选GUI/Agent或部署。
- ENG/旧组件语义核验：[旧Release磁盘三组件及匹配PortablePDB](<docs/visual/r121/old-release-source-checksums.json>)已核对CodeView GUID/Stamp；AgentServer、OperationJournal、WindowsTaskExecutor、WslController的源码校验和与当前一致，但Program、TaskOrchestrator、AgentMonitor不同。局部本地引用历史中，前两文件只有初始提交，原始及LF/CRLF、UTF8有无BOM四变体均不匹配PDB；AgentMonitor没有引用历史。此结果不排除其他来源，也不足以证明旧Program收尾绝不控制受管进程，仍不停止13004。后续继续静态核验旧二进制收尾及实际PID绑定，不以当前源码替代旧版本安全证明。
- ENG/旧版收尾补证：[静态解码、精确源码及原始IL](<docs/visual/r121/legacy-shutdown-final/analysis.json>)读取旧Release Agent451方法/Core3995方法，不加载程序集、不附加13004。旧Program在server返回后Cancel，等监测/恢复worker，再Drain关准入并等待已跟踪操作；不是StopAll。旧controller在requireRecoveryPreference=true时两次检查全局AutoRecoverFailedTasks，旧worker确实传true；当前逐任务false不能单独证明旧worker资格。旧History.Dispose的实际IL只释放writer Stream与gate，Monitor只释放sampleGate；匹配PDB的executor/WSL Dispose仅释放keepalive句柄。已准入的stop/restart仍可走真实StopAsync，恢复也可StartAsync；这里不排除并发控制、取消回调或所有间接路径，仍需新鲜的零Running/零Admitted/旧全局off证明。
- FLOW/切换工具隔离实测：[最终夹具](<docs/visual/r121/legacy-shutdown-final/fixture/analysis.json>)只启动旧发行Agent的独立root和专属GUID映像名预存worker，官方只读loader/ProcessIdentity确认只命中该worker。临时工具只支持一次shutdown，要求≤60秒预检与配置/暂停/回执集合哈希不变，连接CurrentUserOnly管道后用GetNamedPipeServerProcessId钉扎实际server PID，并复核期望启动时间/映像/哈希；发送前持久化原operationId，未知不换ID重放。故意以worker身份钉扎Agent管道，[精确拒绝](<docs/visual/r121/legacy-shutdown-final/fixture/wrong-server.stderr.txt>)返回3且无intent/请求；正确钉扎后[Agent退出0](<docs/visual/r121/legacy-shutdown-final/fixture/shutdown-result.json>)，worker同PID/启动时间/映像仍存活，再由自有stop文件正常退出，[清理记录](<docs/visual/r121/legacy-shutdown-final/fixture/cleanup.json>)无fallback kill。早期拒绝曾用未处理异常，随后改成正常退出3并完整重跑；两次均为自有fixture，不是生产关闭证据。
- CLEAN/静态与关闭夹具阶段：[34份精确留存与清理后基线](<docs/visual/r121/legacy-shutdown-final/runtime-phase.json>)先核对源/IL/原始结果哈希，确认两批自有Agent/worker已退出，再核验绝对路径、祖先及无reparse后删除两完整IL树与两fixture根，共4496文件/8886128字节；仅清理子代理工具报告的精确grep溢写文件，不扫其临时父目录。PinnedShutdown崩溃转储匹配项0。复用的预检/切换工具、候选包暂留以供实际切换，未冒称最终清理。生产Agent13004身份和五保护文件哈希仍不变，未启停真实受管进程。文档默认发行入口为artifacts/publish/win-x64，后续激活需保留现有.previous/r16，不改全局自启或快捷方式。
- DEPLOY/实际切换：[新鲜预检](<docs/visual/r121/production-cutover/preflight-before.json>)再次确认五任务两类有效策略off、11个真实进程身份、Running/Admitted均0和GUI/CLI消费者0。原五保护文件先做精确备份；旧Agent退出后才复制130份监测历史文件，共66907386字节。默认发行目录已从核验候选激活，旧包移到[独立回滚目录](<artifacts/publish/win-x64.r121-before-update/>)，既有.previous/r16保留，未改全局自启或快捷方式。标准Release四关键文件未覆盖；新Agent31760运行于正式runtime，instance更新，原11进程PID/启动时刻/映像、恢复账本原字节及五保护哈希不变。[切换与历史段指纹](<docs/visual/r121/production-cutover/analysis.json>)保留，重启秒环从新实例重新积累，切换断采不补造。
- DEPLOY/观察异常如实保留：预检先因工具错读偏好字段名、继而误要求缺省恢复字段必须显式false而拒绝，均在intent/请求/备份前退出。已核对旧模型及Store与PDB匹配，缺省bool确为false，没有为通过检查改写偏好。实际shutdown仅发送一次，server PID钉扎13004；响应和原始回执均确认成功、原PID随后不存在，但观察器读取非本对象启动进程的ExitCode抛异常。[原始unknown](<docs/visual/r121/production-cutover/old-agent-shutdown/unknown.json>)、[原意图解析后的退出观察](<docs/visual/r121/production-cutover/old-agent-shutdown/observed-outcome.json>)及[不含shutdown调用的接续脚本](<docs/visual/r121/production-cutover/source/resume-after-observed-exit.ps1>)保留；没有换ID重发或强杀。旧生产进程退出码未知，不能拿隔离fixture的exit0替代它。
- DATA/生产新实例：[只读资源RPC](<docs/visual/r121/production-cutover/real-monitoring.json>)确认五个启用任务都有真实工作集秒点、主机有至少两次真实磁盘读写速率，源分辨率1秒；旧持久源仍为10秒，切换前五任务返回1505个含已知指标桶。持久状态明确30天/64MiB，停止后历史快照保留在[回滚数据](<artifacts/qa/r121-production-rollback/>)；正常容量/时间修剪仍按合同，不承诺完整30天。新实例与GUI实际加载的[包内托管组件及原生依赖指纹](<docs/visual/r121/production-cutover/loaded-package-components.json>)已记录，不靠新旧可相同的apphost哈希或程序集版本号声称更新。
- FLOW/VIS生产入口：[正式GUI18572启动身份](<docs/visual/r121/production-cutover/gui-start-pin.json>)来自正常App.OnStartup、显式原root，复用已核对的新Agent；[真实后台副实例](<docs/visual/r121/production-cutover/single-instance-background.json>)退出0且不新增GUI/Agent。服务/主机页实际UIA切换后，[服务像素](<docs/visual/r121/production-cutover/production-services.png>)与[主机四轨像素](<docs/visual/r121/production-cutover/production-host.png>)均已查看，为1152×648 DIP/144 DPI的1728×972精确HWND PrintWindow，捕获未改变前台，不是呈现FPS。生产沿原偏好为亮色，双主题/尺寸矩阵沿前述隔离实窗证据，未为验收改写偏好。正常标题栏关闭实际[隐藏到托盘而不退出](<docs/visual/r121/production-cutover/tray-hidden.json>)；后台PostMessage双击和单次UIA Invoke均未恢复，原观察保留，未冒称送达。随后对已核验NotifyItemIcon进行一次真实OS双击，[同PID主窗恢复](<docs/visual/r121/production-cutover/tray-physical-restore.json>)。该动作确使GUI成为前台，之后仅最小化自身，原工作窗口自然恢复；未激活/操作浏览器。后台副实例不覆盖正常副实例激活通知的全部路径，也不把程序注入当真人输入。
- SAFETY/启动后复核：[GUI原生旅程后的只读结果](<docs/visual/r121/production-cutover/post-gui-safety.json>)保持全部11进程身份、五保护文件和暂停账本原字节，Running/Admitted仍0。原始回执选择性投影没有新增任务启停/重启；唯一新增控制为旧Agent的shutdown。正式Console在清理后仍能[ping新实例31760](<docs/visual/r121/production-cutover/post-cleanup-daemon-status.json>)并[读取被动监测](<docs/visual/r121/production-cutover/post-cleanup-monitor-status.json>)，没有调用daemon start或任务控制。
- CLEAN/最终批次：[逐目标删除及运行态](<docs/visual/r121/final-cleanup/cleanup-result.json>)在复核绝对路径、祖先/子树无reparse、无测试消费者和全部保留哈希后，删除临时工程、SDK隔离构建根及16个R121Release专属bin/obj，共1824文件/900004219字节；只删本任务精确job列表溢写文件，不清扫临时父目录或DSH包装进程。清理前补齐策略工具、未匹配HEAD源码及精确执行工具二进制等缺口，[106份源/自有编译产物映射](<docs/visual/r121/final-cleanup/retained-index.json>)去重为56对象/11432970字节。全部[107份原始测试记录](<docs/visual/r121/final-cleanup/retained-raw-test-records.json>)共90997085字节保留，包括失败TRX；不是只留成功摘要。当前仅保留正式GUI/Agent和明确的旧包/历史/偏好回滚数据，不将它们当泄漏清理；R121临时编译/探针根0、清理强杀0，前述标准Release及原有备份未动。
- 限制与最终解释：原生密集历史已补完整线程及最后明细Dispose后至少3.37秒有效CPU窗，双主题选择/模式/时段、局部红退、隐藏/减少动画释放及真实Agent+B首选/GUI正常退出重开都有前述分项证据。符合Logon门禁的策略实写仅限隔离root；不以强制销毁窗口或重建客户端代替GUI进程退出。真实呈现FPS、顿挫根因和两项早期全量失败根因仍未获证实，不用回调、PrintWindow、CPU回落或最新8262全绿替代它们。安全切换采用新鲜无消费者快照，不承诺抵御并发恶意客户端的原子排他。IO口径沿[官方语言中立counter API](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdh-pdhaddenglishcounterw)和[两次真实采样](https://learn.microsoft.com/en-us/windows/win32/api/pdh/nf-pdhgetformattedcountervalue)，未放宽3秒缺口规则、用容量替代IO或新增全机WPR。

## 2026-11-12 — 服务选择切换收尾顿挫（R120）

按用户“可以进行检查修复”的授权，沿[R117–R119既有合同](<docs/design-system.md#L816-L843>)走修复快速路径。范围是左侧服务/进程选择与右侧详情进场，不是顶部tab；不改变动效时长、阶段、区域接力、纯红绘线/均匀回色、立即取消清理或192组冻结帧预算，不改采集、协议、业务状态和偏好。

- ROOT/修复：[健康带绘制](<src/SystemAutorun.App/Controls/DashboardPlot.cs#L91-L151>)原来每格全扫全部健康区间，密集历史回包会撞上进场尾段；后端8192区间上限不能用120历史桶替代。[枚举回归](<tests/SystemAutorun.IntegrationTests/DashboardHealthRenderingTests.cs>)在920 DIP绘图区先确认原绘制枚举102次，再确认修复后仅1次。现在每区间只投影一次，缓存本次绘制的时间/宽度标量，并按实际交集分桶；仍保留逐格裁剪、矩形边缘和输入覆盖顺序，不合并裁剪、不简化历史、不跨缺口连线。双主题47/183/920 DIP的旧绘制器逐字节像素对照和8192区间枚举共[7项最终回归](<docs/visual/r120/final/r120-health-final.trx>)通过；[原绘制红灯](<docs/visual/r120/before/r120-health-red.trx>)保留。
- DATA/测量：[独立选择夹具](<tests/SystemAutorun.IntegrationTests/ServiceSelectionPerformanceTests.cs>)经实际ListBox选择/双向绑定进入详情，使用明确标注的合成回放，而不是生产Agent数据。每图285–292个有效恒值CPU/内存点，CPU为0.04%，含真实夹具缺口；每次120历史桶，健康区间分别1/120/8192，在650/850ms回放历史发布，再覆盖第1秒采样和后续尾段。1152×648 DIP、144 DPI、动画启用，每组合3次；最终密集案例在最后一次Dispose后继续记录至少1093ms。首轮夹具误用禁用服务而没有资源点，已纠正为启用服务，未放宽非空断言；[首轮](<docs/visual/r120/before/r120-before.trx>)/[诊断](<docs/visual/r120/before/r120-fixture-diagnostic.trx>)与[有效基线](<docs/visual/r120/before/r120-before-final.trx>)均保留。
- PERF：下表是8192区间、每主题6次历史绘制的隔离优化配置比较。健康绘制计时包含OnRender整个健康带；回调间隙取650–1000ms尾段。p95/p99采用nearest-rank，小样本不作总体分位数保证。[分段汇总](<docs/visual/r120/selection-summary.json>)另列0–400/400–650/650–1000/1000–1850ms各段的p95/p99/max、CPU、分配、GC及清理；[汇总脚本](<docs/visual/r120/summarize.ps1>)、修复前/后原始JSON、中间两轮及[最终测量](<docs/visual/r120/final/r120-final-selection.trx>)保留，不只挑最好一轮。

|主题|健康带中位ms（前→后）|健康带最大ms（前→后）|尾段回调间隙p95/p99/max ms（前→后）|
|---|---:|---:|---|
|深色|164.54 → 20.20|191.86 → 21.29|165.04/219.71/219.71 → 44.14/67.00/67.00|
|浅色|166.35 → 0.99|173.33 → 1.42|174.64/194.42/194.42 → 43.58/50.13/50.35|

- PERF边界：修复前密集案例的Dispose最大仅0.52/0.33ms，不支持把本轮重绘热点归因于统一撤时钟；没有把清理延后一帧。默认关闭的[进程内计时观察器](<src/SystemAutorun.App/Services/MotionTiming.cs>)只供隔离探针使用，没有外发或正常运行计时输出。修复后仍见约67/50ms最大回调间隙，不能宣称所有顿挫消失；深浅主题差距也不解释为主题本身的固有成本。这是屏外真实HWND/Dispatcher的合成回放，不是生产负载复现、DWM呈现FPS或GPU成本证明。
- VIS/FLOW：最终编译版本在144 DPI真实桌面检查双主题920×580、1152×648、1320×820 DIP。[深色最小](<docs/visual/r120/final-dark-920/000.png>)/[宽窗](<docs/visual/r120/final-dark-1320/000.png>)与[浅色最小](<docs/visual/r120/final-light-920/000.png>)/[宽窗](<docs/visual/r120/final-light-1320/000.png>)均实际查看；标题、选择/操作组及最右运行时间仍同排，资源曲线保留断点，没有新增操作焦点外框。实际点击左侧A，再用方向键选择B；已查看[深色绘线](<docs/visual/r120/final-dark-1152/003.png>)/[区域接力](<docs/visual/r120/final-dark-1152/005.png>)/[终态](<docs/visual/r120/final-dark-1152/035.png>)与[浅色绘线](<docs/visual/r120/final-light-1152/003.png>)/[区域接力](<docs/visual/r120/final-light-1152/006.png>)/[终态](<docs/visual/r120/final-light-1152/035.png>)，观察覆盖历史发布和至少1秒采样尾段。[最终原生门控](<docs/visual/r120/native-final/r120-native-final.trx>)通过。截图辅助[脚本](<docs/visual/r120/capture-selection.ps1>)逐帧确认目标HWND/PID且处于前台，前台改变即停止；GDI取样和PNG编码仅约100–150ms一帧，不能证明连续呈现无掉帧。DSH控制条是外部覆盖层，不属于产品界面。
- ENG：Debug全量[Core](<TestResults/R120/Debug/r120-core-debug.trx>)997/997与[Integration](<TestResults/R120/Debug/r120-integration-debug.trx>)2922/2922通过，合计3919，0失败/0跳过。临时优化配置另通过最终像素/枚举7项、分段测量2项及原生门控1项；不是本轮标准Release全量回归。自动回归移除手动probe变量，保留原有生命周期、历史/实时、布局及动效测试；没有跳过失败用例。
- 运行/清理：[最终核验](<docs/visual/r120/runtime-and-cleanup.json>)确认生产GUI PID33364、原Agent PID13004的路径与UTC启动ticks完全一致，五份配置/偏好SHA256与[开始快照](<docs/visual/r120/runtime-before.json>)相同，仅原有GUI/Agent留存，没有testhost。未替换生产Release、未重启GUI/Agent，当前生产窗口尚未加载本修复。临时优化配置/程序集的[SHA256](<docs/visual/r120/assembly-hashes.json>)已保留；核对精确绝对路径后删除24个R120Before/R120After构建目录、已确认的临时编译元数据/探针控制文件、两张本轮桌面工具临时图及冗余帧，合计319,486,880字节，保留22张选帧与完整时间戳。[清理清单](<docs/visual/r120/cleanup-result.json>)不涉及其它会话目录、生产Release或共享编译服务。首轮身份核对的JSON自动日期对象与字符串比较误报另[保留](<docs/visual/r120/runtime-identity-serialization-check.json>)，改比较UTC ticks后通过，未恢复/改写任何生产状态。本轮不冒称生产Agent或真实历史端到端验收。

## 2026-11-11 — 实时趋势、单行详情头与冻结帧复用（R119）

按[已批准的B方案](<docs/design-system.md#L834-L843>)沿现有实机界面 Inherit。范围限 GUI、动效资源复用和隔离后端核验；不改 Agent 采集、历史协议、真实业务命令或用户偏好。**工具窗替代后端未放行，现有 AllowsTransparency 冲突仍是遗留限制，不以本轮视觉结果追认。**

- DATA：新增[有界实时缓存](<src/SystemAutorun.App/Services/ResourceTrendBuffer.cs>)，默认保留最近5分钟、每组最多301个实际快照和8组任务/Agent身份；按进程启动身份断开，启动身份未知只保留各次实际观察的孤立点；重复、过时、未来、暂停/隐藏/切换及断采不制造新点、不补零、不跨缺口连线。CPU 使用百分比，实时内存使用工作集字节；[自适应尺度](<src/SystemAutorun.App/Services/DashboardTrendScale.cs>)保留最小跨度、显式上下限及30秒收缩迟滞。历史只连接真实相邻可用桶，仍显示原协议的内存百分比，不用当前物理内存反算历史字节；主读数仍是当前值。[针对性隔离回归](<TestResults/R119/r119-resource-isolation.trx>)36/36通过，[真实OS采样与实窗消息循环](<TestResults/R119/r119-window-performance.trx>)3/3通过。
- VIS：144 DPI（150%）原生桌面，双主题分别检查920×580、1152×648、1320×820 DIP；查看[深色宽窗](<docs/visual/r119/live-dark-1320x820/000.png>)、[浅色常用窗](<docs/visual/r119/live-light-1152x648/000.png>)及其余尺寸截图。服务名、32 DIP选择/操作组件、最右运行时间保持同排，无挤掉操作或运行时间；超长标题省略/全名提示另由实窗布局断言覆盖，不冒称所有长标题均做了原生桌面矩阵。图表只绘实际收到的测试宿主 OS 数值；[样本原文](<docs/visual/r119/native-real-resources.ndjson>)记录时间、进程身份、CPU与字节，不是合成波形。截图中的DSH控制条/蓝色指针标记是外部覆盖层，不属于产品UI。
- FLOW：在隔离实窗用真实指针打开资源模式、选择历史，再打开时间段并选7天，观察[无历史状态](<docs/visual/r119/history-light-7d/000.png>)，随后返回实时；健康时间段仍为7天，而资源图恢复独立5分钟，不借历史范围重置实时尺度。24小时/7天/30天及任务切换隔离另有自动回归，未冒称这三项都做了真实历史端到端采集。实机悬停揭露空白区不命中及祖先来源提示抢占；[图表控件](<src/SystemAutorun.App/Controls/DashboardPlot.cs>)补透明命中区域并在首次悬停前注册自身提示，实测[CPU未舍入原值](<docs/visual/r119/fixed-cpu-hover/000.png>)、[内存精确字节](<docs/visual/r119/fixed-memory-hover-dark/000.png>)与[真正未采样](<docs/visual/r119/fixed-unsampled-hover-dark/000.png>)。修复后的[最终Debug增量回归](<TestResults/R119/r119-final-debug-incremental.trx>)42/42通过；[独立临时配置回归](<TestResults/R119/r119-final-hover-targeted.trx>)及[原生悬停门控](<TestResults/R119/r119-native-hover-fixed.trx>)分别覆盖控件初始化和真实桌面行为。未执行真实服务停止/重启、诊断提交或外观菜单写入。
- MOT/VIS：复用保持R117纯红整线绘制、末段均匀回色、区域接力与原冻结状态预算；不共享时钟/事件，不栅格化页面，不降帧或删阶段。[深色主机接力](<docs/visual/r119/motion-dark-host/007.png>)/[终态](<docs/visual/r119/motion-dark-host/025.png>)、[浅色主机接力](<docs/visual/r119/motion-light-host/007.png>)、双主题服务与日志共六段原生切页序列已查看；[浅色日志终态](<docs/visual/r119/motion-light-log/025.png>)的来源/计数仍是旧合成夹具，只证明视觉与选择，不证明真实日志聚合。每段26帧，保留选帧与完整trace；[红线像素记录](<docs/visual/r119/motion-pixel-analysis.json>)仅作阶段定位辅证，不以GDI采样间隔推导产品帧率。
- PERF：同尺寸/边宽/颜色/透明度冻结矢量帧有界复用。[7轮×480次准备](<docs/visual/r119/frame-preparation-performance.json>)的分配由29,319,400降至2,292,520字节（约92.18%），耗时中位数由275.00降至12.58ms；这是重复准备的暖缓存基准，不是整程序内存下降92%。[双主题实窗消息循环记录](<docs/visual/r119/window-performance-summary.json>)每种操作3轮，主机切页中位813–817ms、服务715–725ms、工具窗开关1176–1190ms，渲染回调间隔中位约16.4–16.7ms；测试窗在屏外，记录进程CPU/分配和Dispatcher/CompositionTarget.Rendering，不代表DWM呈现FPS、GPU成本或点击到全页就绪上限，也没有未缓存的整窗A/B数据。
- 后端隔离核验：原生外层 HWND + 不透明 HwndSource 子窗的双主题进入/退出及明确反向连续帧证明主体随父alpha合成；探针已修正首显直接跳满、8物理像素及按进度猜反向的问题，使用300ms、按144DPI换算8 DIP，并按当前目标反向。[最终状态](<docs/visual/r119/native2-final-state.json>)确认outerLayered=true、childLayered=false、childPerPixelOpacity=false。**[开放的ComboBox弹层](<docs/visual/r119/native2-popup-open-half/000.png>)在父alpha约128时仍完全不透明，弹层合成失败；不能作为可替换后端。** 模态/Owner/veto/真实Closed/隐藏/DPI切换/chrome/焦点桥接未验收，探针切主题还会重建RootVisual，不据此证明输入树保留。生产ToolWindow仍保留原实现，生命周期迁移须另批专项；未修改原Agent。
- ENG：Debug全量[Core](<TestResults/R119/Debug/r119-core-debug-final.trx>)997/997及[Integration](<TestResults/R119/Debug/r119-integration-debug-final.trx>)2912/2912通过，均0失败/0跳过；其后只补首次悬停注册，最终42项增量复验通过。Release全量[Core](<TestResults/R119/Release/r119-core-release-final.trx>)997/997及[Integration](<TestResults/R119/Release/r119-integration-release-final.trx>)2912/2912通过，均0失败/0跳过，包含最终首次悬停注册修复。两种配置各3909项；日志中未报告构建警告/错误。保留[首轮失败](<TestResults/R119/Debug/r119-debug-full.trx>)（Integration 2904通过/7失败）：6项SelfStartup旧断言误要求R118遮蔽中的父场景实际可见，现改验逻辑tab可见、输入暂停及真实Closed后恢复；另1项0.1秒连接期限在冷启动本地准备前已耗尽，改为1秒以确实覆盖真实管道连接超时，原保留ID/无变更/清理断言不变。没有跳过失败用例或改业务语义。
- 运行/清理：本轮资源探针仅采自己的testhost，健康/主机/日志使用隔离合成夹具；原生宿主探针不启动生产App、不连接生产IPC、不读真实root或写偏好。原Agent PID13004及2026-11-08启动身份保留。[最终运行与清理记录](<docs/visual/r119/runtime-and-cleanup.json>)确认原Agent身份不变，配置/覆盖配置/GUI设置/启动项备份/外观文件五份SHA256均与本轮开始相同；任务探针进程已退出。核对精确绝对路径后删除22个探针目录/辅助文件/临时R119Hover构建目标及142张冗余帧，保留70张切页/原生合成选帧、其它尺寸/悬停截图、完整trace、样本和小型[宿主探针源码](<docs/visual/r119/native-host-probe/Program.cs>)。没有清其它会话目录、重启生产GUI或原Agent，也不把隔离窗口冒称生产Agent集成。

## 2026-11-11 — 弹窗首末帧与背景过渡（R118）

沿[既有视觉合同](<docs/design-system.md>)的修复路径，按用户确认同时修复进入/退出黑框与背景突现；不改布局、主题方向、业务流程或原Agent。

- ROOT：不透明WPF HWND上的Window.Opacity只把绘制淡到原生清除面，并非整窗透明；[修复前原生首帧](<docs/visual/r118/before-black-entry.png>)约51.44%画面为纯黑，退出中段则[变暗成灰底](<docs/visual/r118/before-gray-exit.png>)。共享[工具窗](<src/SystemAutorun.App/Services/ToolWindow.cs>)改为WPF支持的逐像素透明目标，在HWND创建前应用既有主题，保留300ms整窗淡变/8 DIP位移；不外部强加WS_EX_LAYERED、不引入替身窗口或强制软件渲染。关闭提交同时等待壳与正文退休，动态减少动画及隐藏/Closed清除壳时钟和订阅。
- ENG：[最终回归](<TestResults/R118/r118-final.trx>)92通过、0失败、0跳过，70秒；含模态结果、重复关闭、首次/最终否决、快速反向、隐藏重显、owner强制关闭、嵌套遮蔽、早退正文仍等待壳退出，以及中途减少动画。原生目标在SourceInitialized时即核验逐像素透明及主题来源。主窗920×580、960×620、1320×820双主题的原有布局/生命周期用例保持。Release构建0警告、0错误，5.99秒；`git diff --check`通过，仅有既有换行提示。
- 修复过程保留：[首轮](<TestResults/R118/r118-regression.trx>)89通过、1失败，新增遮罩反向用例揭露SetCurrentValue未把当前透明度保留为新动画基值；改为控件自有Opacity的真实基值后，[独立复核](<TestResults/R118/r118-reversal.trx>)及最终回归通过，连续性断言收紧至百万分之一，没有放宽或跳过。额外[首轮模态探针](<TestResults/R118/r118-native-modal.trx>)在返回断言通过后，因人工观察finish门控超90秒失败；未计作通过，保留记录，及时结束观察的[复跑](<TestResults/R118/r118-native-modal-final.trx>)通过。
- VIS（MOT/REU/COL/VER）：144 DPI（150%）、1152×648 DIP原生桌面，双主题各采主窗弹窗进入/退出和嵌套进入/返回，每段38帧、共304帧；GDI使用CAPTUREBLT包含透明HWND，逐帧核对前台归属，不以RenderTargetBitmap冒充合成证据。已看[浅色进入](<docs/visual/r118/light-entry/012.png>)、[浅色真实淡出](<docs/visual/r118/light-exit/006.png>)、[深色进入](<docs/visual/r118/dark-entry/010.png>)及[深色淡出](<docs/visual/r118/dark-exit/006.png>)：不再露黑/变暗，斜纹可透过正在退场的窗壳。64张选帧和8份trace保留实际时间；捕获/编码约50–65ms而非名义30ms，不据此声称产品帧率。[像素复核](<docs/visual/r118/pixel-review.json>)显示修复后八段均无纯黑像素，只作辅证，不将主题近黑背景误判为黑框。
- 背景：共享[遮罩](<src/SystemAutorun.App/Controls/DialogBackdrop.cs>)可逆淡入/出，进入即暂停进场与后台展示读取、遮挡鼠标；原页面仍可见直至遮罩完全覆盖后才隐藏，避免先跳成空底。[嵌套浅色](<docs/visual/r118/light-nested-entry/006.png>)/[嵌套深色](<docs/visual/r118/dark-nested-entry/006.png>)同样经过共享通道，最后窗口真实退出后恢复原内容与原地进场；减少动画、隐藏、最小化、卸载/关闭收敛并回收动画。
- FLOW：八段为隔离夹具调用真实生产DoctorWindow的Show/Close；另在真实模态[工具窗](<docs/visual/r118/modal-tool.png>)通过原生UIA调用顶栏关闭，实际返回[主窗](<docs/visual/r118/modal-return.png>)，复跑[回执](<docs/visual/r118/modal-return-final.txt>)确认modal=false、Closed成功、scrim消失及筛选保留。未执行系统诊断、真实服务启停或配置提交；未新增焦点外框。
- DATA/运行：双主题[浅色](<TestResults/R118/r118-native-light.trx>)/[深色](<TestResults/R118/r118-native-dark.trx>)探针均正常回收，主题仅进程内persist:null。外观SHA256保持DB4A4B6A9F8A6B562294371D4315BB2179F3A13F36C2DB630AA6268BC8ECF58C。新版Release GUI PID7164已启动并查看[运行截图](<docs/visual/r118/release-loaded.png>)；原Agent PID13004及2026-11-08启动身份不变。清理与最终身份回读见[运行记录](<docs/visual/r118/runtime-and-cleanup.json>)，只清本轮精确归属的批量帧、探针目录、脚本与临时截图。

## 2026-11-11 — 整线末段回色与分层接力（R117）

按用户批准的[本轮变更契约](<docs/design-system.md>)延续真实UI的Inherit路线，取代R116空间渐变。范围限共享进场、语义区域标记和回归；保留布局、主题、采样、业务命令和原Agent。

- ENG：`dotnet test ... --no-restore` 的[最终回归](<TestResults/R117/r117-final.trx>)128通过、0失败、0跳过，85秒。覆盖整路径同色/跨角/单次透明度、16个冻结状态、卸载前真实终值、语义继承及不同视口下分层时序、延迟封顶、主机仪表模板不重画，以及原有刷新/切换/hover/减少动画/关闭释放。扩大检查的[首轮](<TestResults/R117/r117-regression.trx>)揭露了R113前的960×620断言、R114前的透明scrim/提前解锁断言和新用例漏算驱动器仪表；按当前1152×648、opaque backdrop覆盖到真实退出以及夹具四个仪表的事实修正，未跳过或放宽产品断言。Release App独立构建0警告/0错误，6.07秒。
- VIS（MOT/REU/COL/VER）：双主题、144DPI、1152×648 DIP真实窗口分别点击服务、主机、异常日志，查看[深色主机接力](<docs/visual/r117/host-dark/010.png>)、[浅色主机接力](<docs/visual/r117/host-light/010.png>)及各页连续阶段帧。纯红路径不再有亮头/尾迹，末段每条路径均匀回色；CPU读数/仪表先于内存仪表，随后磁盘与网络；日志标题、元数据和正文分层，末帧恢复主题线色。保留[最小920×580](<docs/visual/r117/host-minimum-light.png>)与[宽桌面1320×820](<docs/visual/r117/host-wide-light.png>)实窗：调整尺寸后无残留半框、裁切读数或错位；这两张为浅色稳定态，不冒称完整双主题尺寸矩阵。浅色序列里的DSH控制提示是桌面覆盖层，不是产品UI。
- FLOW：上述六次真实指针切页及原生UIA主机返回均已执行并观察；回归另覆盖真实选择变化、刷新不重播、工具窗隐藏/关闭/嵌套返回和减少动画。未执行真实服务启停或业务提交，也不以截图证明这些流程。本轮没有新增焦点外框或改变点击范围。
- 时序边界：[采样清单](<docs/visual/r117/manifest.json>)保留实际时间戳；原始请求间隔30ms，GDI捕获/编码实际约50ms，不能据此声称产品帧率。900ms约束针对单次进场播放器，不含既有切页退出/布局时间，不冒称点击到全页就绪900ms。
- DATA：两轮[深色](<TestResults/R117/r117-native-dark.trx>)/[浅色](<TestResults/R117/r117-native-light.trx>)门控探针均正常结束，使用隔离ServerFixture及合成服务、主机指标和日志；沿用R115命名不代表真实日志集成，合成列表与未刷新计数的组合不作为业务验收。主题只在测试进程内应用，未改外观菜单；尺寸探针及新版重启后外观文件SHA256均保持原值。
- 运行/清理：[记录](<docs/visual/r117/runtime-and-cleanup.json>)确认原Agent PID13004及2026-11-08启动身份未变；只替换GUI，新Release PID10456已启动并查看[真实运行截图](<docs/visual/r117/release-loaded.png>)。两个探针PID23392/19720退出，已按精确绝对路径核验后删除240张批量帧、探针目录、脚本与临时指纹；仅保留30张阶段帧、6份trace、2张尺寸截图、运行截图和小型清单，不清其它任务产物。

## 2026-11-11 — 短红前沿与原地成形衔接（R116）

沿[已批准R116动效切片](<docs/design-system.md>)继续既有实窗Inherit；用户认可“前沿绘制、尾迹回主题色、内容同步显现”。仅改共享动效与针对性测试，不修改业务、布局和主题偏好。

- ROOT：正交边按整条路径累计距离，只有3 DIP前沿及最多36 DIP短尾迹着红；其余已画部分即为主题色。完整几何后60ms收稳（最后15ms已是最终画刷），再卸载时钟；透明度施加于DrawingGroup一次，避免半透明拐角叠色。标签/读数/动作延迟改为70/130/150ms，与绘线重叠，取消独立的大面积红色线框等待阶段。
- ENG：[最终针对性回归](<TestResults/R116/r116-final.trx>)163通过、0失败、0跳过，84秒；覆盖新增尾迹局部性/跨角/透明度、冻结状态预算、卸载前终值、内容重叠，以及原刷新/快速切换/日志框线/hover/减少动画/关闭释放回归。首轮4个失败均保留于[首轮TRX](<TestResults/R116/r116-regression.trx>)：两项工具标题断言改为从进入时记录真实Clip动画，避免提前完成后采样；两项新收尾断言改为比较WPF复制时钟后的颜色/透明度而非画刷引用，仍要求冻结值及卸载前已稳定，没有跳过测试。Release App构建5.04秒，0警告/0错误。
- VIS/FLOW：实际鼠标选择服务及两条合成日志；144 DPI（150%）、1152×648 DIP，双主题各28帧共112原生屏幕帧，实际时间见四份trace（未用名义25ms间隔冒称帧率）。抽检[深色服务绘制](<docs/visual/r116/service-dark-008.png>)、[深色收稳](<docs/visual/r116/service-dark-015.png>)、[浅色绘制](<docs/visual/r116/service-light-008.png>)和[浅色收稳](<docs/visual/r116/service-light-015.png>)，以及[深色日志绘制](<docs/visual/r116/log-dark-008.png>)/[收稳](<docs/visual/r116/log-dark-015.png>)、[浅色日志绘制](<docs/visual/r116/log-light-008.png>)/[收稳](<docs/visual/r116/log-light-015.png>)：主题色线条留在原位，正文在前沿尚未完成时已进入，没有整框红转灰的末帧换皮，也未新增焦点外框。DSH控制条和指针标记是外部覆盖层，不属于被测UI。
- 像素旁证：[分析记录](<docs/visual/r116/native-analysis.json>)限定标题结构线ROI，深色红色像素峰值93、浅色39，灰色线像素逐段增长，结束红色归零；不把这些阈值当审美评分。日志双主题及浅色服务末8帧详情ROI差为0。深色服务末段有299个像素变化，实际定位仅在动作图标区域(x1479–1552,y106–126)，正文和边框未变化；没有将这组称为整页零变化或绝对无闪烁。
- PERF/边界：每条绘线预建15个冻结状态、底色7个，120目标预算不变，仍无逐字测量或渲染tick重建；这属于实现/预算证据，未跑CPU/GPU/FPS基准。新增实窗只覆盖常用尺寸；不重复宣称最小/宽窗口、其它DPI与所有弹窗完成本轮原生验收。相关弹窗/取消/减少动画有自动回归，业务持久化与真实服务启停不在本轮范围。
- DATA/偏好：两轮探针均使用隔离ServerFixture与合成行；截图里的R115来源名只是沿用夹具，不证明真实日志读取/聚合。主题由测试进程内Apply设置，未操作外观菜单；外观文件测试前、探针退出后及正式GUI重启后的SHA256一致。原Agent PID13004及2026-11-08启动身份保留，没有执行真实任务启停、Run/Boot或远程写入。
- 运行/清理：两轮[深色](<TestResults/R116/r116-native-dark.trx>)/[浅色](<TestResults/R116/r116-native-light.trx>)人工门控探针正常结束并回收夹具，TRX本身不代替上面的VIS/FLOW。新版Release GUI PID8308已启动并实际查看[运行截图](<docs/visual/r116/release-loaded.png>)；只保留16张阶段帧、4份trace、分析和运行截图，删除本轮批量帧、两个探针目录、捕获/分析脚本及临时偏好指纹，不清理其它会话数据。

## 2026-11-11 — 重复进场、日志框线和 hover 闪烁修复（R115）

按[既有设计R115](<docs/design-system.md>)走修复快速路径；用户本次明确要求修改后测试，覆盖前几轮“不跑测试”的边界。以下仅验收本次缺陷范围，不代表整套软件G3或性能基准通过。

- ROOT：相同稳定值的服务名/主机ID/日志Entry不再因新对象引用重播；绑定同轮A→null→A合并后不重播，真实选择及快速反向仍进场。只读日志正文与结构边框分开控制：原生PART_ContentHost淡入，编辑器本体保持可见，避免绘线先完成却被整体透明度遮住。
- HOVER根因及复验：第一轮原生连续帧实际抓到一帧灰白亮峰，并非只靠静态代码推测。WPF透明白的隐藏RGB参与颜色插值，深色行背景采样出现7.33→107.67→28.67；规整透明端点RGB后最终原生采样为7.33→12.67→27.67→28.67，图标为7.33→22.33→28.67，浅色两者单向244.33→约230→229。采样为背景RGB通道均值，不是对比度/性能分数；取样避开文字、桌面控制浮层及鼠标标记。另以1ms步进自动验证双主题hover进/出合成颜色不越界、不反向，撤销共享文字/控件显现的低幅脉冲。
- ENG：最终[针对性回归TRX](<TestResults/R115/r115-final-158.trx>)为158通过、0失败、0跳过，86秒；覆盖稳定刷新/短暂空选择、快速切换、日志框线和主题来源恢复、单向显现、共享hover配方、颜色/减少动画、窗口进出与取消、owner关闭订阅释放、当前桌面布局及相关VM。同步了R112已删除控件/命令/页脚的旧断言和浅色焦点颜色断言，修复扩展测试揭露的跨STA斜纹主题订阅泄漏，而非屏蔽失败。门控NativeProbe在无环境变量时直接返回；真实人工探针另有[第一轮](<TestResults/R115/r115-native.trx>)及[最终轮](<TestResults/R115/r115-native-final.trx>)正常结束记录，此单测结果本身不当作VIS/FLOW证据。
- VIS/FLOW：实际前台隔离实窗，144 DPI（150%），常用1152×648 DIP双主题。原生鼠标切换两条合成日志，查看标题横线和正文矩形逐段绘制、文字随后出现、结束恢复主题；[深色绘制中](<docs/visual/r115/log-drawing-dark.png>)、[深色终态](<docs/visual/r115/log-settled-dark.png>)、[最终浅色绘制中](<docs/visual/r115/log-drawing-light.png>)、[浅色终态](<docs/visual/r115/log-settled-light.png>)。实际hover刷新图标和未选日志行并复拍双主题；[深色](<docs/visual/r115/row-hover-dark.png>)与[浅色](<docs/visual/r115/row-hover-light.png>)不再出现第一轮的灰白亮峰。没有为点击/焦点新增外框。
- 稳定刷新：隔离实窗每秒新建字符串、重灌合成服务快照并刷新CollectionView，6秒16帧中服务列表与资源正文静态区域像素差均0；控制浮层覆盖的顶部/指针区域未纳入。参见[首次采样](<docs/visual/r115/services-first.png>)、[末次采样](<docs/visual/r115/services-last.png>)、[数值分析](<docs/visual/r115/native-analysis.json>)及[原生坐标/时序记录](<docs/visual/r115/native-traces.json>)。这是有界采样与自动回归的组合，不宣称任意帧率下绝对零卡顿。
- 桌面范围：另实际查看浅色[最小920×580 DIP](<docs/visual/r115/minimum-light.png>)与[宽1620×651 DIP](<docs/visual/r115/wide-light.png>)；框线、详情正文和动作未溢出，表格截断处仍有详情完整正文。双主题四组尺寸另有自动几何回归；未把未拍摄的暗色最小/宽窗口称为实窗验收。
- DATA边界：探针为隔离ServerFixture和合成行，只用于动效/换行/选择；未执行真实服务启停、业务配置写入、Run/Boot或远程连接。截图中的合成日志直接注入，因此“尚未读取”及0计数不是实际日志读取/聚合的验收证据；本轮不新增此类数据旅程或剪贴板/持久化结论。手工外观菜单实际切过主题，当前停在浅色；这是每用户外观偏好，不是Agent业务配置，未声称主题恢复或尺寸持久化已复验。
- 构建/运行/清理：仅Release App构建成功，5.92秒、0警告/0错误；新版GUI PID9756已启动，实际查看[运行画面](<docs/visual/r115/release-loaded.png>)，窗口1728×972物理像素、可见启用。原Release Agent PID13004及2026-11-08启动时间保持；最终进程检查只余正式GUI和原Agent、无testhost。已删除本次两组临时探针目录、连续帧批量文件及临时捕获/分析脚本，隔离夹具自行回收；仅保留16个精简证据文件共786356字节及TRX，不清理其它会话数据。改动范围git diff --check通过。

## 2026-11-11 — 性能优先与弹窗/侧栏（R114，用户自行测试）

沿[原设计R114](<docs/design-system.md>)按用户最新取舍改动，延续不跑测试、只编译启动交其检查。

- PERF（静态证据，不是性能实测）：R113每字TextPointer/字符矩形、最多2048几何和每帧DrawingBrush创建已移除；改为1–4段预生成Clip，长日志整块淡入。描边和底色也不再有.NET逐帧GetCurrentValue；冻结帧在入口有界创建。未运行FPS/CPU基准，不能宣称“始终流畅”已验证。
- ROOT：主窗Loaded实例处理与WindowTheme的类Loaded处理此前会接收子树冒泡；现仅自身首次/自身Loaded，PresencePanel也过滤子树生命周期，避免弹窗/模板加载重播主场景。
- MODAL：主窗及嵌套工具所有权链暂停进场、隐藏但不重建原内容；不透明主题纯色+单个20fps缓慢平移斜纹替代，覆盖保持到实际隐藏/关闭，最后弹窗结束才恢复并进场；主界面读数/读取暂停，业务Agent不停止。工具窗整壳300ms透明度/8 DIP进出与原最终关闭守卫接线。
- MOT：动画绘线仅临时语义红色，完成/中断恢复原画刷；侧栏220ms/30fps实列宽与列表淡入/出，结束回到原列契约，列表实例、选择和滚动保留。
- ENG/运行：构建首次发现WinForms/WPF ListBox歧义，已显式指定WPF别名；最终Release App构建成功，12.00秒、0警告/0错误。新版GUI PID7376已启动，UIA确认窗口可见、启用且有焦点，仍为1728×972物理像素；仅核实启动，不算交互/性能/像素验收。原Release Agent PID13004保留，没有启动测试或探针进程，也未创建测试临时数据。
- 未验：真实流畅度/CPU/GPU、截图与双主题效果、所有弹窗及嵌套/关闭取消/应用退出、侧栏快速反向/减少动画旅程均未执行；历史回归不等于本次通过。

## 2026-11-10 — 随机逐字、详情切换和窗口记忆（R113，用户自行测试）

范围沿[原设计R113](<docs/design-system.md>)，覆盖此前“不逐字”要求。用户上一轮的“不跑其它测试、启动后自行测试”边界继续沿用；没有把旧2862回归或构建当作本轮G3。

- 实现：按真实字素位置随机揭示的OpacityMask，不替换字符串、绑定或复制内容；标签、读数、按钮保留分组先后，日志只读正文参与。服务名/主机ID/日志Entry切换复用完整进场，顶部选中块、非中性Border与Panel底色也延后出现；先挡住新内容、等绑定布局完成再播，首帧时钟在附着后明确定位零点。
- 窗口：默认1152×648 DIP（16:9）；通过既有每用户外观设置增量保存尺寸，主题更新也保留尺寸及其他字段。650ms防抖、关闭兜底，保存正常/RestoreBounds尺寸，过滤无效值、恢复时限制工作区；不修改Agent协议、业务配置或恢复策略，不另建配置文件。
- ENG/运行：已仅停止经路径核对的旧GUI，保留Release Agent PID13004；两次仅Release App编译均成功，最终8.26秒、0警告/0错误。新版GUI PID4420已启动，UIA确认窗口可见、启用，外框1728×972物理像素（150%下1152×648 DIP，16:9）；仅核实启动，不冒充交互/像素验收。没有启动测试进程或测试临时数据。
- 未验：随机显现的实机观感、快速列表反向切换、白色块的连续帧、尺寸跨重启保存/多DPI/最大化还原、复制/复杂字素与减少动画的运行旅程，均待用户检查。旧的移除功能/固定尺寸/闪烁断言未同步或执行，不宣称测试已通过。

## 2026-11-10 — 实机反馈快速修正（R112，用户自行测试）

用户明确要求快速修改、启动交其测试，且不执行其它测试；沿[原设计入口R112](<docs/design-system.md>)接续，不以R111的旧回归结果证明本次改动。

- 已改：交互控件/选中行/色块与原生编辑器作为整体平滑进场，鼠标所在文字不短闪；日志条目详情按不可变Entry单次200ms过渡；共享ComboBox去除投影/上下间隙；卷列表复用整机磁盘的标题、全宽进度条和容量锚线，字号不变。
- 已删：服务追踪开关及VM命令、添加表单的可选追踪、未追踪提示、已观测时长/覆盖率文字、服务两侧和主机/异常页底部状态栏及预留行高。异常日志空列表保留未读/读取失败说明，不把读取失败伪装为没有日志。
- 数据边界：所有启用服务本就参与资源/健康采集，继续保持1s/10s；未改Agent、Core/IPC或现有任务的恢复资格，不批量改配置。新任务沿原默认ExpectedRunning=true，不删除内部未知/新鲜度/恢复安全门禁。
- ENG：首次仅构建发现WinForms/WPF的TextBox/ComboBox名称歧义，已加显式WPF别名；最终Release App构建成功，0警告/0错误（8.51秒），已启动新版GUI PID5236，原生窗口存在、可见、启用且拥有焦点（仅核实启动，不算交互验收）。原Release Agent PID13004保留，未启停任务。自动测试、手工UI旅程、截图与持久化验收均按用户要求不执行，VIS/FLOW/DATA保持未验证；移除功能对应的旧测试尚未同步/执行，旧2862通过不能套用于本轮。

## 2026-11-10 — 分阶段进场、紧凑列表与全宽底栏（R111）

用户明确授权先改实装再检查；范围沿用[设计 R111](<docs/design-system.md>)，不重开视觉方向。原工作区未提交修改保留，未 stage/commit/push；本节时序覆盖 R110 的约300ms，原位画布、正交线、可逆退出、减少动画与 R108 实时读数过渡保留。

### 实装与分项证据

| 类别 | 实际结果与边界 |
| --- | --- |
| ENG | Debug 定向 **48/48**、0跳过，[TRX](<TestResults/R111/r111-targeted.trx>)；最终全量 **2862/2862**、0跳过，7分57秒，[最终TRX](<TestResults/R111/r111-full-debug-r3.trx>)。Release GUI 构建0警告/0错误。Core单测及Release集成未重跑，中间失败保留如下。 |
| MOT | [LineMotion](<src/SystemAutorun.App/Services/LineMotion.cs>)按空间区域及 Label/Readout/Action 分组，线220ms、标签滞后200ms、读数340ms、动作360ms、离散闪动100ms，最迟约620ms收稳。数值及动作的阶段通过可继承附加属性标记；帧采样、生命周期及数据 tick 不重播回归保留。 |
| FLOW / 侧栏 | [SidebarAssist](<src/SystemAutorun.App/Services/SidebarAssist.cs>)将真实栅格在52 DIP与原0.3*（240–340 DIP）间切换，不重建列表；折叠仍保留入口和加号。双主题×四档桌面尺寸检验实际列宽、列表可见性、选中、筛选与滚动偏移、跨页状态。Release实窗经原生UIA分别收起服务与主机列表，已看收起后主区域实际扩展的像素。 |
| DATA / 内容真值 | 服务行只有名称及FAIL告警，禁用/未知不伪装正常；主机行只有名称及本机标签，状态细节保留自动化帮助文本。标题正常OK隐藏、FAIL/UNKNOWN保留；实测5项服务、1个服务器和UPTIME秒字段，包含0/负值、跨分钟/跨天边界。没有更改业务状态、Agent、IPC或恢复策略。 |
| VIS / 列表与读数 | 已看合成真实WPF[服务行](<docs/visual/sa-20261110/r111-compact-staged/state/r111-services-minimal-dark.png>)、[正常主机浅色](<docs/visual/sa-20261110/r111-compact-staged/state/r111-host-header-OK-light.png>)，以及920×580、960×620、1320×820、1620×651的主机/侧栏采样。Release实窗[服务](<docs/visual/sa-20261110/r111-compact-staged/live-services-150.png>)、[收起服务](<docs/visual/sa-20261110/r111-compact-staged/live-services-collapsed-150.png>)、[主机](<docs/visual/sa-20261110/r111-compact-staged/live-hosts-150.png>)、[收起主机](<docs/visual/sa-20261110/r111-compact-staged/live-hosts-collapsed-150.png>)已实际查看；后四张为150%桌面实屏截图，不是合成监测值。 |
| VIS / 菜单 | 消除MD 5.3.2默认16 DIP上下留白及-16偏移/阴影空隙。共享模板保留PART_Popup、勾选、助记键、图标与子菜单箭头，补IsHighlighted/IsSubmenuOpen/禁用反馈；菜单与嵌套菜单上下端实际几何均为1 DIP边框，无额外垫高。已看[子菜单](<docs/visual/sa-20261110/r111-compact-staged/state/r111-submenu-dark.png>)像素及模型→勾选绑定。物理键盘/指针连续交互未作全项验收。 |
| VIS / 标准弹窗 | 五类底栏共享UniformGrid，单按钮铺满、双按钮等宽，扣除既有16 DIP外边距后端点接齐、按钮间距8 DIP；双主题×四档owner尺寸均测真实几何、默认/取消及无外框焦点。已看[远程双按钮](<docs/visual/sa-20261110/r111-compact-staged/state/r111-footer-remote-dark-920x580.png>)、[确认双按钮](<docs/visual/sa-20261110/r111-compact-staged/state/r111-footer-message-dark-920x580.png>)、[单按钮](<docs/visual/sa-20261110/r111-compact-staged/state/r111-footer-info-light-960x620.png>)、添加/诊断/详情的真实WPF截图。未扩大浏览/图标工具按钮、Recovery/SelfStartup专用底栏，既有业务回调测试保留。 |

### 失败记录、运行与未验

- 初次新测试编译发现截图helper仅收Window及四处xUnit2031分析器错误；helper改收FrameworkElement（截图逻辑不变）、断言改为筛选谓词重载后定向48项通过。没有删除断言或跳过用例。
- 首次全量2860/2862：两项旧监控页测试仍硬编码300ms，按已批准R111更新为约600ms及完成后移除所有clock，复查双主题通过。第二次全量2861/2862：启动项最小列宽断言在150%遇到278.6667 DIP；查明280 DIP列减去1 DIP分隔线经设备像素取整后为1.3333 DIP，测试改用实际DPI推导取整分隔线，不改启动项布局、不随意降低常量。
- 两次人工探针生命周期正常回收，但外部UIA脚本均未完成旅程：首轮观测到窗口最小化后找不到文本；第二轮Invoke后未等布局便读坐标而失败。保留[首轮失败](<docs/visual/sa-20261110/r111-compact-staged/native/flow-failed.json>)与[第二轮失败](<docs/visual/sa-20261110/r111-compact-staged/native-r2/flow-failed.json>)，其1/1生命周期TRX不冒充原生旅程通过。后续Release实窗逐步UIA→新截图验证了两个侧栏，不把脚本失败改写成成功。
- 后续Release原生旅程：实际设置菜单→展开外观子菜单，已看[主菜单](<docs/visual/sa-20261110/r111-compact-staged/live-menu-150.png>)及[子菜单](<docs/visual/sa-20261110/r111-compact-staged/live-submenu-150.png>)，上下留白已去除且深色勾选仍显示；未切换主题或其他开关。随后从该菜单打开真实模态远程窗，已看[全宽双按钮](<docs/visual/sa-20261110/r111-compact-staged/live-remote-footer-150.png>)，UIA读回两按钮均568物理像素、间距12物理像素（150%下为8 DIP），仅取消，不添加或保存。
- Release GUI PID26168已打开供检查；原Release Agent PID13004保留。真实任务未启停，未提交表单、远程连接、Run/Boot或业务配置变更；三个受保护配置SHA256与检查前基线一致。最终无testhost残留；自有UIA脚本及探针通信文件回收，只保留截图、失败记录与TRX。原生工具曾报告窗口短暂前台后返回，不宣称全程无焦点干扰或物理焦点恢复已验。
- **未验仍未验：** 原生物理Tab/Shift+Tab/Escape、全部菜单/弹窗的外部完整旅程、Windows系统减少动画实机切换、每一个短闪的连续主观观感、125%完整矩阵。已有WPF行为/几何和有限原生截图不是完整G3全项通过。

## 2026-11-10 — FakeUI 进场与共享对齐修正（R110）

用户明确“开始执行”；限定合同见[设计 R110](<docs/design-system.md#2026-11-10--fakeui-进场与共享对齐修正r110>)。沿 R109 灰阶/直角，不改变数据、业务动作和窗口尺寸；本节替代 R109 的叠加整页淡入/2 DIP 上移及对角揭线。工作区原有大量未提交修改保留，未 stage/commit/push。

### 实装与证据

| 类别 | 实际结果与边界 |
| --- | --- |
| ENG | Debug 定向 **66/66**；之后全量集成 **2848/2848**、0 跳过，7分17秒，原始 [TRX](<artifacts/qa/r110-results/r110-debug.trx>)。Release GUI 构建通过、0警告/0错误。仅改 App/界面测试，Core 单测及 Release 集成未重跑。 |
| MOT / 生命周期 | [LineMotion](<src/SystemAutorun.App/Services/LineMotion.cs>)改为按真实边段画 DrawingBrush（170ms、最多60ms分组错开、3 DIP 前沿），字形延后130ms、100ms离散两次短闪，最迟290ms稳态；首个 Rendering 续播暂停时钟。结束/中断移除 owned clocks 及事件，恢复原共享画刷/绑定。主窗首次显示、第一次进入折叠页、页返回、工具窗、隐藏/最小化/卸载、继承 ReduceMotion、主题恢复及数据 tick 不重播有回归。系统减少动画的实现路径保留，但本轮未修改 Windows 系统设置实测。 |
| ALN / 共享布局 | [PresencePanel](<src/SystemAutorun.App/Controls/PresencePanel.cs>)不再进场淡入或位移；保留160ms可逆退出和立即退役旧输入。中性背景/页层序遮挡旧页，修复短闪空隙的旧内容透出。服务/主机宽轨240–340 DIP、主区域20 DIP内距、两侧28 DIP服务底栏、44 DIP日志筛选/表头、34 DIP主机读数标题行；根Window共享布局取整。双主题四个桌面尺寸均断言实际几何；IP保留20 DIP。 |
| VIS / 已看像素 | 本轮 **96 DPI / 100%**。已看真实WPF离屏[主机最小窗](<docs/visual/sa-20261110/r110-fakeui/state/r110-host-dark-920x580.png>)、[浅色日志最小窗](<docs/visual/sa-20261110/r110-fakeui/state/r110-exceptions-light-920x580.png>)、[主机进场](<docs/visual/sa-20261110/r110-fakeui/state/r110-host-dark-040.png>)与[后段](<docs/visual/sa-20261110/r110-fakeui/state/r110-host-dark-160.png>)，以及主窗初显/工具窗双主题采样。原生 PrintWindow [服务页](<docs/visual/sa-20261110/r110-fakeui/native-services.png>)、[异常页](<docs/visual/sa-20261110/r110-fakeui/native-exceptions.png>)和[真实模态添加窗](<docs/visual/sa-20261110/r110-fakeui/native-add.png>)已实际查看：底栏/表头接齐，工具小于主窗且主要动作可见。合成数据，不是生产监测值。 |
| VIS / 录制边界 | [原生切页 GIF](<docs/visual/sa-20261110/r110-fakeui/native-tab-r2.gif>)由 UIA 与 PrintWindow 同一有界脚本同步触发，100次采集合并成5个变更帧，实际时间见[CSV](<docs/visual/sa-20261110/r110-fakeui/native-tab-r2.csv>)；已看[起始](<docs/visual/sa-20261110/r110-fakeui/native-r2-01.png>)、[绘线](<docs/visual/sa-20261110/r110-fakeui/native-r2-02.png>)、[收口](<docs/visual/sa-20261110/r110-fakeui/native-r2-03.png>)像素。它记录真实窗口而非插值演示，但采集会合并/漏掉短闪，不作为每个脉冲均被显示或完整连续观感已验的证据。离屏文件名中的0/40/90等是采样请求时刻，实际耗时另存CSV，不是精确动画起点。 |
| FLOW / 原生 | 隔离 fixture 运行实际主窗，经 Windows UIA 切主机→异常→服务；在异常搜索写入标记，切页返回后 ValuePattern 原样读回。服务入口打开真实 owned 模态添加窗，调用取消后主窗可继续切页；未提交新增。第二次原生往返再次验证筛选保留，主动发完成信号，探针 **1/1** 正常结束。首轮[交互记录](<docs/visual/sa-20261110/r110-fakeui/native-flow.json>)保留。UIA 并非物理键鼠；原生后端曾报告窗口短暂前台后返回，不宣称零焦点干扰或物理焦点恢复已验。 |
| DATA | 没有变更 Agent、IPC、配置结构、监测/恢复策略；秒级读数仍走R108平滑，不重播入场。全部写入/取消测试使用隔离 root；本轮未操作真实工作负载、Run/Boot、远程主机或真实配置。原 Release Agent PID13004保留，未停止/替换。 |

新增[FakeUiMotionTests](<tests/SystemAutorun.IntegrationTests/FakeUiMotionTests.cs>)覆盖正交线段几何、固定3 DIP前沿、原画刷不变、纯离散脉冲取值、主窗/首访页/工具入口、最小化/隐藏/减少动画清理与双主题四尺寸共享锚线。既有[Presence](<tests/SystemAutorun.IntegrationTests/PresenceMotionTests.cs>)、[工具窗](<tests/SystemAutorun.IntegrationTests/ToolWindowMotionTests.cs>)及[监测页](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>)断言按新合同更新，保留退出中断、输入退役和数据刷新断言，不靠删除检查通过。

### 本轮问题、清理与未验

- 实装采样发现“第一次打开先前折叠的主机页不播放”：进入请求早于 Loaded/可见布局，旧分支直接 Settle。已补待就绪入口及初次整窗/页的互斥所有权，加入首访断言并重跑66项及全量；不是只改第二次切页测试。
- 初次编译暴露 `Brushes` 歧义、fixture缺少remote reader及CreateClock参数名错误，均修正后再跑以上最终结果。中间一次首访断言失败用于定位上述实际缺陷，没有跳过或放宽该断言。
- 首轮人工探针操作后未及时写完成信号，4分钟到期以失败退出；finally回收窗/fixture，PID12912已退出。第二轮PID15356完成UIA往返和录制后正常结束；不是把首轮超时算成通过。第一段异步录屏只录到静态帧，已删除，保留同步r2原件。
- 所有本轮测试/原生探针/录制进程均已退出；核验绝对路径与非reparse后删除两个自有探针目录、临时脚本和失败静态录制，只保留截图、有效GIF/CSV和TRX。原PID13004仍存活。`git diff --check` 无空白错误（只有原有换行提示）。
- **未验仍未验：** 125%/150%桌面DPI、物理鼠标/Tab/Shift+Tab/Escape及焦点返回、Windows系统减少动画实机切换、全部短闪的连续主观观感。本轮只交付实装、已验证的100%像素/原生UIA子集及工程结果，**不宣布完整G3全项通过**。

## 2026-11-09 — 去重与科幻线条进场（R109）

范围与用户选择见[设计 R109 节](<docs/design-system.md#2026-11-09--去重与科幻线条进场r109>)。新增共享[LineMotion](<src/SystemAutorun.App/Services/LineMotion.cs>)，由[PresencePanel](<src/SystemAutorun.App/Controls/PresencePanel.cs>)在页/工具进场时触发，主窗首次Loaded触发一次。

| 类别 | 实际状态与证据边界 |
| --- | --- |
| ENG | Debug 集成全量 **2841/2841**（7分41秒），含新增[进场用例](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>)双主题：切页播放、动画中仍可交互、结束后边框回到同一共享主题画刷且无残留时钟、文字不透明度回 1、数据刷新不重播、中途开减少动画立即停止、开启后不播放。此前一轮关联集 277/278，唯一失败为 ToolWindowMotion 的中段透明度采样未命中（负载相关），单独连跑 3 次 19/19。仅改 App，Core 未重跑；未构建 Release。 |
| VIS | 已看 150% WPF 离屏实装：[进场 45ms](<docs/visual/sa-20261109/r109-dedup-motion/state/r109-entrance-dark-1.png>)、[110ms 亮头](<docs/visual/sa-20261109/r109-dedup-motion/state/r109-entrance-dark-2.png>)、[浅色 110ms](<docs/visual/sa-20261109/r109-dedup-motion/state/r109-entrance-light-2.png>)、[静止](<docs/visual/sa-20261109/r109-dedup-motion/state/r109-entrance-dark-4-rest.png>)；去重后[主机 920×580](<docs/visual/sa-20261109/r109-dedup-motion/state/monitoring-populated-920x580.png>)（无运行保障列、整机磁盘 19% / 880 GB / 4.5 TB、页脚只留状态）与[服务页](<docs/visual/sa-20261109/r109-dedup-motion/state/restart-uptime-main-24.png>)（头部只有名称与运行时间，历史行无“24 小时”）。合成数据，非生产读数。帧按真实时间采样，不是逐帧视频。**未验：** 主窗首次显示与工具窗进场的像素、真实运行版本实窗、物理键鼠、浅色以外的 DPI。 |
| DATA | 整机磁盘为全部已就绪卷的已用/总量和；单卷列表与 Agent/IPC 未改。 |

## 2026-11-08 — 1 秒实时刷新与补齐交互动效（R108）

范围与用户选择见[设计 R108 节](<docs/design-system.md#2026-11-08--1-秒实时刷新与补齐交互动效r108>)。

| 类别 | 实际状态与证据边界 |
| --- | --- |
| ENG | Debug 全量：Core **997/997**、Integration **2829/2829**（9分54秒），之后新增[读数动效](<tests/SystemAutorun.IntegrationTests/ReadingMotionTests.cs>)8例与两例秒级读取（不重排列表、隐藏/暂停/忙态跳过、失败静默），定向 **10/10**；新增后未重跑全量。Agent 新增秒级 tick 不探测/不落盘/不发布、配置失效强制健康轮、资源失败去掉进程 uptime 三例并入全量。读数动效测试在系统关闭动画时直接返回，不构成该设置下的动画证据。 |
| DATA | 替换后的真实 Release Agent（PID 13004）经 `sautorun monitor status` 连续 4 次：`samplingIntervalSeconds=1`，主机观测每秒前进，任务观测与历史写入保持 10 秒一跳（22:22:08→22:22:18）。旧 GUI PID 27384 由精确路径核验后结束，旧 Agent 30332 经 `daemon stop` 正常退出；`auto_start_missing_on_launch=false`，未触发任务启停。 |
| VIS / FLOW | 已看新 Release 150% 实窗 PrintWindow：服务页页脚显示“1s”；主机页两张间隔约 2 秒的截图时间 22:33:36→22:33:38、CPU 38%→65%、读数条同步变化，存储/网络行保留。**未验：** 菜单/下拉/tooltip 淡入、悬停渐变、复选框过渡、启动项管理窗进出的实窗像素与交互（目标窗被用户其他窗口遮挡，为不打断用户未继续前台操作），逐帧观感，浅色主题，减少动画实机开关。 |

## 2026-11-06 — 持续运行、CLI 与主次窗口（R107，进行中）

用户要求按评估建议顺序修复，并补充“弹出的子窗口尽量比主窗口小”。顺序为正确性 → 常驻降载及历史/日志读取 → 紧凑窗口与完整短动画 → CLI 帮助/覆盖说明 → 隔离回归与长稳验证。不开放尚未批准的 startup/Boot 原生写入口，不修改真实任务、启动项或现有运行进程。

| 类别 / 切片 | 实际状态与证据边界 |
| --- | --- |
| ENG / 第一批正确性 | Debug 首轮定向集成 **69/69**；补充 JSON/YAML 输入 caller cancellation 并修正 help/version 提前返回后，扩大到所有名称含 `Cli` 的用例、`GuiConfigurationControlTests`、普通/纳管添加失败保留及主窗回调接线，**581/581**（29秒）。运行只使用隔离 root；Console 测试保持 stdin 打开且不写入，`--timeout 1` 后自行退出 6，返回 `timeout` JSON，无 Agent 或共享状态写入。 |
| DATA / CLI | 单次读取取消返回非零 `cancelled`；`monitor watch` / `logs --follow` 仍以 0 正常结束。JSON/YAML 输入用独立 deadline，输入完成前不连接/发送 RPC、不分配待提交操作 ID；同步阻塞的 `Console.In` 隔离到一次后台读取，超时仅保证停止等待和不进入 RPC，不冒称底层非协作读取已取消；Console 退出回收该读取。 |
| FLOW / 添加 | 普通添加复用已有提交回调，确认成功才关窗；失败保留同一表单，提交中阻止重复保存和关闭；忙态显式提示不再静默丢弃。未知提交保留原 ID 并阻止本 GUI 会话再次添加；该保护不等同于跨重启持久恢复。模拟窗口回归通过；原生UIA合成保存失败、输入保留及像素检查已补（见后文），不等同于真实持久化失败或物理键鼠验收。 |
| G2 / 窗口与动画 | [尺寸关系候选 r1](<docs/visual/sa-20261106/r107-compact/g2/main-add-size-r1.png>)为真实生图，1536×1024 PNG，未传截图，仅功能/风格文字。已检查像素；图片擅自改成表格布局、加入绿色状态和胶囊开关，并错误说明常驻追踪等于自动重启，**不能直接作为实装模板**。用户明确选择“可以，只参考比例，保留现有布局与风格”，**G2 限定范围及补充规则已批准并授权实施**：仅采用主次窗口比例与表单紧凑度，原 r4/R105 布局、状态语义、复选框及原文案不变。 |
| VIS / 新尺寸与完整动画 | **尺寸已初步实现，整体VIS/FLOW未完成。** 主窗960×620 DIP、最小920×580；日志/启动项/远程800×500；添加520宽。共享[工具窗约束](<src/SystemAutorun.App/Services/ToolWindowLayout.cs>)按根owner的88%宽/84%高与当前屏幕工作区设上限，随尺寸/DPI更新并在关闭时解绑。已看150%真实WPF离屏[主机](<docs/visual/sa-20261106/r107-compact/ui-debug-r3/r107-host-meters.png>)和[最小owner下的展开表单](<docs/visual/sa-20261106/r107-compact/ui-debug-r3/r107-add-expanded-owner-920x580.png>)；前者发现并修复容量文本挤掉存储卷标签/用量条，后者有内部滚动且底部动作不裁切。不是原生桌面截图/物理键鼠证据；上述早期高级区样张仍使用原MaterialDesign过渡，不作为减少动画证据；后续共享披露修复见下行。三页、遮罩、诊断与日志窗已按下述限定范围验证；其余工具关闭、完整焦点与多DPI验收仍待完成。 |
| ENG / 有界异常尾读 | 每文件最多256 KiB尾部窗口＋编码探测，保留UTF-8、UTF-16/32 BOM及大小端、LF/CRLF/CR、中文/代理对、完整无时间戳记录与堆栈合并；只丢弃首个可能不完整的边界行。字节/行上限计入Truncated。初轮把读失败也计入Truncated导致既有CLI锁文件用例失败，已恢复原读失败语义与原断言：sourceCount仍为尝试数，读失败可能被省略，false/empty不能证明可用或健康。修正后Core **983/983**、异常CLI＋输入取消集成 **54/54**。 |
| ENG / 历史投影 | 提取共享纯参数校验，Store不再为校验调用一次完整空投影；桶聚合改用已有spans的索引范围，不复制每桶临时List，跨多桶span仍逐桶裁剪。未改读文件/反序列化、持久化、锁、预算、epoch或删除水位。新增跨1/120/1000桶与ValidUntil、锁占用/已取消时非法参数优先级、同长度且恢复mtime改写后必须读到新内容；最终Debug Core **997/997**。这是减少确定的无用分配，未声称读IO、锁争用或长稳内存已改善。 |
| ENG / 隐藏降载与忙态分离 | 已实现：隐藏/最小化停dashboard时钟及历史/日志展示读取、跳过展示缓存写入；本机被动快照继续维护托盘告警事实。后台刷新独立IsRefreshing，用户操作仍用IsBusy，操作开始取消旧展示读取；恢复可见时补刷新，若命令占用则在可执行后补一次。新增隐藏/恢复、最小化、慢读取消及用户操作竞态测试。首次全量2567/2568，唯一失败为旧用例仍期待远程刷新IsBusy=true；按新契约改为IsRefreshing=true/IsBusy=false，保留远程禁止添加及返回本机获得新快照的断言，定向复验通过。 |
| ENG / 新尺寸定向回归 | 修正上述契约及窗口夹具Owner显示顺序后，MonitoringWindow/DashboardReadModel/DashboardMonitoring/GuiConfigurationControl共 **48/48**，0跳过，17秒。覆盖四档主窗、根owner嵌套、禁止工具最大化、展开滚动与固定底部动作；缩小后真实发现的零宽用量条已修复，不删除数值比例断言。随后全量Debug **2566/2569**，3失败均为原尺寸断言（工具最小840/520、表单必须再长200 DIP）；已改为新760/460与800/500默认契约，新增紧凑矩阵、列表可用高度、实际滚到最后字段且不裁切的断言；再修正280-DIP清单列须扣1-DIP结构分隔线的旧阈值，受影响Startup/LogViewer/DialogChrome/RemoteHosts **32/32**、0跳过、24秒。修正后未重跑整个2569用例，不称新全量通过。 |
| VIS / FLOW原生限定 | 独立隔离probe使用真实HWND及UIA Invoke/ExpandCollapse/ScrollPattern，已看PrintWindow像素：[主窗960×620](<docs/visual/sa-20261106/r107-compact/native-r1/main.png>)→[添加520×358](<docs/visual/sa-20261106/r107-compact/native-r1/add-basic.png>)→[展开520×520](<docs/visual/sa-20261106/r107-compact/native-r1/add-expanded.png>)→[滚到底部](<docs/visual/sa-20261106/r107-compact/native-r1/add-scrolled.png>)→取消→[日志800×500](<docs/visual/sa-20261106/r107-compact/native-r1/logs.png>)→返回。实际滚动0→100%，末尾检查类型/匹配/超时及底部动作可达；返回后主窗搜索仍为“媒体”。主窗/表单/工具比例符合限定G2。合成任务、无执行器，日志当前选控制源真实返回source missing，未伪称有日志。**不是物理键鼠、焦点顺序、失败提交、全部工具/多DPI或逐帧动效验收。** 原生模态曾自行前置，工具报告恢复原前台；未主动激活用户浏览器。probe **1/1**通过，原始信息见[记录](<docs/visual/sa-20261106/r107-compact/native-r1/probe.txt>)。 |
| ENG / 共享披露动效 | [DisclosurePresenter](<src/SystemAutorun.App/Controls/DisclosurePresenter.cs>)和[共享模板](<src/SystemAutorun.App/Themes/Disclosure.xaml>)替换添加/登录启动/恢复三个显式消费者。160ms高度裁剪、透明度和箭头旋转成对往返，不缩放文字；反转从当前值接续，代次检查排除旧回调，减少动画立即收尾，卸载解除系统事件与时钟。首轮发现新时钟首帧前跳到目标值，已让其基值保持当前显示值；检查实际HasAnimatedProperties，不把尚未激活的GetValueSource标志当作没有时钟。最终DisclosureMotion/DialogChrome/MonitoringWindow/RecoveryGui/SelfStartupGui **71/71**，0跳过，30秒；涵盖中途反转、减少动画中途启用、关闭清时钟、内容重测与深浅色正文。采图定向 **9/9**。 |
| VIS / 披露与语义正文 | 已查看150% WPF实装：[深色末尾字段](<docs/visual/sa-20261106/r107-compact/disclosure-r1/add/disclosure-dark-scrolled.png>)、[浅色末尾字段](<docs/visual/sa-20261106/r107-compact/disclosure-r1/add/disclosure-light-scrolled.png>)、[登录启动](<docs/visual/sa-20261106/r107-compact/disclosure-r1/self/details-1120x700.png>)及其宽档、[恢复](<docs/visual/sa-20261106/r107-compact/disclosure-r1/recovery/details-scrolled-1120x700.png>)及其宽档。首次像素审查发现自定义Expander未指定Foreground，未显式着色的健康标题/账户变成黑字；已统一绑定TextPrimary并补双主题继承断言，重新采图复查。恢复夹具进一步等待真实时钟结束，避免自然布局已到位却把过渡中的箭头当终态。内部内容可滚出，固定窗口标题与底部动作保留；不新增焦点框。 |
| FLOW / 原生披露限定 | 独立无Agent/配置/执行器的150%探针经原生UIA展开→60ms后收起→35ms后再展开→滚动0到100%→收起→标题关闭，**1/1**通过。已看[折叠](<docs/visual/sa-20261106/r107-compact/disclosure-r1/native/collapsed.png>)、[展开](<docs/visual/sa-20261106/r107-compact/disclosure-r1/native/expanded.png>)、[滚到底](<docs/visual/sa-20261106/r107-compact/disclosure-r1/native/scrolled.png>)和[返回折叠](<docs/visual/sa-20261106/r107-compact/disclosure-r1/native/returned.png>)的PrintWindow/无遮挡screen-region像素；高度358→520→358，收起后高级字段从当前UIA树消失。记录[属性变化](<docs/visual/sa-20261106/r107-compact/disclosure-r1/native/frames.csv>)显示中间进度、两次改向及终态clock=false；这是属性事件采样，含同一调度回调内的短暂基值，**不是合成器逐帧视频或精确时延测量**。原生UIA可能改变目标窗焦点；未主动激活浏览器。物理键鼠、模态保存/取消结果及完整焦点旅程仍未在本片复验。 |
| FLOW / 命令级帮助 | [帮助渲染](<src/SystemAutorun.Cli/CliHelp.cs>)支持`help COMMAND`和`COMMAND --help`，组与命令均从真实路由解析，保留大小写规范化与名称上下文；未知/未实现主题返回2，`--`后的字面名称不误作help。帮助跳过执行所需payload/revision/yes，但正常执行的原校验保持，业务路由/权限不扩张。各注册命令的文本与JSON内容一致；测试使用拒绝输入/连接的替身。真实Console在stdin仍打开时直接退出0，不创建指定root；另已查看真实`help startup self --json`输出，仅列status/prepare/set，`startup boot install --help --json`返回2/invalid_argument。纯Console帮助沿已批CLI范围，未改GUI，G2/新增VIS不适用。 |
| ENG / 输入示例与覆盖文档 | 任务、策略patch、三种主机写入的JSON样例经实际CLI输入/严格DTO反序列化到记录型transport，**不是原生目标或生产写入验收**；self set只指导使用新鲜准备响应data，不伪造模板，保留未决提示封锁/原ID查询要求。新增[使用说明](<docs/cli-guide.md>)和当前GUI/CLI矩阵，修正README把self prepare/set遗漏成未实现的旧说法，并把架构原目标表明确标为非当前清单。核对既有GUI Boot管理员Helper菜单仍存在；它不等于CLI Boot写入获批，本轮未运行/迁移。最终Debug全部名称含Cli的回归 **649/649**，0跳过，35秒；此前帮助/取消定向83/83，真实Console探针也通过。不称全应用或Release全量回归通过。 |
| 实现 / 遮罩成对过渡 | 主窗遮罩改用[PresencePanel](<src/SystemAutorun.App/Controls/PresencePanel.cs>)：160ms透明度往返，离场即退出鼠标命中及输入，动画结束再Collapsed；快速重开接当前值，代次检查拒绝旧完成回调。继承ReduceMotion中途开启、父窗隐藏及卸载均收尾清时钟，卸载解绑系统事件；保留原Scrim语义画刷与布局。主窗改用具名工具事件处理器，工具关闭及主窗销毁时解除订阅。没有给任意Window追加Closing拦截，也未改变工具本身关闭、保存保护、模态结果或日志资源释放顺序。 |
| ENG / 遮罩与窗口关联回归 | 新增[PresenceMotionTests](<tests/SystemAutorun.IntegrationTests/PresenceMotionTests.cs>)及[实际主窗嵌套工具用例](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>)，覆盖进退反转、旧回调、两方向减少动画、隐藏/重显/关闭清理、绑定保持及父级禁用；通过实际Show/Hide/Close核对嵌套工具关闭后遮罩仍在、最后工具隐藏时退场、重显反转及最终解除命中。定向 **31/31**；扩大到PresenceMotion/MonitoringWindow/DisclosureMotion/DialogChrome/LogViewerGui/RecoveryGui/SelfStartupGui后 **106/106**，0跳过，约1分钟。最后强化夹具异常路径也关闭隐藏窗口并排空卸载，再复验 **7/7**。采图初轮因输出目录未预建而6例失败，未改产品/断言；建目录后 **6/6**。 |
| VIS / 遮罩限定 | 已看150% DPI的真实WPF离屏渲染：[深色打开](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-dark-960-open.png>)、[退出开始](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-dark-960-leaving.png>)、[返回](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-dark-960-returned.png>)及对应[浅色打开](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-light-960-open.png>)、[退出开始](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-light-960-leaving.png>)、[返回](<docs/visual/sa-20261106/r107-compact/scrim-r1/scrim-light-960-returned.png>)；另查看920×580双主题返回与1320×820双主题遮罩，共10张。尺寸、原布局及边界覆盖保持，退出后无残余遮罩或新增焦点框。图只捕获主窗、不含独立工具HWND；离场样张采到最初变化，不当逐帧或精确时延证据。隔离管道/未刷新视图不代表生产监测数据；本片未执行原生UIA/物理键鼠、完整返回焦点或系统动画开关操作，**整体VIS/FLOW仍未完成**。 |
| 实现 / 三页成对过渡 | 服务、主机、异常日志统一使用[PresencePanel的Page变体](<src/SystemAutorun.App/Controls/PresencePanel.cs>)：160ms透明度与2 DIP位移同进度往返，退出立即禁用旧页输入，动画完成再Collapsed；减少动画、隐藏/关闭及快速反转沿共享清理。移除三页原FadeOnShow；[监控宿主](<src/SystemAutorun.App/Controls/MonitoringView.xaml.cs>)不挂动画，只根据叶子与页脚实际Visibility保留到退场结束，避免父子双重淡入或父级提前截断。状态页脚单独淡出，不叠加叶子动效。SelectTab、回本机保护、快捷键、数据/提交契约未改。对应REU-4–6、MOT-1–5；完整物理焦点验收仍开放。 |
| ENG / 页面与关联窗口 | 新增六个有向切页、快速返回、继承减少动画、隐藏/恢复、数据更新不重播及输入/筛选保持断言。首轮发现UserControl.Style内ElementName绑定不能得到目标叶子、宿主不显示，改为显式Source绑定后定向 **4/4**。扩大回归初轮 **133/134**：新反转测试采样过早落入0.001终点吸附区；改为采真实中段，保留8位精度等值断言，复验 **134/134**。后续指标几何修复后PresenceMotion/MonitoringWindow/DisclosureMotion/DialogChrome/LogViewerGui/RecoveryGui/SelfStartupGui/DashboardMonitoring/DashboardReadModel合计 **136/136**、0跳过、62秒。最后将几何样例改用完整合成HostStatusSnapshot，避免仅写文字造成用量条矛盾，再定向 **2/2**。不是全solution回归。 |
| VIS / 最小窗指标修复 | 初版实际[920 DIP窄窗证据](<docs/visual/sa-20261106/r107-compact/pages-r1/monitoring-populated-920x580.png>)发现“最高磁盘”与读数重叠。三个数值标题统一为[MetricHeadingPanel](<src/SystemAutorun.App/Controls/MetricHeadingPanel.cs>)：空间足够同排、不足自然分行，保留原字号/文字及数值右锚；超长读数省略并提供完整tooltip。双主题×9宽度（920–1620）×75%/100%/长卷名实测不相交、边界与字宽，CPU/内存/磁盘24 DIP不缩小。已看修后150% WPF离屏[最小深色](<docs/visual/sa-20261106/r107-compact/pages-r2/metric-headings-dark-920.png>)、[最小浅色](<docs/visual/sa-20261106/r107-compact/pages-r2/metric-headings-light-920.png>)、[常用主机](<docs/visual/sa-20261106/r107-compact/pages-r2/monitoring-populated-960x620.png>)、[宽浅色主机](<docs/visual/sa-20261106/r107-compact/pages-r2/light-host-1320x820.png>)及[异常日志](<docs/visual/sa-20261106/r107-compact/pages-r2/monitoring-exceptions-960x620.png>)/[服务](<docs/visual/sa-20261106/r107-compact/pages-r2/light-services-960x620.png>)。这是合成布局数据，不代表生产资源读数或业务集成验收。 |
| FLOW / 原生三页限定 | 隔离HWND、150% DPI，经UIA Invoke走服务→[主机](<docs/visual/sa-20261106/r107-compact/pages-r1/native/host.png>)→[异常日志](<docs/visual/sa-20261106/r107-compact/pages-r1/native/exceptions.png>)→[服务](<docs/visual/sa-20261106/r107-compact/pages-r1/native/services-returned.png>)，再往返异常页。ValuePattern输入“页面往返验证”，返回后读值仍在；服务筛选仍为“媒体”，原选中任务保留。已看3张PrintWindow实窗像素；手动探针 **1/1**，随后正常收起并回收，原始[探针信息](<docs/visual/sa-20261106/r107-compact/pages-r1/native/probe.txt>)保留。此旅程发生在指标排版小修复前，改后复验的是上述WPF关联集；未重跑原生旅程。未接真实Agent资源采样的页如实显示不可用/未读取，不代表生产数据。UIA多次使目标短暂前置，工具报告已恢复原前台；未操作浏览器。**非物理键鼠、完整焦点返回、多DPI或逐帧观感证明；总VIS/FLOW继续开放。** |
| 实现 / 工具退出基础与诊断试点 | 新增显式[ToolWindow](<src/SystemAutorun.App/Services/ToolWindow.cs>)，首个消费者是诊断窗；**日志、Self、Recovery及添加/远程/详情/消息现也接入（见后文），StartupManager尚未迁移**。XAML声明共享PresencePanel的Tool变体，沿160ms/2 DIP同一进度进入、退出与反转；终态用Hidden保留SizeToContent几何，不在最终guard前塌高。首轮和最终轮均执行同步Closing guard，重复请求合并，最终OnClosing内恢复暂存DialogResult，否决则反转并清除旧结果。窗口隐藏、关闭及代次校验阻止旧回调误关；减少动画直接放行或完成排队收尾。WindowTheme对新基类不再叠加旧FadeOnShow。主窗遮罩跟随待退出状态，新的DialogOwner也排除正在退出的工具。ClosureTask只表示实际Closed，不代表业务资源已回收。对应REU-4–6、MOT-1–5；迁移与总验收仍开放。 |
| ENG / 工具关闭边界 | [专用19例](<tests/SystemAutorun.IntegrationTests/ToolWindowMotionTests.cs>)覆盖真实ShowDialog的true/false/普通Close/IsCancel/WM_CLOSE、重复请求、初次与最终guard否决、旧true不泄漏到后续取消、反转、隐藏重开、owner强拆、中途减少动画、未显示即关闭及SizeToContent保持。模态断言在STA仍运行时完成，不借dispatcher teardown解帧。首版运行时重挂Content撞上WPF未完成的逻辑父节点接线，改为XAML显式宿主；同时修正测试的owner显示顺序、可见Loaded采样和旧Fade反转测试的近零吸附区采样。截图首轮因未预建证据目录失败，创建普通目录后重跑，不计失败轮为通过。最终上述页面/工具/相关业务GUI集合加新用例合计 **156/156**、0跳过、66秒；截图定向 **8/8**。不是全solution回归。 |
| VIS / 诊断窗与遮罩 | 双主题×主窗920×580、960×620、1320×820矩阵中，诊断窗保持800×480 DIP。已检查150% WPF离屏[最小深色](<docs/visual/sa-20261106/r107-compact/tools-r1/doctor-dark-920-open.png>)、[最小浅色](<docs/visual/sa-20261106/r107-compact/tools-r1/doctor-light-920-open.png>)、[常用深色](<docs/visual/sa-20261106/r107-compact/tools-r1/doctor-dark-960-open.png>)、[宽owner浅色](<docs/visual/sa-20261106/r107-compact/tools-r1/doctor-light-1320-open.png>)及[主窗遮罩](<docs/visual/sa-20261106/r107-compact/tools-r1/scrim-dark-960-open.png>)。检查发现诊断摘要保留了构造时的深色brush，切浅色后过淡；改为动态资源引用，并加入主题颜色断言后重新生成、查看截图。未改布局、字号或文案。诊断行是明确标注的合成样例，不代表系统诊断结果；逐帧观感和完整焦点矩阵未验。 |
| FLOW / 原生诊断窗关闭 | 隔离主窗与真实ShowDialog、150% DPI：[工具窗](<docs/visual/sa-20261106/r107-compact/tools-r1/native/doctor-open.png>)1200×720物理像素，小于1440×930的主窗。对核对过PID/HWND的测试工具发送系统SendKeys Esc，观察[主窗返回](<docs/visual/sa-20261106/r107-compact/tools-r1/native/main-returned.png>)；[探针](<docs/visual/sa-20261106/r107-compact/tools-r1/native/ready.txt>)与[返回断言](<docs/visual/sa-20261106/r107-compact/tools-r1/native/returned.txt>)确认模态正常返回false、实际Closed、遮罩清除且模型筛选仍为“工具返回保留”。已看两张PrintWindow像素，手动探针 **1/1**，43秒，完成后回收。键盘工具短暂借用目标前台并报告已恢复；未操作浏览器。**窗口由夹具直接打开，未跑诊断命令的数据链路；不证明完整入口/焦点返回、硬件键盘、Alt+F4或其他工具已验收。** |
| 实现 / 日志工具生命周期 | [日志窗](<src/SystemAutorun.App/Dialogs/LogViewerWindow.xaml.cs>)显式接入ToolWindow/PresencePanel；首次接受关闭同步发布pending ShutdownTask，反转/最终否决不完成该承诺，仅实际Closed启动幂等回收，owner强拆也有Closed兜底。清理用WhenAll等待模型、初始化与导出，finally释放窗口CTS；[模型](<src/SystemAutorun.App/ViewModels/LogViewerViewModel.cs>)也并行等待follow与reads，单项失败仍释放其CTS/信号量。文件选择器返回后复核Closed、退出状态和关闭代次，旧生命周期不得开始导出或读取已释放token。[主窗入口](<src/SystemAutorun.App/MainWindow.xaml.cs>)保留window/model引用，在finally等待ShutdownTask和模型Dispose；Hide结束模态帧时先真正Close，不提前销毁活视图。共享ToolWindow在退出期间拦截PreviewKeyDown，防止窗级F5绕过已禁用正文。保存路径、筛选、模态性、文案与权限保持。 |
| ENG / 日志关闭与错误回收 | 新增[18例](<tests/SystemAutorun.IntegrationTests/LogWindowMotionTests.cs>)，覆盖普通/减少动画、退出前不清理、Closed后仍等待导出、反转/最终否决/隐藏重显、文件选择器迟到返回与新代次重新导出、导出/follow故障后仍等待初始化/其他reads并释放资源、主窗正常关闭/Hide返回后继续等待、双主题×三档owner几何。首轮漏设IsPresented使正文隐藏、关闭立即完成，定向6例失败；补齐显式初始状态而不改断言，49/49复验通过。随后新增几何采图17/17，再补模型故障用例及扩大关联集，最终 **174/174**、0跳过、82秒。该集合为LogWindowMotion/ToolWindowMotion/PresenceMotion/MonitoringWindow/DisclosureMotion/DialogChrome/LogViewerGui/RecoveryGui/SelfStartupGui/DashboardMonitoring/DashboardReadModel，**不是全solution回归**。 |
| VIS / 日志双主题与窗口比例 | 已看150% WPF实装：[最小owner深色](<docs/visual/sa-20261106/r107-compact/logs-r1/logs-dark-owner-920.png>)、[最小浅色](<docs/visual/sa-20261106/r107-compact/logs-r1/logs-light-owner-920.png>)、[常用深色](<docs/visual/sa-20261106/r107-compact/logs-r1/logs-dark-owner-960.png>)和[宽owner浅色](<docs/visual/sa-20261106/r107-compact/logs-r1/logs-light-owner-1320.png>)。三档owner为920×580、960×620、1320×820；日志窗约800×487.2/500 DIP，标题/筛选/分页动作不裁切，至少保留一条完整事件行，原字号与布局不变，不叠加旧FadeOnShow，不加焦点框。截图内容是隔离源中的合成日志，通过真实文件/管道读取，非生产数据；不是逐帧观感或其他DPI证据。 |
| FLOW / 原生日志入口与返回焦点 | 150%隔离主窗经UIA Invoke服务日志→[缺失控制源](<docs/visual/sa-20261106/r107-compact/logs-r1/native/logs-source-missing.png>)→真实展开来源、选择business并刷新→[4条合成业务日志](<docs/visual/sa-20261106/r107-compact/logs-r1/native/logs-business.png>)→返回按钮→[主窗](<docs/visual/sa-20261106/r107-compact/logs-r1/native/main-returned.png>)。再打开两次，分别向重新核对的精确HWND发送系统SendKeys Esc、Alt+F4；三次返回均观察到ServiceLogsButton enabled且hasKeyboardFocus=true，搜索“媒体”及所选“媒体库”保留。本次通过UIA调用入口，返回焦点实际位于入口按钮，因此仅证明入口焦点恢复，**不是恢复到之前的SearchBox或完整Tab顺序**。已看3张PrintWindow像素；[观察记录](<docs/visual/sa-20261106/r107-compact/logs-r1/native/observations.json>)和[探针信息](<docs/visual/sa-20261106/r107-compact/logs-r1/native/ready.txt>)保留；probe **1/1**、6分59秒（含人工操作），随后正常收起并回收。仅前置独占测试窗，未操作浏览器/真实任务/启动项；不证明硬件键盘、多DPI、生产数据或整应用退出等待。 |
| 实现 / Self与Recovery生命周期 | [Self](<src/SystemAutorun.App/Dialogs/SelfStartupWindow.xaml.cs>)与[Recovery](<src/SystemAutorun.App/Dialogs/RecoveryWindow.xaml.cs>)显式使用ToolWindow/PresencePanel；首轮接受关闭同步发布pending ShutdownTask，反转/最终否决不兑现，实际Closed才停观察器、解绑owner并回收。WhenAll同时等待模型清理与InitializationTask，保留可等待的失败且显式观察异常；[Self模型](<src/SystemAutorun.App/ViewModels/SelfStartupViewModel.cs>)用finally释放CTS。Self管理器在重开、换owner及临时Hide/恢复前取消旧退出代次；只在Closed移除注册。保留未确认准备至真正关闭，不取消已确认workflow或改写未知pending。[主窗Recovery入口](<src/SystemAutorun.App/MainWindow.xaml.cs>)保留window/model，Hide解帧后先实际Close，再等待ShutdownTask并finally Dispose。未改布局、文案、默认确认、模态性、业务权限或原有焦点安全判断。 |
| ENG / 工作流窗口回归 | [新增34例](<tests/SystemAutorun.IntegrationTests/WorkflowWindowMotionTests.cs>)复用既有隔离夹具，覆盖Self准备/未知×普通、减少动画、反转、最终否决、换owner、暂藏；Recovery准备态关闭/反转/否决/隐藏重显/owner强拆；初始化失败仍等待挂起reads及CTS释放；主窗Recovery正常关闭和Hide后仍等待；双主题×三档owner几何。首轮编译因漏引入SelfStartupPendingStore命名空间失败，补using后定向 **93/93**、0跳过。加入两例主窗调用链后，前述11组关联测试及最终构建复验均 **208/208**、0跳过、1分40秒；**不是全solution回归**。既有已确认提交/owner强拆用例继续验证视图清理不等待或取消共享提交。 |
| DATA / 准备与未知保持 | Self使用合成CurrentUserOnly Pipe，无原生适配器；真正关闭前准备对象仍可确认，真正关闭后只丢弃自身准备，所有新关闭测试无startup-self-set；未知原ID及pending字节保持。Recovery使用私有配置与真实AgentServer/恢复store，准备取消后store字节不变、无operation journal；已有提交测试仍检查仅一次请求且WorkloadCalls=0。这不是生产服务、真实登录登记或Boot验收。 |
| VIS / 工作流工具比例 | 150% WPF实装的三档owner为920×580、960×620、1320×820；Self保留680 DIP宽、Recovery760 DIP宽，均小于owner，标题及关闭/确认/取消可达，无双重FadeOnShow和新增焦点框。已看[Self最小深色](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/self-dark-owner-920.png>)、[Self宽owner浅色](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/self-light-owner-1320.png>)、[Recovery最小浅色](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/recovery-light-owner-920.png>)及[Recovery常用深色](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/recovery-dark-owner-960.png>)；内容为隔离根及合成/夹具状态。其余矩阵有几何断言，不冒称每张像素均已人工检查；不是逐帧或其他DPI证据。 |
| FLOW / Self入口与Recovery原生关闭 | 150%原生Self：UIA设置→登录启动→[准备启用](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/self/prepared.png>)→取消→主机页入口→[重新打开为选择态](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/self/reopened.png>)→精确新HWND SendKeys Esc→[主机页返回](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/self/main-returned.png>)。两次窗体HWND不同，无旧准备残留；[最终记录](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/self/after.json>)只有两次status和一次prepare，State=Ready，无set，probe **1/1**、5分15秒。Recovery由夹具直接打开，UIA准备→[确认页](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/recovery/prepared.png>)→精确HWND SendKeys Alt+F4，等待Closed/ShutdownTask；[最终记录](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/recovery/after.json>)为Ready、空ID、Pending=Missing、Errors=[]，probe **1/1**、34秒。已看4张PrintWindow像素；[观察边界](<docs/visual/sa-20261106/r107-compact/workflow-tools-r1/native/observations.json>)明确：Self返回后入口hasKeyboardFocus=false，本次**不作为焦点恢复通过**；Recovery不是主窗入口原生旅程。键盘工具短暂借用目标前台并报告归还，未操作浏览器；完整Tab/焦点、物理输入与逐帧观感仍未验。 |
| 实现 / 表单与轻量工具 | [添加](<src/SystemAutorun.App/Dialogs/AddProgramWindow.xaml.cs>)、[远程](<src/SystemAutorun.App/Dialogs/RemoteHostsWindow.xaml.cs>)、[任务详情](<src/SystemAutorun.App/Dialogs/TaskDetailWindow.xaml.cs>)、[消息框](<src/SystemAutorun.App/Dialogs/MessageDialog.xaml.cs>)显式接入ToolWindow/PresencePanel。保持原宽度、字段顺序、文案、默认动作及模态结果；Add的_saving关闭保护不变，Remote仍允许在保存中关闭且不新增取消/回滚。Remote在已接受退出后不再应用迟到DialogResult或弹错误子窗，避免装饰延时复活旧关闭流程。无新增业务入口或权限。 |
| ENG / 表单与消息回归 | [67个新增用例](<tests/SystemAutorun.IntegrationTests/FormWindowMotionTests.cs>)（含[隔离旅程](<tests/SystemAutorun.IntegrationTests/FormWindowNativeTests.cs>)）覆盖4类×正常/减少动画/反转/最终否决/隐藏重显/owner强拆，消息确认/取消/系统关闭及旧true清除，Add保存保护、拒绝/异常后同窗重试，Remote迟到成功/失败不改取消结果，双主题×三档owner几何。首轮定向 **96/96**；矩阵与隔离旅程 **25/25**。扩大关联集首次 **279/280**：旧披露测试在Close请求后立即断言卸载，而新契约此时仍在退出；[修正为等待实际ClosureTask](<tests/SystemAutorun.IntegrationTests/DisclosureMotionTests.cs>)并保留卸载/时钟断言，夹具finally也明确等Closed。最终原11组加RemoteHostsWindowTests/StartupGuiWindowTests **280/280**、0跳过、2分22秒，**不是全solution回归**。 |
| VIS / 紧凑表单与裁切修复 | 已看150%实装[添加最小深色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-add-dark-owner-920.png>)、[添加宽owner浅色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-add-light-owner-1320.png>)、[详情常用深色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-task-dark-owner-960.png>)、[详情最小浅色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-task-light-owner-920.png>)、[消息常用深色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-message-dark-owner-960.png>)及[消息宽owner浅色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-message-light-owner-1320.png>)。宽度分别520/680/480 DIP，主要动作可达。检查发现远程“启用此服务器”被两个固定宽输入框挤掉，以及[底部说明被横向StackPanel无限测量截断](<docs/visual/sa-20261106/r107-compact/forms-r1/native/remote-hint-before.png>)；保持同排字段顺序，仅改弹性列及受限宽度图文宿主，补标签完整宽度、滚到底部完整文本边界断言。修复后复看[最小浅色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-remote-light-owner-920.png>)、[宽owner深色](<docs/visual/sa-20261106/r107-compact/forms-r1/form-remote-dark-owner-1320.png>)及[原生复验](<docs/visual/sa-20261106/r107-compact/forms-r1/native/remote-hint-after.png>)，提示完整换行、动作可达。几何矩阵24例，非每张像素都人工检查；无新增焦点框，不作为完整焦点/逐帧/其他DPI证据。 |
| FLOW / 原生失败保留与返回 | 150%独占夹具owner直接依次打开4工具，**不是生产主窗入口旅程**。UIA修改添加名称→添加→[合成失败消息](<docs/visual/sa-20261106/r107-compact/forms-r1/native/add-error.png>)→确定→[同HWND保留输入](<docs/visual/sa-20261106/r107-compact/forms-r1/native/add-retained.png>)→取消；远程改名→保存→[合成失败](<docs/visual/sa-20261106/r107-compact/forms-r1/native/remote-error.png>)→[草稿保留](<docs/visual/sa-20261106/r107-compact/forms-r1/native/remote-retained.png>)→滚到底→SendKeys Esc；[详情](<docs/visual/sa-20261106/r107-compact/forms-r1/native/task.png>)用Alt+F4返回；[确认框](<docs/visual/sa-20261106/r107-compact/forms-r1/native/message.png>)用取消返回。[记录](<docs/visual/sa-20261106/r107-compact/forms-r1/native/after.json>)确认4次false/Closed、两个拒绝回调各1次及名称保留，probe **1/1**、3分55秒。补齐说明换行后再次运行远程失败→返回→滚动100%→Esc，**1/1**、1分53秒，[最终记录](<docs/visual/sa-20261106/r107-compact/forms-r1/native/remote-r2/after.json>)仍false、Closed、仅1次拒绝回调。已看8张原生像素（含修复前失败样张）；[观察边界](<docs/visual/sa-20261106/r107-compact/forms-r1/native/observations.json>)保留，不证明完整Tab/主入口返回焦点、硬件输入或逐帧观感。 |
| DATA / 本轮边界 | 两种表单原生旅程仅注入合成拒绝回调，没有Agent/client、执行器或SSH传输；远程节点禁用且地址为文档示例网段。唯一临时known_hosts内容在关闭后复读字节不变，根内无新增文件，再由夹具回收；不把草稿列表更新冒称持久化成功。后台业务实现、注册表、真实启动项及Boot未操作。 |
| 实现 / 应用退出等待 | [ToolWindowLifetimes](<src/SystemAutorun.App/Services/ToolWindowLifetimes.cs>)作为App持有的视图登记表；[ToolWindow](<src/SystemAutorun.App/Services/ToolWindow.cs>)在SourceInitialized自动登记，仅构造未创建native source的窗口不被持有。稳定完成任务先等ClosureTask，再回窗口Dispatcher读取ResourceCleanupTask；日志/Self/Recovery映射原ShutdownTask。关闭/隐藏/反转/换owner不提前丢弃待清理条目，settled后移除；已移除故障按窗口类型保留次数及最后异常，不累积全部已关窗口。[App退出核心](<src/SystemAutorun.App/App.xaml.cs>)在首次await前封闭准入并capture，关owner后join，再清理Agent/single-instance，最后Shutdown。残余独立工具跳过装饰延时关闭，但不绕过同步guard；若guard拒绝则记录“cleanup incomplete”，不无限等一个未关闭窗口或冒称成功，原有最终退出故障策略保持。 |
| 实现 / 迟到开窗防护 | [主窗入口](<src/SystemAutorun.App/MainWindow.xaml.cs>)在退出时拒绝诊断/详情/日志/启动项/添加/远程新窗，远程异步准备返回后再次校验；[消息框](<src/SystemAutorun.App/Dialogs/MessageDialog.xaml.cs>)在退出时不弹迟到信息/错误，确认返回false而非同意；[StartupManager纳管表单入口](<src/SystemAutorun.App/Dialogs/StartupManagerWindow.xaml.cs>)同样拒绝迟到打开。Self临时挂起的owner在退出时不恢复显示，主窗外部激活亦被拒绝。未改表单保存回调、取消/回滚或confirmed workflow的所有权。登记只等待已创建的工具视图；远程准备的迟到回调被抑制，不因此宣称所有主窗异步准备请求都已join。 |
| ENG / 生命周期与准入 | 新增[16例生命周期测试](<tests/SystemAutorun.IntegrationTests/ToolWindowLifetimeTests.cs>)：Source前不登记、重复登记、已Closed仍pending、初始CompletedTask陷阱、owner跳过Closing、退出中/反转/隐藏/换owner、故障条目已退表仍有诊断、getter故障不跳过慢读、owner关闭故障/guard拒绝显式报错、拒绝迟到准入、消息确认不误同意，以及真实三资源窗只注入视图读且不初始化IPC。实际App.RequestExitAsync用独立登记表/最终关闭标记验证重复退出不重复动作、读取未回收时不执行最终动作；真实MainWindow远程准备暂停在settings闸门，退出后放行，本地隔离控制平面读取完成仍不弹窗，配置字节不变。首轮4个夹具失败，补空配置并改用同STA owner集合后与ToolWindowMotion **34/34**；扩大集首次缺测试Services命名空间已补。最终前280组＋本组及GuiConfigurationControl/纳管回调 **302/302**、0跳过、2分31秒，非全solution。 |
| VIS / FLOW / 退出核心水平原生验证 | 未改产品布局、尺寸、主题或增加退出界面；沿R107已批准的退出/清理边界修复。150%原生[真实日志窗](<docs/visual/sa-20261106/r107-compact/lifetime-r1/log.png>)明确标注合成读取、未连Agent；精确HWND SendKeys Alt+F4后[窗已关但读取未完](<docs/visual/sa-20261106/r107-compact/lifetime-r1/closed-pending.png>)→UIA发起实际App退出核心→[仍等待读取、最终动作未执行](<docs/visual/sa-20261106/r107-compact/lifetime-r1/waiting.png>)→UIA释放合成读取→最终动作恰好一次、登记数0。三张PrintWindow像素已看，probe **1/1**、42秒，[返回事实](<docs/visual/sa-20261106/r107-compact/lifetime-r1/after.json>)及[边界](<docs/visual/sa-20261106/r107-compact/lifetime-r1/observations.json>)已归档。后两张的按钮/提示是**测试夹具，不是新产品UI**；最终Dispatcher关闭替换为标记，未真的Application.Shutdown，登记表显式注入、生产Source接线仅源码核验，不冒称生产托盘退出、Agent回收或完整焦点验收。 |
| DATA / 退出范围 | 原生探针没有IPC或native adapter，真实日志模型仅持有一个不可取消的合成视图读；配置夹具只有tasks空数组，未新增业务数据。自动迟到远程测试仅使用独占根的真实named-pipe控制平面与NeverRemoteReader，配置字节未变。没有取消/等待Add/Remote保存或已确认Self/Recovery事务；本片没有操作真实服务、启动项、注册表或Boot。 |
| 后续 / 动效与长稳 | **待实施与验证**：StartupManager接入与仅视图读清理，余下真实入口/焦点旅程、生产自动登记/完整退出的隔离验证、10–20分钟有界浸泡及全量Debug回归。StartupManager仍同步Dispose取消CTS，且[已确认Adopt仍用视图token](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs#L159-L174>)；不得用笼统join所有命令或取消已确认事务冒充视图回收，需单独审计读/提交边界。当前退出join只覆盖已迁入的ToolWindow资源，尚不称全部窗口完成。前期短时采样不是多日长稳证据；未引入length/mtime命中跳读缓存。 |
| 清理 / 当前运行版本 | 本片测试均已结束。终检只见原有Release GUI PID27384与Agent PID30332，未重启/替换；无testhost/sautorun/额外Agent。十轮原生probe及对应fixture root已回收；退出切片3张截图及ready/after记录校验哈希后归档，精确工具临时副本和probe目录已删除，前9轮清理保持。最终302例后匹配的SystemAutorun临时目录仍为0；本片探针PID32920已退出，其fixture root不存在。上轮咨询已释放，本轮未新增子代理。git diff --check通过（仅既有换行提示；不代表未跟踪源文件的差异检查）；最终构建时间晚于本片全部生命周期产品源文件。当前仅构建Debug，生产窗口尚未加载，未提交版本。 |

## 2026-11-06 — 刷新不闪、浅色模式、紧凑与动画（R106）

沿[设计基线 R106 节](<docs/design-system.md#2026-11-06--刷新不闪浅色模式紧凑与动画r106>)实施；用户批准计划，浅色免生效果图。改动前原文在 [before-code](<docs/visual/sa-20261105/r106-polish/before-code/>)。本机 150% 缩放、会话 Active。**物理键鼠、真实系统主题切换、动画的逐帧观感仍待用户实测，不称整体 UI 完成。**

| 类别 / 规则 | 实际结果与证据边界 |
| --- | --- |
| FLOW / 刷新不闪 | [DashboardReadModelTests](<tests/SystemAutorun.IntegrationTests/DashboardReadModelTests.cs>) 断言同名刷新时 `Tasks` 无任何集合变更、行对象与选中项不变、行发出全属性变更；增删时只有一次 Remove + 一次 Add。历史在同服务同窗口刷新时不先置空（代码路径），由历史/仪表盘相关 799 项用例回归；未录制真实 10 秒刷新的视频。 |
| VIS / 合并与紧凑 | 已看[服务 24 小时](<docs/visual/sa-20261105/r106-polish/state/restart-uptime-main-24.png>)：标题行“可用率 50.0% · 已观测 10 分钟 0.7%”，CPU/内存上下排列；首轮像素发现两格并排时右侧大片空白，已改为上下。[远程服务器](<docs/visual/sa-20261105/r106-polish/state/dialog-remote-920x600.png>)编辑态只剩“保存”。 |
| VIS / 浅色 | 已看[主机 1060×660](<docs/visual/sa-20261105/r106-polish/state/light-host-1060x660.png>)及 920×580/1320×820/1620×651、[服务](<docs/visual/sa-20261105/r106-polish/state/light-services-1060x660.png>)。`LightPaletteRecolorsOpenWindowsLiveAndRestoresDark` 在已打开窗口上切换并断言背景/文字换色、文字对底 ≥4.5:1（主文字 ≥7:1）、严重标签白字对红底 ≥4.5:1、切回深色恢复原值、`Apply` 不写偏好文件。首轮发现资源画刷被样式封存冻结、原地改色无效，改为全部 DynamicResource + 应用级覆盖。浅色下各工具窗口、菜单、下拉弹层的像素未逐个看。 |
| VIS / 动画 | 底色 140 ms、前景 120 ms、页面/工具窗口 160 ms 淡入；`ReduceMotion` 或系统关闭动画时直达终态（所有截图用例均在减少动画下取帧）。动画观感为代码与参数证据，未逐帧取证。 |
| DATA | 外观偏好写每用户 `%LOCALAPPDATA%\SystemAutorun\appearance.json`；未改 `autorun.gui.settings.json`、配置修订、IPC 或 Agent 行为。远程服务器保存仍走原 `SaveDraftAsync`。 |
| ENG / 双配置 | 首轮 Debug 全量 3 项失败：主题事件跨 STA 线程访问控件、Application.Windows 跨线程，按钮对比度用例直接把 setter 值当画刷；已改为各控件在自身 Dispatcher 刷新、仅应用线程遍历窗口，用例按控件解析 DynamicResource。修复后 Debug 集成 2551/2551；Release 全量 Core 971/971、集成 2551/2551。 |
## 2026-11-05 — 同级视图、默认采集与紧凑窗口（R105）

沿[设计基线 R105 节](<docs/design-system.md#2026-11-05--同级视图默认采集与紧凑窗口r105>)实施。用户批准方向；G2 效果图只作紧凑度参考，视觉仍以 r4/R104 为准。改动前原文在 [before-code](<docs/visual/sa-20261105/r105-tabs-addtask/before-code/>)（监控窗口与其测试原未纳入 git，已逐字节核对备份后才删除）。本机显示缩放 150%、会话 Active。**真实配置下的人工点击与托盘退出旅程待用户实测，不称整体 UI 完成。**

| 类别 / 规则 | 实际结果与证据边界 |
| --- | --- |
| VIS / 顶部同级 tab — INT-3a、GRID | 已看像素：[主机 1120×700](<docs/visual/sa-20261105/r105-tabs-addtask/state/monitoring-populated-1120x700.png>)、[异常日志 960×600](<docs/visual/sa-20261105/r105-tabs-addtask/state/monitoring-exceptions-960x600.png>) 及 [state](<docs/visual/sa-20261105/r105-tabs-addtask/state/>) 下 960×600/1120×700/1320×820/1620×651 全套。选中 tab 只有底色与亮字，无条或外框。960 宽首轮发现异常日志的时间与级别列被硬切，已收紧固定列并给来源/文件加省略号，复查无截断。 |
| VIS / 新建任务 | [折叠 560×358](<docs/visual/sa-20261105/r105-tabs-addtask/state/dialog-add-basic-560x358.png>) 只有程序、名称、启动时机、常驻追踪；[展开](<docs/visual/sa-20261105/r105-tabs-addtask/state/dialog-add-advanced-560x720.png>) 至 720 上限后滚动。首轮像素发现“高级”标题字号偏大、比字段多缩进约 24 DIP，展开截图早于过渡结束；已对齐并改为等窗口实际长高后再断言和截图。 |
| VIS / 主次窗口 | 日志、启动项 1040×660（最小 900×560）、远程服务器 920×600，只留关闭、不进任务栏，标题只写功能名；自动恢复与登录启动正文 16→13。[远程](<docs/visual/sa-20261105/r105-tabs-addtask/state/dialog-remote-920x600.png>)已看像素；日志、启动项、自动恢复、登录启动的新尺寸像素未单独复看。 |
| FLOW / 程序化交互 | [MonitoringWindowTests](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>) 点击三个 tab 原地切换、不开新窗口；Tab 顺序为 服务→主机→异常日志→刷新→设置→最小化→最大化→收起到托盘→管理远程→主机行→系统诊断→登录启动，不经过隐藏的搜索框与服务列表；真实对话框打开时主窗遮罩出现、关闭后撤除。[SelfStartupGuiTests](<tests/SystemAutorun.IntegrationTests/SelfStartupGuiTests.cs>) 改为从主机 tab 再次打开仍是同一非模态工具和同一准备态。[DialogChromeTests](<tests/SystemAutorun.IntegrationTests/DialogChromeTests.cs>) 断言主/普通按钮模板无 MaterialDesign 遮罩，悬停与按下底色对文字均 ≥4.5:1。均为 WPF 逻辑焦点与事件路由，不是物理键鼠证据。 |
| DATA / 默认采集 | 采集范围改为启用或常驻的工作负载；[ManagedProcessReaderTests](<tests/SystemAutorun.IntegrationTests/ManagedProcessReaderTests.cs>) 断言未常驻但启用的任务被采样且保留 `ExpectedRunning=false`，禁用未常驻的不采。自动恢复资格 `Eligible` 未改，恢复相关用例全部通过；常驻异常计数只数常驻任务。配置文件格式与 IPC 契约不变。 |
| ENG / 双配置 | Debug 全量：Core 971/971、集成 2548/2548（删去随 `DashboardMinimumHeight` 移除的 5 个理论用例）。之后新增新建任务用例并修正列宽/展开对齐，Release 全量：Core 971/971、集成 2549/2549。运行前清空 `SA_*PROBE`/`SA_*EVIDENCE` 变量。 |

## 2026-11-05 — 全局界面审美统一（R104）

沿[设计基线 R104 节](<docs/design-system.md#2026-11-05--全局界面审美统一r104>)实施。用户批准计划，并指定以 r4 参考图为视觉基准、免生 G2 效果图；不改业务、IPC/CLI、数据结构、权限或已批准的确认/未知旅程。监控窗口改动前的原文保存在 [before-code](<docs/visual/sa-20261105/r104-ui-polish/before-code/MonitoringWindow.xaml>)（原文件未纳入 git，按 r82 before-code 与 patch 重建，719 行）。**物理键鼠、100%/125%/150% DPI 实机与真实主机数据下的监控页仍未验，不称整体 UI 完成。**

| 类别 / 规则 | 实际结果与证据边界 |
| --- | --- |
| VIS / 状态色 — INT-3/3a、COL | 已看像素：主屏[30 天反白选中](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/restart-uptime-main-720.png>)、[启动项选中行亮字](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/startup-selected-1120x700.png>)、[日志展开行](<docs/visual/sa-20261105/r104-ui-polish/full-debug/log/logs-1320x820.png>)。对比度按 sRGB 实算：焦点行底线 4.63:1、图标焦点 3.24:1、反白焦点约 3.9:1；已禁用服务静止 2.6:1 记为非活动项例外。self 工具“取消”在键盘焦点下为 `#8B8B94`，属未改动的 Foreground 角色旧规则。 |
| VIS / 监控窗口 — ALN、GRID | 已看 1040×650/1120×700/1320×820/1620×651 [概览](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/monitoring-overview-1320x820.png>)、[长文本头部](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/monitoring-header-1040x650-long.png>)、[异常日志](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/monitoring-exceptions-1320x820.png>)：2×2 读数格与下方两列竖线对齐，长身份截断不压摘要。夹具无主机数据，只覆盖 `--`/空列表态。补跑的合成有数据用例（16 核、2 卷、2 网卡）在逻辑树上断言 5 条用量条的指示宽度与读数一致（±1%）、两行网卡收发均可见；但该轮运行时 Windows 会话已断开/锁屏（`query user` 为 Disc），连无关红色方块也渲染为全透明，所得截图无效已删除，**有数据态像素仍未看**，待会话解锁后重跑 `PopulatedOverview` 用例取证。 |
| VIS / 对话框外壳 — REU、RAD | 已看[添加](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/dialog-add-720x720.png>)、[远程](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/dialog-remote-980x650.png>)、[诊断](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/dialog-doctor-800x480.png>)、[任务详情](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/dialog-task-680x480.png>)、[消息](<docs/visual/sa-20261105/r104-ui-polish/full-debug/state/dialog-message-480x151.png>)及各自最小尺寸：同一 50 DIP 标题栏、细线字形、常规字重、无内嵌底色。任务详情“启动模式”仍显示 record 原文，为既有数据格式问题，本轮未改。 |
| FLOW / 程序化交互 | [DialogChromeTests](<tests/SystemAutorun.IntegrationTests/DialogChromeTests.cs>) 从关闭按钮路由 `CloseWindowCommand` 实际关闭 5 个对话框；[MonitoringWindowTests](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>) 验证返回为 `IsCancel`、焦点后图标变亮、选中主机行获焦时字色不变且行底线变亮、窗口内无复选框。Tab 遍历实测发现标题栏用 DockPanel Right 停靠导致键盘顺序与视觉顺序相反（返回→关闭→远程→刷新），已改为三列 Grid，现为 返回→刷新→管理远程→关闭→主机行→系统诊断→登录启动，用例固定该顺序且不经过隐藏元素。均为 WPF 逻辑焦点与命令路由，不是物理键鼠或原生前台证据；修复后 Debug/Release 受影响 29 项通过。 |
| VIS / R104b 焦点提亮与 150% DPI | 用户确认后将文字动作焦点由变暗改为提亮（静止 `#8B8B94`→焦点 `#FAFAFA`，3.24:1），[ColorStateAssistTests](<tests/SystemAutorun.IntegrationTests/ColorStateAssistTests.cs>) 按亮度断言焦点更亮、≥3:1、静止对底≥4.5:1、禁用更暗。本轮会话为 Active 且显示缩放 150%，已看像素：[主屏](<docs/visual/sa-20261105/r104-ui-polish/final-release/state/uptime-main-24.png>)、[有数据监控页](<docs/visual/sa-20261105/r104-ui-polish/final-release/state/monitoring-populated-1320x820.png>)、[异常日志](<docs/visual/sa-20261105/r104-ui-polish/final-release/state/monitoring-exceptions-1320x820.png>)、[启动项详情](<docs/visual/sa-20261105/r104-ui-polish/final-release/state/startup-details-1620x651.png>)、[日志](<docs/visual/sa-20261105/r104-ui-polish/final-release/log/logs-1320x820.png>)、[self](<docs/visual/sa-20261105/r104-ui-polish/final-release/self/confirm-1320x820.png>)、[恢复](<docs/visual/sa-20261105/r104-ui-polish/final-release/recovery/confirm-1320x820.png>)、[远程主机](<docs/visual/sa-20261105/r104-ui-polish/final-release/state/dialog-remote-980x650.png>)。150% 下异常日志多行消息被行高切半，已改为单行省略，全文在详情与悬停提示。旧用例 `GuiLogDirectoryTests` 在 150% 下因窗口取整到整像素（651→651.33 DIP）精确相等失败，改为与 MonitoringWindowTests 相同的 1 设备像素容差。全量 Debug/Release 各 2551 项中仅此 1 项失败，修复后受影响 24 项双配置通过。有数据态网速仍为“等待速率样本”，有速率读数的像素未覆盖。 |
| DATA | 无持久化或契约变更。两个设置开关仅从监控窗口移除，主屏设置菜单的同一绑定未改。 |
| ENG / 双配置 | 全解决方案 [Debug](<docs/visual/sa-20261105/r104-ui-polish/full-debug/trx>) / [Release](<docs/visual/sa-20261105/r104-ui-polish/full-release/trx>)：Core 971/971、集成 2550/2550 通过。测试断言按新契约改为更严格的条件（选中亮于静止、禁用暗于静止、焦点不降低选中字色），没有删除断言。运行前清空 `SA_*PROBE` 变量；结束后无残留 SystemAutorun/testhost 进程，空的 `%TEMP%\SystemAutorun.Tests` 已删除，构建服务器已关闭。 |
## 2026-11-05 — 真实Agent进程重启历史回读与可用率边界修复（R103）

沿[G1-r1监控共享事实与SA06](<docs/architecture.md#L307-L317>)及[原验收项](<docs/architecture.md#L335-L346>)补R102指定的实际OS进程证据，并修复扩大回归查明的数学百分数越域。新设计G2为N/A：产品只改Core一处可用率计算，不改GUI、数据结构、权限、旅程或主题；按原批准行为的小修复路径收证。**真实进程接管与合成档案回读已验证，不等于真实服务历史、崩溃重启、完整SA06或总G3。** R101的对象栈重建仍保持`ProcessRestart=false`，不追改成新证据。

| 类别 / 规则 | 实际结果与证据边界 |
| --- | --- |
| FLOW / 真OS进程 — SA06、VER-5 | [新用例](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.History.cs>)复用[原OwnedAgent](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.cs#L252-L367>)：原文件仅加partial，`tasks: []`与启动前空任务断言、精确Process对象兜底及Temp归属校验原样。先释放seed writer，再启动实际Agent，shutdown RPC后等待OS exit=0/释放句柄，再从同物理root启动第二代。独立sautorun两端status均核验IPC身份；[原生现场](<docs/visual/sa-20261105/r103-process-history/final-process-evidence/process-restart.json>)PID 20088→28604、原生启动时间/映像路径与不同InstanceId、递增StartedAt共同证明进程替换，不只比较实例GUID。 |
| DATA / 归档对账 — CON-2、STA-1/3 | 六帧**合成归档任务**不在当前空任务配置内，由已关闭的Core writer生成；不是Agent现场服务观测。固定[24h query](<docs/visual/sa-20261105/r103-process-history/final-process-evidence/history-24.query.json>)及168/720h、120桶，IPC和Console独立验证健康20秒、故障20秒、未知分别86360/604760/2591960秒，可用率50%、末桶CPU/内存均值40；前119桶均无资源值。完整Console Data在[重启前](<docs/visual/sa-20261105/r103-process-history/final-process-evidence/before.history-24.console.ndjson>)/[重启后](<docs/visual/sa-20261105/r103-process-history/final-process-evidence/after.history-24.console.ndjson>)三窗逐字相同；IPC与CLI按类型规范化比较，保留等价JSON转义，不通过删字段或放宽数值断言消除差异。详[独立终检](<docs/visual/sa-20261105/r103-process-history/audit-after.json>)。 |
| DATA / 实际采样与持久化 — STA-3、VER-5 | 前后两个真实Agent各自至少一帧空任务观测实际落盘，属于不同真实实例；合成六帧仍完整。旧归档及第一代关闭的segment在第二代期间SHA256/长度/mtime不变，配置字节不变，两个shutdown后writer.lock均释放。**新进程会立即采样，`zeroNewSampling=false`**；只读history命令不等于整个正在运行的root零IO。新segment/epoch marker/停机回执合法变化，未错误冻结，不调用Journal.GetAsync来冒充零写诊断。 |
| 产品最小修复 — REU-3、STA-1/3 | 扩大回归发现全健康小数秒桶产生`100.00000000000001%`，GUI合法性检查将其当未知。[13tick回归](<tests/SystemAutorun.Core.Tests/MonitoringHistoryPercentBoundaryTests.cs>)在[旧实现确定性red](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-percent-red.trx>)；[Core仅一行Clamp](<src/SystemAutorun.Core/Monitoring/MonitoringHistoryProjection.cs#L114>)恢复数学百分数0–100域。known=0仍null，未知/健康秒数、区间及资源计算均不改；不在GUI放宽越域防御，也不把缺采补成健康。 |
| ENG / 恢复夹具修正 — ALN-14/19、VER-5 | [旧几何断言实败](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-recovery-geometry-before.trx>)读到owner1620×651、tool820×484、extent=viewport=357，不能仅从IsExpanded推断正文已排版或必溢出。[夹具](<tests/SystemAutorun.IntegrationTests/RecoveryGuiTests.cs#L208-L238>)仅在详情滚动段采用产品既有MinHeight=300；等待正文可见、有真实尺寸、变换后高度与ActualHeight相同并纳入extent，再核验真正滚到末端及footer bounds，收起完成后恢复原高度。早期加严仍被MaterialDesign缩放动画制造假阳性，Lead看像素后未采信，末版未禁用动画/改模板或加长3秒预算。[最终几何](<docs/visual/sa-20261105/r103-process-history/recovery-rendered-Debug/geometry.json>)三owner均正文高度/渲染高度128、extent497、viewport173、offset324；产品XAML和尺寸合同未动。 |
| VIS / FLOW限定 — VER-1/2/4/5 | 重跑原MainWindow语义切窗及精确独立Console对账；已查看[24h](<docs/visual/sa-20261105/r103-process-history/parity-after/restart-uptime-main-24.png>)/[7d](<docs/visual/sa-20261105/r103-process-history/parity-after/restart-uptime-main-168.png>)/[30d](<docs/visual/sa-20261105/r103-process-history/parity-after/restart-uptime-main-720.png>)1320×820、scale1离屏运行像素。另已查看恢复详情三owner [最小](<docs/visual/sa-20261105/r103-process-history/recovery-rendered-Debug/details-scrolled-1120x700.png>)/[常用](<docs/visual/sa-20261105/r103-process-history/recovery-rendered-Debug/details-scrolled-1320x820.png>)/[宽](<docs/visual/sa-20261105/r103-process-history/recovery-rendered-Debug/details-scrolled-1620x651.png>)及[Release宽](<docs/visual/sa-20261105/r103-process-history/recovery-rendered-Release/details-scrolled-1620x651.png>)正文和footer像素。正文可读、底部动作不裁掉；已验prepare/discard/expand/scroll/collapse的WPF语义路径。**不是前台截图、物理键鼠、原入口焦点返回或多DPI全验，不以这些图关闭完整UI。** |
| ENG / 双配置 — VER-6 | Core `Log/Configuration/Monitoring` [Debug](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-final-Core-Debug.trx>)/[Release](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-final-Core-Release.trx>)各129/129；Integration在R102选集增加完整AgentProcessContracts后，[Debug](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-final3-Integration-Debug.trx>)/[Release](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-final3-Integration-Release.trx>)各757/757，均0失败/跳过，每配置886。其后仅再加严Recovery测试的实际渲染高度断言，末版相关消费者[Debug](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-rendered-Debug.trx>)/[Release](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-rendered-Release.trx>)各11/11；另外[Core监控25/25](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-monitoring-green.trx>)与[四旅程4/4](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-journeys-green.trx>)均属上述选集，不累加。未跑全solution。人工probe/evidence变量每次先移除并断言null，仅定向授权目录启用输出。 |
| ENG / 原失败与静态意见 — VER-6 | 保留[xUnit2010编译失败](<docs/visual/sa-20261105/r103-process-history/focused.txt>)、[时间戳JSON转义差异](<docs/visual/sa-20261105/r103-process-history/focused-results/r103-corrected.trx>)、[初轮扩大755/757](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-Integration-Debug.trx>)和[夹具布局等待756/757](<docs/visual/sa-20261105/r103-process-history/regression-results/r103-final2-Integration-Debug.trx>)。后者是旧collapsed extent相加的终态判定不可靠，改为实际正文坐标/渲染高度及完成收起，不靠提高deadline或删断言。证据变量曾错用STATE名称导致恢复图未生成，未冒称有图；改正后单独收证。三次[只读探索/复核及Lead纠偏](<docs/visual/sa-20261105/r103-process-history/review.md>)均不当作执行通过证明，早期通过却正文未显的像素亦不冒充最终证据。 |
| 保留工作与资源 — VER-6 | [148个冻结输入](<docs/visual/sa-20261105/r103-process-history/before-sources.json>)中[145不变](<docs/visual/sa-20261105/r103-process-history/after-sources.json>)，仅Core计算、原进程测试partial声明和Recovery测试改变；[两份新测试](<docs/visual/sa-20261105/r103-process-history/new-test-sources.json>)独立新增。修改前字节快照与patch保留原dirty工作，未回退他人变更。[终检](<docs/visual/sa-20261105/r103-process-history/audit-after.json>)4个有记录root均已回收，当前SystemAutorun临时目录、相关进程、私有HKCU测试键/fixture服务候选0；仅fixture自身清理，未额外杀进程或删未知数据。真实Run/Boot、生产配置、系统时钟/时区与用户工作负载未动，未提交/发布。 |

**下一优先片：** 先从R101明确的Boot GUI/CLI不对等出发，只读核对旧runas Helper、既有包准入与共享workflow边界，集中形成Boot独立G1的精确方案和需要用户决定的权限/确认取舍；批准前不新增UAC或开放CLI写入。目录实际Shell/前台键鼠、uptime/日志/恢复剩余原生矩阵、远程有效VIS、native公开写workflow和离线hint呈现仍按原矩阵开放，不为取图打断用户。

## 2026-11-05 — GUI日志目录与独立CLI同源修复（R102）

恢复[G1-r1共享配置与目录查询](<docs/architecture.md#L267-L291>)及[G2日志r2保留目录能力](<docs/design-system.md#L315-L326>)，修复R101查明的自定义`log_dir`偏差。已重新查看[r4原图](<docs/visual/sa-20261031/dashboard-approved-r4.png>)；沿小修复路径，不新增界面、布局、token、provider、写权限或公共契约，不重开G1/G2设计。**目录解析实现与限定DATA/ENG已验证，完整目录UI及总G3仍开放。** R101业务矩阵对应行已校正，不保留“尚未修复”的过期结论。

| 类别 / 规则 | 实际结果与证据边界 |
| --- | --- |
| 产品最小修复 — REU-1/3、STA-1/3 | [OpenLogsAsync](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Logs.cs#L18-L44>)每次直接读取同root Agent的`configuration`，取已解析Settings.LogDirectory；与CLI `logs path`同源，不在GUI重写路径解析。读取预算3秒，非延迟保证；失败经现有错误事件/状态呈现，绝不回退旧root/logs、自动拉起Agent或提权。保留明确点击后创建所选目录与Shell打开的既有行为；只说“已请求打开”，不从Process返回值推断窗口一定可见。 |
| 协作/CAS — CON-2、REU-3 | 刻意不走AgentConfigurationClient.ReadAsync（会推进编辑revision），也不走RunBusyAsync（会flush设置并在失败时reload）。[主模型](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L23-L28>)仅增加现有风格的可选`Func<ProcessStartInfo, Process?>`注入；默认仍Process.Start，using仅释放句柄，不停止Explorer。已detached模型不执行目录打开；未把这个用例概括为完整销毁中竞态验收。 |
| DATA / 默认与自定义 — VER-5、STA-3 | [三种真实管道用例](<tests/SystemAutorun.IntegrationTests/GuiLogDirectoryTests.cs#L20-L75>)使用原ServerFixture/ConfigurationWorkspace及独立无新窗口sautorun进程。默认、含中文/空格的相对和绝对目录均对账；绝对目录仍限owned fixture内。先在同root外部编辑配置再读取，GUI及Console各读两轮新路径，配置全文件SHA未因查询改变；GUI创建正确目录，不创建无关root/logs，编辑cursor仍保留原revision而不是偷偷rebase。见[相对路径原始输出及revision](<docs/visual/sa-20261105/r102-log-directory/accepted-evidence/directory-relative.json>)、[总审计](<docs/visual/sa-20261105/r102-log-directory/audit-after.json>)。 |
| DATA / 失败与离线 — STA-1、INT-4/6 | 配置坏YAML、同名文件阻挡目录是真实fixture错误；Shell拒绝为注入Win32Exception，**不是实际ACL/Shell拒绝**。失败均无fallback，编辑cursor不前移，错误后command恢复可用；以修正配置后的新VM作显式重试，无自动重试。实际无Pipe的[offline](<docs/visual/sa-20261105/r102-log-directory/accepted-evidence/directory-offline.json>)在预算结束后仅报错，root仍只有原YAML、未创建目录，ping无Agent应答。NeverRun/NeverProbe/NeverRemote阻断工作负载/远程探针；不以测试接管真实日志或生产配置。 |
| FLOW / VIS限定 — VER-1/2/4/5、INT-7 | [真实MainWindow菜单绑定用例](<tests/SystemAutorun.IntegrationTests/GuiLogDirectoryTests.Window.cs>)实际MenuItem经AutomationPeer Invoke执行，Shell委托只捕获准确FileName及UseShellExecute/空参数/空verb，不启动Explorer。Popup刻意保持关闭，避免WPF将屏外Popup移回用户桌面。Lead已查看[1120×700](<docs/visual/sa-20261105/r102-log-directory/accepted-evidence/directory-main-1120.png>)/[1320×820](<docs/visual/sa-20261105/r102-log-directory/accepted-evidence/directory-main-1320.png>)/[1620×651](<docs/visual/sa-20261105/r102-log-directory/accepted-evidence/directory-main-1620.png>)真实WPF离屏像素，DpiScale=1：原结构/图标未改，筛选保留、底部“已请求”可读；空任务夹具不伪装有服务观测。**不是前台截图、物理键鼠、真实菜单开闭/焦点、错误模态或实际Shell旅程；这些UI验收及额外桌面缩放仍未验证，不以本片称UI完成。** |
| ENG / 定向与回归 — VER-6 | [最终定向11/11](<docs/visual/sa-20261105/r102-log-directory/focused-results/r102-accepted.trx>)；Core `Log/Configuration/Monitoring` [Debug](<docs/visual/sa-20261105/r102-log-directory/regression-results/r102-Core-Debug.trx>)/[Release](<docs/visual/sa-20261105/r102-log-directory/regression-results/r102-Core-Release.trx>)各128/128；Integration `Recovery/Cli/Monitoring/Dashboard/Log/GuiConfiguration/RemoteHost` [Debug](<docs/visual/sa-20261105/r102-log-directory/regression-results/r102-Integration-Debug.trx>)/[Release](<docs/visual/sa-20261105/r102-log-directory/regression-results/r102-Integration-Release.trx>)各751/751，均0失败/跳过，每配置879。11定向已含751内，不累加；未跑全solution。probe/evidence变量全部移除并断言null，仅定向受控启用独立证据目录。 |
| ENG / 保留失败 — VER-6 | 首次[缺using](<docs/visual/sa-20261105/r102-log-directory/focused.txt>)、第二次[xUnit2031](<docs/visual/sa-20261105/r102-log-directory/focused-corrected.txt>)均编译阶段未跑测试；修正引用/过滤断言，不抑制诊断。首个可运行轮[10/11](<docs/visual/sa-20261105/r102-log-directory/focused-results/r102-current.trx>)宽窗初始高度被XAML MinHeight=700约束；夹具等待既有宽度最低高度规则生效后再请求651，保留精确高度断言，未改产品尺寸规则或跳过窗口。旧输出不覆写，最终accepted-evidence另存。辅助审计首次foreach管道语法失败无动作，修正后才采信实际终检。 |
| 独立复核 / 范围与清理 — VER-6 | [两份只读调研与Lead复核](<docs/visual/sa-20261105/r102-log-directory/review.md>)无必须修复项，不将代理的静态检查说成它实际跑了测试。[前](<docs/visual/sa-20261105/r102-log-directory/before-sources.json>)/[后](<docs/visual/sa-20261105/r102-log-directory/after-sources.json>)145冻结输入中143不变，仅主VM和Logs partial改变，2份新测试独立新增；字节快照/精确patch保留原dirty工作。终检19个有记录的早期/最终root均已由fixture回收，当前SystemAutorun临时目录、相关进程、私有HKCU测试键及fixture服务前缀候选0；未额外杀进程/删未知数据，保留小量证据，diff-check通过。CLI/Core/Agent/XAML产品、真实Run/Boot、生产配置、系统时区/时钟和用户工作负载未动，未发布。 |

**R102时的下一优先片（R103已推进）：** 同root实际OS进程重启与合成归档精确回读现有受限证据，未将R101对象栈冷重建改称进程重启；真实工作负载/异常崩溃历史仍未验。目录实际Shell/前台键鼠、uptime和日志剩余原生矩阵、远程有效VIS以及R101表内受限写端等继续开放，后续只在获授权的窗口/资源范围执行，不为取图打断用户。

## 2026-11-05 — 全业务入口对照、监测宿主栈重建回读与Help纠偏（R101）

沿[G1-r1逐项parity要求](<docs/architecture.md#L259-L303>)及[SA06/08–10](<docs/architecture.md#L335-L346>)检查真实入口，不以Help、按钮存在或底层测试代替交付。两份只读调研经Lead抽查合并如下；这是当前代码的业务族清单，不是全项G3通过表。**整体目标仍未完成。** 本片产品只校正CLI帮助中的过期缺口描述，不改语法/JSON/RPC/权限或GUI；G2新设计为N/A，沿既有主窗进行限定DATA验证。

### 当前GUI→CLI业务覆盖与实际缺口

“接线”仅表示入口到相同业务契约可达；验收仍以各历史条目的明确范围为准。CLI路由基准为[实际Routes](<src/SystemAutorun.Cli/CliArguments.cs#L21-L59>)，不是批准目标命令表。

| 能力 / GUI实际入口 | CLI与共享路径 | 当前事实、未关闭范围 |
| --- | --- | --- |
| 服务清单、详情、刷新 / 主窗与状态详情 | `task list/show` → list/status；[详情handler](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L427-L440>) | 已接线；详情与主窗并非同一个显示面，完整原生旅程仍按SA09验。 |
| 服务启停/重启、全局补启动 / 主窗与托盘 | `task start/stop/restart/start-missing` → 同名RPC；[单项](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L406-L413>)/[全局](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L455-L464>) | 已接线；既有隔离原生验收不等于所有任务类型和生产工作负载均验完，不能照搬“全未运行”或“全通过”。 |
| 添加/移出工作负载 / AddProgram及移除确认 | `task add/remove` → task-add/task-remove；[添加](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L538-L545>) | 共用配置revision与校验；移出不停止进程、依赖拒绝保持。CLI完整JSON表单覆盖既有任务类型，GUI完整失败恢复/尺寸尚非全验。 |
| 启禁、常驻追踪 / 服务更多与右键 | `task enable/disable/track/untrack` → task-enable/task-monitor；[GUI命令](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L415-L425>) | 已接线；启用配置不等于立即执行，追踪不是另一个生命周期控制器。 |
| 配置查看/路径/外部编辑 | `config show [--raw]`、`config path`；[GUI外部编辑](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L594-L599>) | GUI打开磁盘编辑器；CLI可读完整文档。`config validate/apply`是CLI额外的共享CAS工具，不凭GUI缺少同构编辑窗推导新的GUI需求；非合作外部编辑的原子性限制保留。 |
| 补启动/自动恢复等共享偏好 | `settings get/set/patch` → ConfigurationWorkspace；[GUI设置提交](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L713-L727>) | 已接线，双端持旧revision仍拒绝覆盖；界面缓存/窗口偏好不是业务配置事实。 |
| 自动恢复读取、pending/原ID诊断 / 按需Recovery工具 | `recovery state` → recovery-state及R100限定本地读取；[GUI本地读取](<src/SystemAutorun.Core/Ipc/RecoveryWorkflow.cs#L41-L81>) | 部分对等：GUI按已选名称在连接前读hint，CLI仅成功Agent返回名称且不改变stdout/退出语义；离线、未返回名称的hint呈现仍未覆盖。CLI全局JSON可读记录，不把没有单项过滤参数本身当作新增业务的理由。 |
| 解除恢复暂停、查询原操作 / Recovery工具 | `recovery resume NAME --expected-revision REV --yes`、`operation show ID`；[CLI守卫](<src/SystemAutorun.Cli/CliApplication.Recovery.cs>) | 已接共享pending/原子预占与单次提交；不新增pause、初始化修复或自动清档。R94–96/R100是分项证据，非完整恢复G3。 |
| 登录启动状态/准备/确认/原ID查验 / self工具 | `startup self status/prepare/set`及`operation show`；[GUI明确确认](<src/SystemAutorun.App/ViewModels/SelfStartupViewModel.cs#L132-L167>) | GUI与CLI已接共享workflow/pending，不再把GUI迁移列为产品缺失；包/当前主体/root边界与逐次确认保留。仅GUID私有适配器等既有受限正向证据，测试仍不写真实Run。 |
| Boot状态、安装/卸载 / 设置菜单 | CLI仅`startup boot status`；[GUI旧入口](<src/SystemAutorun.App/MainWindow.xaml.cs#L213-L214>)→[runas Helper](<src/SystemAutorun.App/App.xaml.cs#L172-L198>) | **明确不对等**：GUI旧提权入口仍可达，CLI没有写命令/共享准入；“CLI Boot closed”不能写成整个GUI无写端。Boot另行细化，未执行此路径，不自动UAC或开新写入。 |
| Windows启动项清单、筛选与缓存 | `startup list --scope/--type/--state/--search/--limit`；[GUI读取](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs#L80-L109>) | 已接共享清单；缓存仅显示，configurationRevision不是全局原生CAS。 |
| 启动项元数据/双槽只读复核 | `startup inspect ID --file JSON --expected-revision REV [--native]`；[GUI复核入口](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs#L113-L145>) | 已接线；槽位present不等于认证历史备份，不授予写权限，未知/权限失败保持可见。 |
| 预填纳管、纳管原回执 / StartupManager | `startup adopt`、`startup operation inspect`；[GUI纳管](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs#L148-L178>) | 已接共享配置写，仅新增任务，不禁用原登记/启动目标；未知不重提，不能从adopt等价推断native写等价。 |
| 原生禁用/原备份恢复/独立重建 | CLI无路由；[GUI Disable/Restore的CanExecute恒false](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs#L181-L186>)，[旧桥返回false](<src/SystemAutorun.App/App.xaml.cs#L168-L170>) | **公开写旅程未接线**，无可提交rebuild入口。底层已有受限实现和部分已批合同，不泛化成“全部native设计未批准”；完整保护/共享接线未完前保持关闭，不以占位按钮/底层测试算交付。 |
| 远程主机配置增删改 / RemoteHosts管理 | `host list/show/add/update/upsert/remove`；[GUI全量提交](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L564-L590>) | 同共享配置事实，GUI整表settings-set与CLI逐项CRUD粒度不同；ACK后重读失败不回滚。有效原生VIS/完整FLOW仍欠，不用历史全黑图当通过。 |
| 本地主机/远程只读资源及刷新 / Hosts与Monitoring | `host status [ID]`、`monitor status/watch`；remote-status只接受已配置ID | 已接线，远程只观测不操纵生命周期；合成/loopback失败不能替代授权真实远端成功证据。 |
| 服务/控制日志分页、筛选、follow、导出 / LogViewer | `logs sources`、`logs --source/--task/--level/--since/--tail/--cursor/--from-start/--follow/--export`；[共享GUI创建](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Logs.cs#L9-L12>) | 已接共享日志，R97主窗→模态→独立Console对账成立；WSL journal/Event provider仍明确不可用。来源全键盘、长文滚动及时限/轮转等LG03/04剩余原生矩阵未关。 |
| 旧异常聚合 / Ctrl+4与异常窗 | `logs exceptions [--level/--search]` → exception-logs | R83有独立Console受限快照证据，保留旧异常聚合不以新LogViewer替代。 |
| 打开日志目录 / 主窗及托盘 | `logs path`从[共享设置](<src/SystemAutorun.Cli/CliApplication.cs#L181-L185>)返回；GUI[R102改为同configuration新快照](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Logs.cs#L18-L44>) | **R102已修复解析偏差**：默认/相对/绝对自定义目录及外部更新后再次读取与独立CLI一致，编辑CAS不前移。受限菜单/像素已验；未真实启动Explorer或跑前台键鼠，不标完整UI通过。 |
| 主机/进程或服务uptime、连续健康 / 主窗及监控工具 | `monitor status/watch [--task]` → monitoring-snapshot | 已接同一结果，真实开始身份/宿主时长/连续健康不混用；部分未知和被动监测已有证据，原生全类型/焦点与多DPI未全验。 |
| 24h/7d/30d历史、可用率/覆盖率与资源趋势 | `monitor history NAME --window/--buckets`或`--file QUERY` → monitoring-history；[GUI固定120桶](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.History.cs>) | R99/R101覆盖精确时间窗、冷store及双端映射；R103新增同root真实OS进程接管后的合成档案对账，并修复可用率浮点越域。不是精确SLA或全部缺采在线；真实工作负载/崩溃历史及完整原生矩阵仍开。 |
| 系统诊断 / 菜单、托盘、DoctorWindow | `doctor` → [同RPC](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L444-L451>) | 已接线；不以JSON连通替代全部诊断失败/权限场景及实窗旅程。 |
| 后台生命周期与协作回执 / GUI确保运行、退出 | `daemon status/start/stop`、`operation show`；[GUI退出不杀后台](<src/SystemAutorun.App/Services/AgentProcessManager.cs#L14-L17>) | start/status/stop及稳定ID有独立Console证据；CLI停后台是显式额外操作，不要求GUI退出也shutdown。operation查询经[Journal.GetAsync](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L230-L237>)，会取RootWriteLease/清孤立临时文件，**不标为零IO的纯诊断**。 |

纯显示/隐藏/托盘图标/窗口布局/搜索筛选焦点与页面导航不要求CLI模拟；背后刷新、业务筛选/读取、设置和生命周期仍按上表核验。CLI额外工具不自动构成GUI功能缺失。不得把安全封锁项、缺真实证据项和已查明的产品偏差混为一种pending。

### 本片实际验证与边界

| 类别 / 规则 | 结果及可复查证据 |
| --- | --- |
| 产品说明 — CON-2/REU-3 | 只改[Help尾段](<src/SystemAutorun.Cli/CliApplication.cs#L664-L666>)：删已过期的GUI self/shared logs未实现描述，保留native/Boot/provider/离线hint缺口及“接线不是完整验收”。新增[说明回归](<tests/SystemAutorun.IntegrationTests/CliCoverageHelpTests.cs>)；独立Console [Debug](<docs/visual/sa-20261105/r101-parity-restart/help-Debug.txt>)/[Release](<docs/visual/sa-20261105/r101-parity-restart/help-Release.txt>)实际`--help`均exit 0且stderr空。无新业务或写权限。 |
| DATA / 冷重开 — SA06、STA-1/3、VER-5 | 原[三时间窗用例](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.Parity.cs>)扩为continuous/reopened两种；[夹具](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.cs#L253-L306>)停止旧AgentServer、释放writer后新建server/dispatcher/source/monitor/Journal identity/history store，复用同一物理root及原配置。原source Calls=375，新source冷态Calls=0；[cold status](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-cold.status.console.ndjson>)显示新instance、tasks/processes/host未观测、runtime空、lastWrittenAt=null，不把旧缓存当当前健康。 |
| DATA / 磁盘历史与双端 — SA06、VER-5 | [同一显式query](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-stable.query.json>)的[关栈前](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-before.history.console.ndjson>)/[零新采样冷回](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-cold.history.console.ndjson>)完整Data逐字相同，4段历史SHA及配置字节不变，冷读取前后全root指纹不变。随后只采一次当前未知/进程不可用样本，Calls=3；真实MainWindow按原按钮WPF语义Invoke切24/168/720h，独立sautorun使用GUI精确From/To/identity，全部120桶、区间、四趋势及绑定读数一致。健康/故障/未知仍分别300/300/85800、300/600/603900、600/600/2590800秒，长缺采/空桶不补健康，无Journal回执。详[现场与数值终检](<docs/visual/sa-20261105/r101-parity-restart/audit-after.json>)。 |
| FLOW / 像素限定 — VER-1/5 | 已查看重开路径[24h](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-uptime-main-24.png>)/[7d](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-uptime-main-168.png>)/[30d](<docs/visual/sa-20261105/r101-parity-restart/live-evidence/restart-uptime-main-720.png>)真实WPF离屏渲染，1320×820 DIP/scale 1，未知资源与缺采说明可见；保留fixture选择及fix搜索。未改UI，**不是前台屏幕截图、SendInput键盘/鼠标或完整VIS/FLOW**。 |
| ENG / 失败保留 — VER-6 | [定向3/3](<docs/visual/sa-20261105/r101-parity-restart/focused-results/r101-focused.trx>)（2种历史路径+Help）。Core `FullyQualifiedName~Monitoring` [Debug](<docs/visual/sa-20261105/r101-parity-restart/regression-results/r101-Core-Debug.trx>)/[Release](<docs/visual/sa-20261105/r101-parity-restart/regression-results/r101-Core-Release.trx>)各24/24；Integration `Recovery\|Cli\|Monitoring\|Dashboard` [Debug](<docs/visual/sa-20261105/r101-parity-restart/regression-results/r101-Integration-Debug.trx>)/[Release](<docs/visual/sa-20261105/r101-parity-restart/regression-results/r101-Integration-Release.trx>)各665/665，均0失败/跳过，每配置689，定向已包含不累加。人工probe/evidence变量先清除并断言null；仅定向受控启用截图输出。首次新增tuple默认null引发CS8604，编译阶段未跑测试；[原输出](<docs/visual/sa-20261105/r101-parity-restart/focused-compile-failure.txt>)保留，改为显式未执行sentinel后通过，不删断言或放宽deadline。 |
| 复核、原工作保持与清理 — VER-6 | [独立只读复核](<docs/visual/sa-20261105/r101-parity-restart/review.md>)无必须修复项，不授予新批准。143个冻结输入中140不变，仅Help与两个测试文件改变；三份修改前字节快照和精确patch保留，新Help测试独立新增。终检确认连续/重开两条有记录的root均已由fixture删除，全部当前SystemAutorun临时目录、相关进程、私有fixture键/服务前缀候选0；未杀进程/删无归属数据，保留少量证据，diff-check通过。未改Core/Agent/GUI产品、系统时钟/时区，未动真实Run/Boot、用户工作负载或发布。 |

**R101时的下一步顺序（R102/R103已推进）：** 目录解析偏差已按共享配置事实修复，尚欠真实Shell/前台键鼠的UI闭环；未扩新界面或改写生产配置。R103已补实际OS进程接管后的合成归档精确回读，uptime原生键盘/受支持尺寸、日志剩余原生旅程、远程有效VIS、native公开写workflow/Boot细化、离线hint呈现仍按上表保持开放。R101此次`hostObjectStackRebuilt=true/processRestart=false`只证明对象栈冷重建，**不追改成R103的新OS进程证据，也不冒称真实工作负载历史、完整SA06或总G3**；已有原生进程重启来源证据亦不能概括为“全部未测”。

## 2026-11-05 — CLI recovery state 本地hint限定诊断（R100）

沿[保留既有CLI语法/JSON的批准范围](<docs/design-system.md#L536-L541>)与[stderr诊断约定](<docs/architecture.md#L294-L298>)补最小产品缺口。仅核验Agent成功返回的恢复记录名称并纯读共享pending store；不增加参数、JSON字段、RPC、离线目录扫描或GUI变化，G2/VIS为N/A。**离线/孤立hint及完整GUI/CLI parity、总目标G3仍未关闭。**

| 类别 / 规则 | 实际结果及边界 |
| --- | --- |
| 产品 / 兼容 — CON-2、REU-2/3 | [CLI分支](<src/SystemAutorun.Cli/CliApplication.cs#L165-L170>)用既有`AgentRecoveryClient.ReadState`验证root/revision、集合与记录骨架，只取合法名称，不用DTO覆盖原`response.Data`，未知字段保留；畸形/错root/replay成功响应返回protocol_mismatch，不读取其名称对应的本地hint。[诊断函数](<src/SystemAutorun.Cli/CliApplication.Recovery.cs#L10-L33>)仅写stderr（含`--json`），本地hint状态不改变有效Agent响应的退出码或信封OperationId。 |
| 本地状态 / 原ID — STA-1/3 | Present展示原ID、预占UTC时间、原revision和rootId，声明非准入/当前状态证明，只建议同root查询原回执；当前状态未暂停也不清pending。Unavailable报告原因但不推断或泄漏无效记录内ID；Missing不添噪声。逐名称纯读不reserve/清档、不读取Journal、不提交/重放，不新增RootWriteLease；后端既有恢复读取协调不因此宣称零IO。 |
| DATA / 失败覆盖 — VER-5 | [19个新增用例](<tests/SystemAutorun.IntegrationTests/RecoveryCliPendingTests.State.cs>)覆盖text/JSON、坏档/目录/超界/错root/主体/名称、missing/未返回名称/未初始化、错误或离线Agent及不合法wire；检查单行JSON、完整Data等值、原ID未进入信封和全root文件指纹。既有[GUI-originated unknown→独立Console](<tests/SystemAutorun.IntegrationTests/RecoveryCliPendingTests.cs#L75-L98>)另加stderr原ID及null信封ID断言；真实workflow/pending/Journal宿主采用test framing，不冒称该旅程使用生产AgentServer或实际运行窗口。 |
| DATA / 独立Console — VER-5 | 新用例连接生产AgentServer/Dispatcher及真实RecoveryControlStore，配置无工作负载、NeverRunExecutor兜底；显式种入一个未准入的local hint，独立Console仍读取Agent真实暂停记录。保留[Console JSON正文](<docs/visual/sa-20261105/r100-recovery-state/console-evidence/console-state.ndjson>)、[stderr](<docs/visual/sa-20261105/r100-recovery-state/console-evidence/console-state.stderr.txt>)及[现场事实](<docs/visual/sa-20261105/r100-recovery-state/console-evidence/live-state.json>)；原ID只在stderr，信封ID为null，Journal回执0、orphan sentinel未清理，全root指纹不变。不是操作已获准入/已完成的证明。 |
| ENG / 失败保留 — VER-6 | 首轮[定向77/77](<docs/visual/sa-20261105/r100-recovery-state/focused-results/r100-focused.trx>)；最终`FullyQualifiedName~Recovery\|FullyQualifiedName~Cli`的Core [Debug](<docs/visual/sa-20261105/r100-recovery-state/regression-results/r100-Core-Debug.trx>)/[Release](<docs/visual/sa-20261105/r100-recovery-state/regression-results/r100-Core-Release-corrected.trx>)各365/365，Integration [Debug](<docs/visual/sa-20261105/r100-recovery-state/regression-results/r100-Integration-Debug-corrected.trx>)/[Release](<docs/visual/sa-20261105/r100-recovery-state/regression-results/r100-Integration-Release-corrected.trx>)各614/614，均0失败/跳过；每配置合计979，含62 Recovery CLI（19新增），77定向已包含其中不重复累加。清除手动probe/evidence变量并断言null；未跑全solution/原生GUI验收。初次批量runner误拼Integration项目路径，MSB1009且该轮未运行Integration；[原失败输出](<docs/visual/sa-20261105/r100-recovery-state/Integration-Debug.txt>)保留，核验项目实体路径后重跑成功，未跳过测试。 |
| 复核 / 未提交工作保持 | 独立只读复核未发现必须修复项，[摘要](<docs/visual/sa-20261105/r100-recovery-state/review.md>)明确它不授予新批准或证明整体G3。141个冻结输入中138不变、3个CLI/测试源在本片范围内变化，另1个路由fixture有单独的修改前字节快照；[源码审计](<docs/visual/sa-20261105/r100-recovery-state/source-audit.json>)及4份no-index差异保留既有untracked内容，原断言未删，夹具仅补合法typed状态/root。新增partial测试独立，不改Core/Agent/GUI产品。 |
| 清理 — VER-6 | [终检](<docs/visual/sa-20261105/r100-recovery-state/audit-after.json>)核实有记录的Console root已删除，当前TEMP下SystemAutorun-*目录、相关进程、私有fixture注册表键/服务前缀候选均0；未杀进程或删除非fixture数据。首次广域before枚举为空，pipeline未创建baseline文件，终检直接观察全部当前匹配目录，不伪造前后基线。保留TRX/字节快照和少量Console证据；`git diff --check`通过。未触碰真实Run/Boot、用户工作负载或发布。 |

**明确未验/未实现：** 这是此次Agent返回名称的逐项本地观察，不是全root或原子快照；没有提示不证明所有hint均Missing。Agent离线/损坏/未初始化时、未返回名称的孤立hint不回退为本地成功或扫描清档，其完整诊断呈现仍待确定。下一片优先整理全GUI业务→CLI逐项覆盖/验收差距，处理已有uptime重启回读与原生尺寸/键盘、日志剩余旅程；不得为追求parity擅开startup disable/restore/Boot写入，也不以本片979项工程通过替代这些功能与G3。

## 2026-11-05 — 主窗与独立Console精确历史窗口对账（R99）

本片沿[已批uptime/history语义](<docs/architecture.md#L305-L317>)及[r4主屏合同](<docs/architecture.md#L459-L474>)补限定DATA旅程。复用既有真实MonitorFixture，新增[一个跨端用例](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.Parity.cs>)，仅扩展[测试夹具](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.cs>)的可控健康、进程不可用和时钟参数；不改产品、CLI语法、RPC或布局。**完整监控及总目标G3仍开放。**

| 类别 / 规则 | 实际结果及边界 |
| --- | --- |
| 来源与隔离 — REU-2/3、CON-2、STA-1 | 合成健康/故障/未知观测及缺采，真实MonitoringHistoryStore落盘、AgentMonitor、AgentServer/Dispatcher及独立Console；NeverExecute/NeverProbe阻断真实工作负载，远程reader直接拒绝。先用受控时钟种入8天前、2天前及近1小时的稀疏样本，再仅将夹具TimeProvider切为系统UTC读取，避免GUI真实查询落在冻结时钟的“未来”；未改系统时间或时区。 |
| DATA / 同窗跨端 — STA-2/3、VER-5 | 实际MainWindow使用默认DashboardMonitoringClient连接同Root管道，以真实范围按钮的WPF UIA Invoke切24/168/720小时。用GUI的精确TaskIdentity、From、To、120桶生成既有`monitor history --file <query> --json`输入，分别拉起独立sautorun进程；不是各自计算“现在”，也不是进程内CLI替身。比较全部健康区间和120个可用率/覆盖率/CPU/内存点的时间及可空值，并检查实际TextBlock与DashboardPlot绑定。每窗原始query、Console和GUI数据保存在[本片证据](<docs/visual/sa-20261105/r99-uptime-parity/current-evidence/uptime-24.query.json>)同目录。 |
| 分母、缺采与未知 — STA-1/3 | [数值审计](<docs/visual/sa-20261105/r99-uptime-parity/audit.json>)核对24h健康300s/故障300s/未知85800s，7d为300/600/603900s，30d为600/600/2590800s；读数分别50.0%/0.7%、33.3%/0.1%、50.0%/0.0%。30d覆盖率原值约0.046296%，0.0%仅为既有一位小数舍入，原始非零值完整保留；不冒充无样本或100%健康。未知通过区间空缺与空桶表达，不要求服务端返回不存在的显式Unknown区间；跨度至少60s的空缺及尾部逾30分钟缺采均未填健康。 |
| uptime与被动读取 — STA-1/3 | 额外独立`monitor status --json`的[原始结果](<docs/visual/sa-20261105/r99-uptime-parity/current-evidence/uptime-status.console.ndjson>)确认宿主uptime为3小时、当前进程源不可用、任务HealthObserved=false，processStartedAt及continuous-health起点均null；GUI服务uptime/CPU/工作集为`--`，未借用宿主时长或缓存进程。全部GUI/Console读取前后采样源Calls固定375；各独立Console读取前后隔离Root全文件SHA不变。未触发补采样，不把GUI初始化的正常缓存过程也宣称为全程无写入。 |
| FLOW / VIS限定证据 — INT-7/8、ALN-4、VER-1/5/6 | 已查看实际WPF[24h](<docs/visual/sa-20261105/r99-uptime-parity/current-evidence/uptime-main-24.png>)、[7d](<docs/visual/sa-20261105/r99-uptime-parity/current-evidence/uptime-main-168.png>)、[30d](<docs/visual/sa-20261105/r99-uptime-parity/current-evidence/uptime-main-720.png>)RenderTargetBitmap像素，记录窗口1320×820 DIP、DpiScale=1：未知斜线、真实稀疏故障、当前未知资源与不可用说明可读，使用原r4矩形/直线控件，无新增装饰或操作外框。范围切换后fixture选择与fix筛选保留。**这是离屏真实WPF渲染及UIA语义集成，不是前台屏幕截图、原生鼠标/键盘旅程、焦点回归或完整VIS/FLOW矩阵。** |
| ENG / 失败保留 — VER-6 | [当前定向用例](<docs/visual/sa-20261105/r99-uptime-parity/focused-results/r99-current.trx>)1/1；相关Core monitoring [Debug](<docs/visual/sa-20261105/r99-uptime-parity/regression-results/r99-core-Debug.trx>)/[Release](<docs/visual/sa-20261105/r99-uptime-parity/regression-results/r99-core-Release.trx>)各24/24，相关Integration [Debug](<docs/visual/sa-20261105/r99-uptime-parity/regression-results/r99-integration-Debug.trx>)/[Release](<docs/visual/sa-20261105/r99-uptime-parity/regression-results/r99-integration-Release.trx>)各130/130，全部0失败/跳过；每配置合计154，定向1例已包含在130内，不重复计数。清除人工probe/evidence变量并断言null；新增实际窗体的partial测试类加入既有非并行WpfWindowCollection。首次编译的缺using及nullable错误保留在[原输出](<docs/visual/sa-20261105/r99-uptime-parity/focused-test.txt>)，修正后重建通过，未跳过断言或延长deadline。 |
| 清理 / 范围保持 | [前](<docs/visual/sa-20261105/r99-uptime-parity/before-sources.json>)/[后](<docs/visual/sa-20261105/r99-uptime-parity/after-sources.json>)核对140个既有冻结输入中139不变，仅测试夹具变化，[精确diff](<docs/visual/sa-20261105/r99-uptime-parity/fixture.patch>)保留原有未提交内容；新用例单独新增。两个有记录的临时Root均已由fixture安全删除，[五类监控夹具核验](<docs/visual/sa-20261105/r99-uptime-parity/fixture-cleanup.json>)在本片时段均无新残留目录，相关testhost/Console进程0；无空结果目录待回收。首次进程审计误匹配自身pwsh及DSH runner，经只读查明后仅排除精确观察者PID，未杀任何进程；处置载于审计。未写真实Run/Boot或注册表，未操纵用户服务或发布。 |

原生监控键盘/焦点旅程、同Root重启后的GUI/独立CLI历史回读、其他支持尺寸/DPI的完整监控验收仍未关闭。CLI `recovery state` 本地pending只读提示仍是已批范围内的明确功能缺口，下一片优先补该业务缺口与全GUI/CLI功能对照，不继续扩造监控探针。

## 2026-11-05 — 主屏服务日志、独立Console同页对账与原生返回（R97）

本片沿[已批日志r2及精确补充](<docs/design-system.md#L278-L326>)补LG01/LG02/LG05/LG06/LG07的限定旅程，已重新查看r2原图。复用现有[主屏入口探针](<tests/SystemAutorun.IntegrationTests/LogViewerGuiTests.cs#L185-L250>)，不改产品、测试代码或15分钟deadline；**完整日志及总目标G3仍开放**。

| 类别 / 规则 | 实际结果及边界 |
| --- | --- |
| 基线与数据 — CON-2、STA-1 | [132继承输入](<docs/visual/sa-20261105/r97-log-main-cli/sources.json>)SHA全部不变；[握手](<docs/visual/sa-20261105/r97-log-main-cli/ready.txt>)为自有PID8664、私有controller root、150% DPI。真实AgentServer/Dispatcher与[真实合成日志文件](<docs/visual/sa-20261105/r97-log-main-cli/fixture-business.log>)，主屏UNKNOWN投影，未启动monitor/真实工作负载、executor为NeverRun夹具，不冒称真实服务运行或uptime数据。原生窗口1320×820 DIP；未改系统DPI。 |
| FLOW — LG01/02、INT-4/7/8 | 从实际MainWindow的ServiceLogsButton UIA Invoke进入既有模态handler，媒体库服务上下文自动带入；首选control文件缺失显示[明确source_missing](<docs/visual/sa-20261105/r97-log-main-cli/initial-unavailable.png>)，不冒充空页。观察后真实SendInput点击business弹出项、Recent读取[4事件](<docs/visual/sa-20261105/r97-log-main-cli/business-four-events.png>)；真实点击error行[展开完整多行异常](<docs/visual/sa-20261105/r97-log-main-cli/business-expanded-error.png>)。显式Refresh仍保留business及同4条summary；未重建来源或执行服务。 |
| DATA / 跨端 — LG01/06 | 实际GUI Export按钮与原生SaveFileDialog将当前页写入[新owned文件](<docs/visual/sa-20261105/r97-log-main-cli/gui-page.ndjson>)；未覆盖已有文件。独立sautorun.exe执行`logs --source business --task 媒体库 --tail 100 --root <同一隔离root> --json`，得到[Console页](<docs/visual/sa-20261105/r97-log-main-cli/console-page.ndjson>)。[逐字段对账](<docs/visual/sa-20261105/r97-log-main-cli/live-parity.json>)核对rootId、来源/路径/任务、4条完整原文、fileIdentity/offset/时间/level/截断/损失标记、availability/scan信息及nextCursor一致；独立requestId/observedAt不强求相等。不是从种入文本推测GUI数据，也不是进程内CliApplication替身。 |
| 原生返回 — LG05/07、INT-7/8/9 | 第一次前台归属闸后的native Escape正常关闭日志，主窗恢复到选中行。第二次先程序聚焦真实ServiceLogsButton并核验实际前台/HasKeyboardFocus，再单次native Enter进入；[fresh native观察](<docs/visual/sa-20261105/r97-log-main-cli/native-second-entry.json>)证明owned日志窗67964730、owner的Win32 IsWindowEnabled=false。一次native Escape返回后，后续[fresh UIA/native观察](<docs/visual/sa-20261105/r97-log-main-cli/native-second-return.json>)及[实际像素](<docs/visual/sa-20261105/r97-log-main-cli/trigger-native-return.png>)证明前台11341466、owner重新enabled、精确ServiceLogsButton键盘焦点、媒体库选择/媒体筛选保留。没有把UIA Invoke算物理输入；也不把后续采样成功说成2.5秒内已恢复。 |
| VIS — ALN-4/14/17、INT-2/3/3a、VER-1 | 已实际查看同窗口的不可用、4事件、error展开及返回像素，与r2＋精确补充比较。沿按需单来源工具、真实来源ID/类型、当前页计数、无虚构总页码；过滤与列头可读，三行原文在现有行内展开，footer可达；点击选中及恢复焦点未出现操作外框/状态条，永久结构线不算新增outline。只验1320×820 DIP/150%与该短多行内容，不推定长值内部滚动、所有焦点色差、其他窗口/DPI或完整VIS通过。 |
| 失败/未证必须保留 — VER-5/6 | [处置记录](<docs/visual/sa-20261105/r97-log-main-cli/observation-disposition.json>)保留后台PostMessage未切换、未确认前台的Down/F5，以及前台/SourceBox焦点已核实的闭合选择器Down/F5仍未切换；不算完整来源键盘通过，也未确诊产品根因。第二入口采样误将UIA IsEnabled当native owner状态而报错；fresh观察已见原日志窗，**没有重放Enter**。首轮精确trigger采样2.5秒内未观测，后续fresh观察成功，不宣称延迟/缓存根因或时限通过；另保留一次SelectionPattern方法误用与后续只读API修正。8张截图尝试均归档，不删失败画面抹红。 |
| ENG / 生命周期 | [现有人工宿主](<docs/visual/sa-20261105/r97-log-main-cli/native-results/r97-native.trx>)1/1，在12分36秒内通过native Alt+F4正常隐藏主窗并结束，非超时/finally强销毁；测试末尾确认Agent未被窗口停止。清除全部实际日志probe/evidence进程变量并断言null后，`LogViewerGuiTests\|LogControlPlaneTests\|CliExceptionLogTests`的[Debug](<docs/visual/sa-20261105/r97-log-main-cli/regression-results/r97-Debug.trx>)/[Release](<docs/visual/sa-20261105/r97-log-main-cli/regression-results/r97-Release.trx>)各83/83、0失败/跳过。这是原代码定向复验，不是全solution或完整手工矩阵。 |
| 清理与下一片 | [归档核验](<docs/visual/sa-20261105/r97-log-main-cli/archive-audit.json>)确认真实controller root、probe、host退出，8张截图同SHA归档后精确删除临时原件；[终检](<docs/visual/sa-20261105/r97-log-main-cli/audit-after.json>)相关进程/私有测试键/fixture service/临候选0、diff-check=0，只额外回收普通空测试父目录，保留其他dirty工作。未读写clipboard、碰真实Run/Boot或发布。完整来源键盘/时限、LG03/04等剩余原生矩阵、CLI只读pending提示、uptime与全GUI功能对照仍开放；下一片优先uptime跨端数据主干/完整功能对照，不无边界追加本探针测试。 |

## 2026-11-05 — 恢复工具原生正反Tab、安全取消与正常退出（R96）

本片复用[已批键盘/返回合同](<docs/design-system.md#L531-L541>)，只补真实运行证据，不改产品、观察器、测试代码或10分钟deadline。R93的[原宿主失败](<docs/visual/sa-20261105/r93-recovery-native/focused-results/r93-native.trx>)继续保留；本次正常结束补齐部分未验项，**不将恢复工具或整体G3宣布完成**。

| 类别 / 规则 | 实际结果及边界 |
| --- | --- |
| 基线 / 夹具 — CON-2、STA-1 | [132源冻结](<docs/visual/sa-20261105/r96-recovery-native-keyboard/sources.json>)全部SHA与R95一致；复核r1原图及精确补充。沿既有真实AgentServer＋UNKNOWN主屏投影，未启动监测或工作负载。[握手](<docs/visual/sa-20261105/r96-recovery-native-keyboard/ready.txt>)绑定自有PID7852、主窗10489752、tool7277944、私有root，owner1320×820 DIP、实际150% DPI，工具820 DIP宽。不冒称真实生产监测数据或全部桌面矩阵。 |
| 原生循环 — INT-1/8/9、VER-4/5 | 更多菜单与恢复项以UIA Invoke进入实际Main handler（不算物理指针）；一次普通native ActivateWindow成功，无前台锁绕过。输入分两种：MCP SendKeys与有前台归属检查的WScript.Shell.SendKeys；后者不激活窗口，前台改变即停止，不使用WPF RaiseEvent。完整[焦点/按键序列](<docs/visual/sa-20261105/r96-recovery-native-keyboard/native-sequence.json>)证实Ready：Refresh→Prepare→Cancel→ChromeClose→ExpanderButton→Refresh，Shift+Tab反向完整返回；确认态：Cancel→Confirm→ChromeClose→ExpanderButton→Cancel，反向同样返回Cancel。记录中的key事件均有本窗口HWND=GetForegroundWindow；修饰键/路由记录不当作独立用户动作计数。 |
| 安全取消与返回 — INT-7/8/9 | 原生Tab到Prepare、Enter只准备，确认态焦点默认Cancel且无隐式提交。确认态正反循环后，原生Enter落在Cancel；[正常返回快照](<docs/visual/sa-20261105/r96-recovery-native-keyboard/native-cancel-return.json>)显示主窗native enabled/真实前台10489752、ServiceMoreButton键盘焦点，media、med和Workloads保留，现场Journal0。随后主窗原生Alt+F4触发正常隐藏，宿主在5分14秒正常完成，不靠deadline/finally超时关闭。 |
| VIS — INT-2/3、ALN-4、VER-1 | 已实际查看[Ready读取焦点](<docs/visual/sa-20261105/r96-recovery-native-keyboard/ready-refresh-focus.png>)及[确认取消焦点](<docs/visual/sa-20261105/r96-recovery-native-keyboard/confirmation-cancel-focus.png>)的同HWND运行像素：明确功能标题、服务和长root换行仍可读，同壳footer可达；Refresh/Cancel呈已有颜色焦点，没有新增外框，primary保留永久结构边框。与r1＋精确补充对照，不恢复生成图装饰标题、Play或遗漏身份。只验这两个150%端点，不推定Expander等全部焦点颜色、其他尺寸/DPI、未知态或完整VIS通过。 |
| DATA / ENG | [finally状态](<docs/visual/sa-20261105/r96-recovery-native-keyboard/after.json>)为Ready、ID null、pending Missing、Journal0、Errors空、1工具；本片没有明确提交或复制。[原生宿主TRX](<docs/visual/sa-20261105/r96-recovery-native-keyboard/native-results/r96-native.trx>)3/3＝1个手工原生case＋2个既有自动入口case，不能说三条都是物理旅程。清除实际probe变量并断言null后，[Debug](<docs/visual/sa-20261105/r96-recovery-native-keyboard/regression-results/r96-Debug.trx>)/[Release](<docs/visual/sa-20261105/r96-recovery-native-keyboard/regression-results/r96-Release.trx>)各19/19、0失败/跳过；这是Recovery GUI定向复验，非R95各594或全solution重跑。 |
| 清理 | [终检](<docs/visual/sa-20261105/r96-recovery-native-keyboard/audit-after.json>)核验132输入无漂移、相关进程/私有测试键/fixture service/临候选0、diff-check=0。真实controller root与probe已消失；3张owned临时截图同SHA归档后精确删除，只额外删除普通且已空的测试父目录。未读写clipboard、改系统DPI、碰真实Run/Boot，保留其他dirty工作。 |
| 未验 / 下一功能片 — VER-6 | 键盘明确提交、未知/已确认后的原生生命周期、Copy往返/失败、100%/125%及其余VIS/FLOW矩阵仍开放。抽查[实际日志入口](<src/SystemAutorun.App/MainWindow.xaml.cs#L62-L67>)、[同root VM工厂](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.Logs.cs#L7-L12>)及[LG01–07](<docs/architecture.md#L3034-L3049>)后，下一片优先“服务→真实日志→返回”与CLI同页对账，不继续无边界增加恢复测试。CLI只读pending提示仍待收敛，必须保留既有JSON/RPC；完整parity/uptime总验收也未完成，原生启动写不得为追求parity越过既有批准边界。 |

## 2026-11-05 — CLI恢复的原子预占与回执协调（R95，写端小片）

本片接续R94，沿[已批共享pending/原ID合同](<docs/design-system.md#L536-L541>)补齐CLI recovery resume写端协调。命令/JSON DTO/RPC、Agent准入策略与全部GUI不变，G2/VIS为N/A；**整个GUI/CLI/监测目标及G3仍未完成**。

| 类别 | 结果及证据 |
| --- | --- |
| 修复前证据 | 预占先于工厂/诊断、注入发送异常保留原ID的9个新行为用例全部失败，见[TRX](<docs/visual/sa-20261105/r95-recovery-cli-reservation/before-results/r95-before.trx>)。首轮[测试编译错误](<docs/visual/sa-20261105/r95-recovery-cli-reservation/initial-test-compile.txt>)来自误用fixture不存在的Journal属性，已改用真实AgentOperationClient；该编译失败未算行为红灯。 |
| 实现 | [CLI入口差异](<docs/visual/sa-20261105/r95-recovery-cli-reservation/cli-entry.patch>)将recovery工厂延迟到[专用协调器](<src/SystemAutorun.Cli/CliApplication.Recovery.cs#L8-L44>)：规范化原ID、校验规范recovery revision，再用共享RecoveryPendingStore原子Reserve；落盘/回读完成并输出、flush诊断后才创建transport、单次发送原name/revision/ID。Missing前置读取不再是发包权，Reserve仍在短root lease内复核冲突；RPC期间不持root lease。不额外复读eligibility、不换ID/revision、不重试或改变server准入。发布不明且未取得Reservation时，只恢复实际可读原记录，不发包、不清档、不展示候选ID。 |
| 严格清档边界 | [实际发送catch](<src/SystemAutorun.Cli/CliApplication.Recovery.cs#L46-L60>)与[ACK验证/清档](<src/SystemAutorun.Cli/CliApplication.Recovery.cs#L61-L94>)分离。已核对[AgentClient attemptedWrite](<src/SystemAutorun.Core/Ipc/AgentClient.cs#L97-L175>)；本片比self更保守，仅具体PipeTransport的未包装timeout/cancellation按pre-send释放，进入发送后的IO/参数/权限异常不推断未发送，任意注入transport异常均Unknown。未调用SendAsync的工厂/诊断失败可释放本次预占；伪造的异常ID不成为恢复ID。仅typed fresh Ok且非replay可清；fresh error（含not_executed）、重放及畸形ACK全部保留。验证只消费证明，不用DTO投影覆盖原CLI wire Data，未知字段仍保留；ACK不是当前状态观察。清档bytes变更则pending_clear_unverified且不删除替换记录。 |
| 真实跨端 / 竞争 | [29新增用例](<tests/SystemAutorun.IntegrationTests/RecoveryCliReservationTests.cs>)中，生产AgentServer＋独立Console的fresh ACK清档，较新暂停后新的明确意图可再提交，两份Journal且配置bytes不变。ACK丢弃及双Console用例使用owned Pipe framing＋真实Dispatcher/controller/store/Journal，不冒称生产AgentServer：CLI丢ACK后，首次打开GUI Core workflow从磁盘得到同一原ID，历史Completed查询不解锁；再开Console不重提。双Console在首份ACK被暂扣时只有winner发包，loser返回recovery_busy和winner原ID，释放ACK后结果0/6、pending Missing、WorkloadCalls=0。这不是运行窗口或原生键鼠验收。 |
| 失败路径 / DATA | 覆盖显式/自动ID、工厂及诊断先后、7类注入发送异常、fresh错误/not_executed/replay、错root/ID/name/畸形ACK、pending bytes被替换、预占lease失败/取消、真实离线Pipe未发送超时。仅持有本次Reservation才能释放，历史/重开/当前状态均不解锁；发送前失败输出无虚构恢复ID。R94的14项仍运行，[旧测试适配差异](<docs/visual/sa-20261105/r95-recovery-cli-reservation/existing-tests.patch>)只补完整有效ACK、partial拆分及竞争Console的10秒专用timeout，不删除断言或跳过测试。 |
| ENG | 初轮[聚焦41/41](<docs/visual/sa-20261105/r95-recovery-cli-reservation/focused-results/r95-focused.trx>)之后补两项预占失败/取消测试，并更正取消诊断不声称清除了未取得的Reservation，再跑最终[Debug](<docs/visual/sa-20261105/r95-recovery-cli-reservation/final-results/r95-final-Debug.trx>)/[Release](<docs/visual/sa-20261105/r95-recovery-cli-reservation/final-results/r95-final-Release.trx>)各594/594、0失败/跳过，含43 Recovery CLI（14旧＋29新）、19 GUI及8真实workflow。筛选为`FullyQualifiedName~Recovery\|FullyQualifiedName~Cli`，不是全solution/全视觉矩阵。 |
| 冻结 / 清理 | [132源输入](<docs/visual/sa-20261105/r95-recovery-cli-reservation/sources.json>)中128继承SHA保持，声明CLI入口/旧测试两项修改及协调器/新测试两项新增。[终检](<docs/visual/sa-20261105/r95-recovery-cli-reservation/audit-after.json>)源漂移0、相关进程/私有测试键/fixture service/临时候选均0、diff-check=0；只回收已核验普通空测试父目录。未改clipboard/DPI、真实工作负载/Run/Boot、安装/发布/提交，保留其他dirty工作。 |
| 剩余目标 | 本片只验收恢复写端协调；CLI只读pending诊断、完整功能对照/uptime与日志总验收仍需推进。R93原生宿主超时及剩余键盘、Copy、DPI/VIS矩阵未被这些通过项覆盖，保留原失败证据，不宣布整体G3完成。 |

## 2026-11-05 — CLI恢复的既有未决前置保护（R94小片，写端协调仍未闭环）

本片沿[已批共享契约](<docs/design-system.md#L536-L541>)修复CLI绕过已发布GUI pending的问题。它只加连接前保护，不补全CLI预占/ACK协调；**整体G3仍开放**。命令语法、JSON DTO、RPC、Agent准入/恢复策略及全部GUI保持不变，因此G2/VIS为N/A，不重新审批或生图。

| 类别 | 结果及证据 |
| --- | --- |
| 根因复现 | [新增14用例](<tests/SystemAutorun.IntegrationTests/RecoveryCliPendingTests.cs>)在修复前为5 passed/9 failed、0跳过，见[原始TRX](<docs/visual/sa-20261105/r94-recovery-cli-gate/before-results/r94-before.trx>)。真实GUI Core workflow丢ACK后保留原ID；在同一隔离root记录较新Stop意图，再启动独立Console。旧CLI仍发送第二次恢复，实测`exit=0; paused=False; mutation RPC delta=1; revision unchanged=False`，见[日志:12](<docs/visual/sa-20261105/r94-recovery-cli-gate/before-test.txt#L12>)。这是测试owned Pipe framing＋真实Dispatcher/controller/store/Journal，不是生产AgentServer，也没有运行真实工作负载。 |
| 最小修复 | [CLI:64](<src/SystemAutorun.Cli/CliApplication.cs#L64>)在生成候选ID及连接工厂之前纯读同root/主体/名称pending。[保护函数](<src/SystemAutorun.Cli/CliApplication.cs#L313-L325>)对Present返回`recovery_busy`/exit6，并仅报告有效原ID与已有`operation show`查询指引；对不可核验记录返回`pending_storage_unavailable`/exit3，不虚构ID。取消读取返回cancelled/exit6、无ID/连接。不写记录、不清档、不把历史查询当解锁。与本片前代码的[独立差异](<docs/visual/sa-20261105/r94-recovery-cli-gate/cli-gate.patch>)保留了无关dirty工作。 |
| FLOW / DATA | 14用例覆盖有/无显式替换ID、大小写同名、有效/损坏/目录记录、取消、不同名/不同root/Missing，以及既有state/operation只读路径。真实独立Console现在阻断GUI-origin未知原ID的重提；较新暂停revision、pending bytes及Journal数量不变，WorkloadCalls=0。随后独立Console读取原回执及recovery state仍成功，GUI保持OutcomeUnknown，历史数据不解锁。无hint路径继续保留调用方revision/ID及未知JSON字段，不替换现有envelope。 |
| ENG | `FullyQualifiedName~Recovery\|FullyQualifiedName~Cli`的[Debug](<docs/visual/sa-20261105/r94-recovery-cli-gate/after-results/r94-Debug.trx>)/[Release](<docs/visual/sa-20261105/r94-recovery-cli-gate/after-results/r94-Release.trx>)各565/565、0失败/跳过，包含14新增、19既有Recovery GUI和8真实workflow用例。这是筛选的IntegrationTests回归，不是全solution重跑，也不替代R93尚未完成的原生VIS/FLOW。 |
| 失败保留 | 首次[测试编译](<docs/visual/sa-20261105/r94-recovery-cli-gate/initial-compile.txt>)及[产品编译](<docs/visual/sa-20261105/r94-recovery-cli-gate/initial-product-compile.txt>)均因遗漏Core.Recovery using失败；已补齐再运行，未将编译失败计作行为红灯或跳过测试。修复前的9个真实行为失败另存，未覆盖。 |
| 冻结 / 清理 | [130源输入](<docs/visual/sa-20261105/r94-recovery-cli-gate/sources.json>)含129继承项及1新增测试；128继承SHA保持，仅声明CLI变更。[终检](<docs/visual/sa-20261105/r94-recovery-cli-gate/audit-after.json>)源漂移0、相关进程/私有测试键/fixture service/临时候选均0、diff-check=0，只额外回收已核验为空且非reparse的测试父目录。没有提交、发布、安装或改动真实Run/Boot。 |
| 尚未闭环 / 下一片 | 本次检查与发送之间仍有竞态；CLI自身发送仍无持久Reservation，丢回执不能靠本片保留原ID。下一片补原子预占→原请求单发→Recovery typed ACK；保留既有yes/revision/ID与server准入，不额外复读eligibility或新增RPC。仅本次Reservation＋已证明pre-send或typed fresh Ok且非replay可清；fresh error（包括not_executed）、replay、畸形ACK均保留。pre-send分类只围实际具体PipeTransport发送，不能罩住后续ACK验证；注入transport异常无pre-send证明。实施前仍须独立复核AgentClient attemptedWrite边界，不照搬self规则。 |

## 2026-11-05 — 恢复工具右键、安全 Enter、滚动及焦点量测（R93，手工宿主超时保留）

沿[已批精确补充](<docs/design-system.md#已与-r1-一并批准的精确补充>)推进分项验收。只改[owned观察器](<tests/SystemAutorun.IntegrationTests/RecoveryGuiEntryProbe.cs>)，不改产品布局/主题、RPC或CLI契约。**首个手工宿主失败，整体G3保持开放**；下表将deadline之前已观测的旅程与宿主结果分开，不用成功片段覆盖失败。

| 类别 / 规则 | 结果及证据 |
| --- | --- |
| 夹具修复 — CON-2、STA-1 | 用真实SearchText筛选路径刷新派生统计，不硬写count/IsEmpty；新增断言1条可见行、Total=1、非空。[当前主窗像素](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/main-count-fixed-150.png>)不再出现R92空态覆盖行。仍是UNKNOWN投影＋真实隔离AgentServer，不据此宣布生产主屏内容/监测已验。owner1320×820 DIP、150%，[握手](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/ready.txt>)绑定PID27300、主窗13175892和私有root。 |
| FLOW — INT-7/8、VER-4 | [动作索引](<docs/visual/sa-20261105/r93-recovery-native/native-actions.json>)保留首次右键331恢复Shell前台后菜单消失，未算通过；观察后一次普通native ActivateWindow338成功，未遇前台锁拒绝或绕过，不冒称后台/不影响前台。339/341及345/347两次SendInput右键media行→fresh菜单边界点击，打开真实handler工具21692802/119737512，owned HWND=GetForegroundWindow、owner disabled。非浏览器操作。 |
| FLOW — 安全Enter | 工具21692802中Tab342到Prepare→Enter343仅准备，核验Cancel焦点、AwaitingConfirmation且ID null→Enter344取消。[事件索引34](<docs/visual/sa-20261105/r93-recovery-native/event-index.json>)与[原始事件](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/events.jsonl>)证实正常返回native enabled主窗/ListBoxItem焦点，media、med及Workloads保留、Ready无ID。没有明确提交。 |
| FLOW/VIS — SCR-4、INT-7、ALN-4 | 原[展开技术区](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/technical-expanded-820-150.png>)内容能放下，不冒称滚动；随后实际拖边角到700×490 DIP，[独立正文区与footer](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/technical-resized-top-700-150.png>)仍可达。四次原生wheel353/355/357/358均有前台相等事件，offset 0→48→96→48→0；[中部screen-region](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/technical-wheel-middle-700-150.png>)与[下部PrintWindow](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/technical-wheel-lower-700-150.png>)实际像素已查看，完整技术修订可读。总range131.667 DIP，**未滚至精确末端**；所有观测footer均在window内，不等于全部尺寸/DPI矩阵。 |
| VIS — INT-2/3、COL-3、VER-5 | [原生前台无hover采样](<docs/visual/sa-20261105/r93-recovery-native/foreground-color-samples.json>)选同一HWND的末次可比端点，经[只读量测](<docs/visual/sa-20261105/r93-recovery-native/native-measurements.json>)：Refresh/Cancel的FG静态F4F4F5→焦点8B8B94为3.072:1、焦点对070708为5.962:1；Prepare永久边框F4F4F5→6B6B75为4.794:1，文字09090B对E4E4E7为15.679:1。三控件FocusVisualStyle null、glyph厚0/primary厚1均不变。只通过这些实际端点，不宣称全部控件/状态、文字、motion时长或完整VIS已验。 |
| 失败 / 未完成 — VER-6 | [手工宿主日志](<docs/visual/sa-20261105/r93-recovery-native/native.txt>)/[TRX](<docs/visual/sa-20261105/r93-recovery-native/focused-results/r93-native.trx>)为2 passed/1 failed：观察持续到[helper第94行](<tests/SystemAutorun.IntegrationTests/RecoveryGuiEntryProbe.cs#L94>)10分钟WaitAsync超时。359只完成下一次Tab到Prepare，后续目标查找已无窗口，停止未重放；没有延长deadline、跳过测试或覆盖失败记录。最终工具/main自动回收不是正常手动关闭通过；完整Tab/Shift+Tab、键盘明确提交、未知/已确认后原生生命周期、Copy往返/失败、100%/125%及其余矩阵仍开放。 |
| DATA / ENG | [finally状态](<docs/visual/sa-20261105/r93-recovery-native/native-first-timeout/after.json>)Ready、ID null、pending Missing、Journal0、observer错误0（4 pointer/5 key/4 wheel/3工具）。移除probe变量后的[Debug](<docs/visual/sa-20261105/r93-recovery-native/regression-results/r93-Debug-regression.trx>)/[Release](<docs/visual/sa-20261105/r93-recovery-native/regression-results/r93-Release-regression.trx>)各27/27、0跳过＝19 GUI＋8真实workflow；这些通过不抹掉人工宿主超时，也不是R91 1780/配置矩阵重跑。[冻结129输入](<docs/visual/sa-20261105/r93-recovery-native/sources.json>)仅声明1项观察器变动、128项原SHA保持。 |
| 清理 | [终检](<docs/visual/sa-20261105/r93-recovery-native/audit-after.json>)核对0源码漂移、相关进程/私有键/fixture service/临时候选均0，真实fixture root已消失、diff-check=0。仅回收已同SHA归档的普通owned probe/5截图和已验证空的共享测试父目录。未动clipboard、系统DPI、真实工作负载或Run/Boot。 |
| 下一功能片 | 只读咨询线索经Lead抽查：[CLI:158–160](<src/SystemAutorun.Cli/CliApplication.cs#L158-L160>)仅self分支调用pending协调，recovery仍直接SendAsync；对照[GUI workflow:47–51](<src/SystemAutorun.Core/Ipc/RecoveryWorkflow.cs#L47-L51>)会先读本地pending。下片优先真实验证CLI是否绕过GUI原ID未决保护，再按已批合同复用recovery专用预占/ACK边界；不照搬self凭据规则、不改CLI语法/JSON，**此处尚未实现或验证竞态后果**。 |

## 2026-11-05 — 恢复工具真实更多入口与安全键盘（R92，分项补验）

沿[已批r1精确补充](<docs/design-system.md#已与-r1-一并批准的精确补充>)补R91的主窗旅程，不改产品行为、主题、CLI语法/JSON或审批范围。[入口套件](<tests/SystemAutorun.IntegrationTests/RecoveryGuiEntryTests.cs>)增加可选owned probe，[观察器](<tests/SystemAutorun.IntegrationTests/RecoveryGuiEntryProbe.cs>)只记录GetForegroundWindow、启禁用、焦点和输入事件，不激活窗口、不合成输入或读写剪贴板。**此片不是恢复GUI或整体G3完成**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 环境 / 数据 | [握手](<docs/visual/sa-20261105/r92-recovery-native/native/ready.txt>)绑定PID26040、主窗HWND24775614、私有root，owner1320×820 DIP、实际150%。production AgentServer承载恢复读取；主窗行是明确UNKNOWN测试投影，关闭runtime启动、自动刷新与远程连接，没有monitor、工作负载、Run/Boot。 |
| FLOW — 真实更多鼠标入口 | [动作索引](<docs/visual/sa-20261105/r92-recovery-native/native-actions.json>)区分两次打开：首次用UIA更多/菜单项进入；第二次工具323/325用SendInput点击更多及fresh UIA边界内的恢复菜单项，得到新HWND10686902。[观察日志](<docs/visual/sa-20261105/r92-recovery-native/native/events.jsonl#L21-L28>)记录主窗左键及新工具打开；两个工具均原生禁用owner。Popup不属于主窗路由树，观察器只记录1次pointer，不把它冒称记录了两次点击。 |
| FLOW — Tab / Enter / Escape | 首次工具HWND10621366的[原生前台与输入](<docs/visual/sa-20261105/r92-recovery-native/native/events.jsonl#L8-L20>)证实：安全Refresh焦点→SendKeys Tab到Prepare→Enter仅进入确认，焦点到Cancel且ID仍空→Escape关窗不提交。工具318/319/321实际送达事件；不是RaiseEvent或只读UIA焦点标志，也不冒称人手硬件输入。未测Enter在Cancel上、完整Tab循环、显式键盘提交或未知态键盘。 |
| FLOW — 原入口返回 | 两次Escape后[日志20/28](<docs/visual/sa-20261105/r92-recovery-native/native/events.jsonl#L18-L28>)均为主窗HWND等于GetForegroundWindow、owner enabled、Keyboard焦点ServiceMoreButton，服务media/筛选med/Workloads保持。Escape的观察handler在产品关闭handler之后运行，故其tool HWND已为0；返回结论取后续主窗观测，不倒推按键前状态。第二次Escape亦经真实SendKeys326。 |
| DATA — 取消后的磁盘边界 | [最终状态](<docs/visual/sa-20261105/r92-recovery-native/native/after.json>)为Ready、ID null、pending Missing、Journal文件0、observer错误0；2个工具/4次键事件，未明确确认。它不证明未知结果现场丢ACK或已确认后关闭，这些仍沿R91已有分项边界。 |
| VIS — 运行像素 | Lead查看[暂停页](<docs/visual/sa-20261105/r92-recovery-native/native/tool-paused-150.png>)和[确认Cancel焦点页](<docs/visual/sa-20261105/r92-recovery-native/native/tool-confirm-cancel-focus-150.png>)：完整服务/root、真实PausedAt、同壳footer，无新增ring。暂停图为screen-region，观察器已核对owned前台；确认/主窗图为PrintWindow，[方法与SHA](<docs/visual/sa-20261105/r92-recovery-native/images.json>)保留。不把PrintWindow说成屏幕区域取帧；焦点/静态3:1、文字/底色4.5:1未重新测量，完整VIS未关闭。 |
| 夹具限制，非产品主屏验收 | [返回主窗](<docs/visual/sa-20261105/r92-recovery-native/native/main-more-return-150.png>)中，手工追加UNKNOWN行没有走VM的UpdateCounts，计数仍0、空态文字与投影行重叠。不能据此判生产主屏布局/统计已通过，也不修产品布局掩盖夹具问题。下一次右键验收先让owned fixture走正常筛选/计数刷新，再测列表命中。 |
| ENG — 当前版本 | [Debug](<docs/visual/sa-20261105/r92-recovery-native/regression-results/r92-Debug-regression.trx>)/[Release](<docs/visual/sa-20261105/r92-recovery-native/regression-results/r92-Release-regression.trx>)各27/27、0跳过，逐项19 GUI＋8真实workflow；另[native宿主](<docs/visual/sa-20261105/r92-recovery-native/focused-results/r92-native-entries.trx>)3/3，其中人工probe不是新增3个用例。[129项冻结输入](<docs/visual/sa-20261105/r92-recovery-native/sources.json>)中，R91继承128项仅声明1项入口测试变动、127项原SHA保持，另加观察器；反向去除4处probe添加后原测试等于before副本。不是integration全集或重跑R91的1780/配置矩阵。 |
| 清理 / 工具边界 | [终检](<docs/visual/sa-20261105/r92-recovery-native/audit-after.json>)确认当前冻结0漂移，相关进程、私有键、fixture service与SystemAutorun临时候选均0，fixture root已消失，diff-check=0。仅回收已归档同SHA的普通owned probe/截图及经空内容核验的共享测试父目录。首版清理审计将FileInfo误当DirectoryInfo用Parent而安全拒删；只读核验后改DirectoryName并保留原全部SHA/名称/reparse约束，终检成功。UIA313报告应用自行前移后返回，后续观察亦记录其前台变化，不保证UIA绝不影响前台；未调用ActivateWindow或改前台锁，没有浏览器操作。 |
| RG01–06仍开放 | 真实右键、完整Tab/Shift+Tab、Enter安全取消及键盘明确确认、未知态/已确认后关闭的原生旅程、长技术内容鼠标滚动、Copy往返/失败、100%/125% DPI与完整焦点对比度。保留[分项审查](<docs/visual/sa-20261105/r92-recovery-native/review-disposition.json>)；既有CLI协同/uptime/日志和本次原生补验都不表示整体parity/G3完成。 |

## 2026-11-04 — 恢复状态按需 GUI（R91，原生键鼠及部分 DPI 仍开放）

本片按[已批r1及精确补充](<docs/design-system.md#已与-r1-一并批准的精确补充>)接入[VM](<src/SystemAutorun.App/ViewModels/RecoveryViewModel.cs>)与[模态窗口](<src/SystemAutorun.App/Dialogs/RecoveryWindow.xaml>)。本机选中服务的更多/右键入口复用该工具，远程、busy或无选择时拒绝打开；只传名称和App私有root，不接提权、pause、初始化修复、新RPC或CLI写流程。解除暂停不启动服务，不改变全局开关；**本片不关闭恢复GUI或整体G3**。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW / DATA — VM与production宿主 | [窗口套件](<tests/SystemAutorun.IntegrationTests/RecoveryGuiTests.cs>)验证读真实配置/恢复意图、准备后零ID/零hint、唯一显式确认、fresh ACK后独立新读与精确清档。确认前资格丢失不发送；uninitialized/disabled/missing/unpaused/corrupt/不合资格状态没有解除入口，且不默认正常或提供修复。异常后不会把旧观察、ACK Data或历史当作运行/健康证据。 |
| FLOW — 生命周期与失回执 | 同套件另用test-framed真实Dispatcher/Journal宿主扣留复核读取或ACK，验证关窗/owner销毁只取消并join本视图未确认读取，确认claim后CheckingConfirmation/提交不撤回；同名协调器仍busy，直到实际workflow退出才回收root。失ACK、ResultUnavailable、post-ACK新读失败分别保留原ID或已知完成，历史/当前读不解锁。它不是全production传输或强制销毁owner后原生模态返回的证明。 |
| FLOW — 实际入口自动化3例 | [入口套件](<tests/SystemAutorun.IntegrationTests/RecoveryGuiEntryTests.cs>)对更多/列表两个真实MenuItem handler使用RoutedEvent进入ShowDialog，验证固定root/服务、native owner禁用、取消未确认准备、返回原选择/筛选及无hint/Journal mutation。另验remote/busy拒绝与无remote连接。它证明WPF语义路由/原生模态禁用，不证明鼠标右键命中、物理Tab/Escape或焦点回到原入口。 |
| VIS — 实际运行像素（150%） | Lead查看[暂停](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-paused-150.png>)→[确认](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-confirm-150.png>)→[返回后独立状态](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-completed-150.png>)与[未知查询](<docs/visual/sa-20261104/r91-recovery-gui/native/unknown-after-query-r2-150.png>)。确认/未知均显示完整服务与换行root；没有Play、生成齿轮、重复未知正文或ring。Copy已补共享细线字形，没有再落回Service图标。前三张及unknown r1使用screen-region，工具报告occludedPossible=false且随机root与握手一致；未另核对Win32前台句柄，不据此宣称原生焦点或完整VIS。unknown r2使用PrintWindow。此处不把RenderTargetBitmap或UIA树冒称屏幕像素。 |
| FLOW / DATA — native UIA正常旅程 | [握手](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-ready.txt>)绑定PID 22244/HWND 101257218、私有root与DPI1.5。UIA Invoke准备→观察确认页安全Cancel逻辑焦点→单次明确确认→观察“自动恢复未暂停”且不代表运行/健康→关闭。[结果](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-after.json>)为Completed、hint Missing、observer无错；[磁盘事实](<docs/visual/sa-20261104/r91-recovery-gui/native/normal-files-after.json>)仅1个真实Journal记录。production AgentServer真实执行resume控制面，夹具没有monitor/工作负载/真实Run或Boot。 |
| FLOW / DATA — native UIA未知旅程 | [r2握手](<docs/visual/sa-20261104/r91-recovery-gui/native/unknown-r2-ready.txt>)绑定PID 16724/HWND 85528190。夹具先持久reserve原ID模拟重开未决，**不是原生窗口现场失ACK**；UIA只查询原操作，显示NotFound不代表未执行，当前paused独立读取，动作仍只有query/copy/close。[结果](<docs/visual/sa-20261104/r91-recovery-gui/native/unknown-after-r2.json>)保持OutcomeUnknown/原ID/Present；只允许精确operation_not_found业务诊断，其他observer错误仍失败。[r2运行](<docs/visual/sa-20261104/r91-recovery-gui/native-unknown-r2.txt>)与[正常运行](<docs/visual/sa-20261104/r91-recovery-gui/native-normal.txt>)均通过。没有调用Copy或读写用户剪贴板；工具308/310报告应用自行前移后已返回，不能保证原生UIA绝不影响前台。 |
| VIS / KFL — 几何与组件边界 | [r3几何](<docs/visual/sa-20261104/r91-recovery-gui/ui-r3/geometry.json>)记录1120/1320/1620 DIP owner下680/820/820 DIP工具宽，确认态高482 DIP，实际DPI均1.5；独立正文滚动不挤掉footer。窗口复用既有字体、纯色焦点/reduced-motion和方形滚动条，确认/未知转换回正文顶部。未实测100%/125% DPI、物理键盘焦点色对比、长技术区真实鼠标滚动或返回焦点，相关VIS/FLOW保持开放。 |
| ENG — 新增测试与红例 | [focused r6](<docs/visual/sa-20261104/r91-recovery-gui/focused-gui-r6.txt>)通过19/19，含16窗口用例和3入口用例。初版ContextMenu类型歧义已限定WPF；坏状态提示补足“不重置”。[r4编译红例](<docs/visual/sa-20261104/r91-recovery-gui/focused-gui-r4.txt>)是新增入口夹具遗漏LaunchOptions参数，[r5](<docs/visual/sa-20261104/r91-recovery-gui/focused-gui-r5.txt>)已修并19/19。第一次[native unknown](<docs/visual/sa-20261104/r91-recovery-gui/native-unknown.txt>)因错误地要求observer全空而失败，[当时结果](<docs/visual/sa-20261104/r91-recovery-gui/native/unknown-after-r1.json>)仍保留原ID；按Dispatcher既有operation_not_found精确断言后重编并以native r2复验，不吞其他错误或跳过用例。 |
| ENG — 冻结回归与保留 | [最终34步](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/steps.json>)均exit0，Debug/Release各[1780/1780](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/totals.json>)、0跳过、构建0警告/0错误；[分组计数](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/test-summary.json>)保留。每配置单独核对19新增GUI、241既有恢复Core和8真实workflow用例全部Passed。输入继承R90的123项，仅[4项声明变化](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/existing-source-audit.json>)；反向去除R91添加并统一EOL后等于before副本，其余119项保持原SHA。加5新源码后冻结[128项](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/sources.json>)，[终检](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/source-audit.json>)0漂移。它仍是分项筛选矩阵，不是integration全集或原生G3。 |
| 清理 / 范围 | 三个native探针均已关闭并collect；确认各自fixture root已消失后，仅删除精确普通owned probe目录及已归档同SHA截图临时文件。[终检清理](<docs/visual/sa-20261104/r91-recovery-gui/matrix-accepted/cleanup-after.json>)确认相关进程、私有HKCU/HKLM键、fixture service、SystemAutorun临时候选均0；仅另删经普通目录/非reparse/空内容核验的共享测试父目录，diff-check=0。未杀无归属进程、删未知目录、安装、发布或提交，原dirty工作保留；没有真实用户服务启停或Run/Boot写入。 |
| RG01–06剩余 | 真实鼠标更多/右键→工具→原入口旅程、Tab/Escape/Enter安全行为与原生焦点返回、Copy往返、长内容鼠标滚动、100%/125% DPI和受影响状态的完整VIS/FLOW仍待验。UIA语义确认、测试通过或漂亮截图都不能替代这些余项；既有CLI协同/uptime/日志切片也不表示整体parity完成。 |

## 2026-11-04 — 名称级恢复 workflow（R90，GUI及CLI写入口仍未接）

本片接通[已批恢复工具Core顺序](<docs/design-system.md#2026-11-04--恢复状态工具-g2-候选-r1r87>)：新增[RecoveryWorkflow](<src/SystemAutorun.Core/Ipc/RecoveryWorkflow.cs>)、[当前观察器](<src/SystemAutorun.Core/Ipc/AgentRecoveryObservationClient.cs>)与[分离快照](<src/SystemAutorun.Core/Recovery/RecoveryWorkflowSnapshot.cs>)，复用R88客户端和R89 pending/receipt。另增两项Core测试及两项集成测试/宿主，共七项新源码，原产品及测试源码本片零修改。未增加RPC、CLI语法/JSON、GUI、提权、恢复策略或全局开关；不是恢复GUI完成。

| 类别 | 实际结果与限制 |
| --- | --- |
| DATA — 当前事实与资格 | ForTask按物理rootId＋当前Windows主体＋名字ignorecase共用进程内协调器，保持固定原名。Open只恢复hint；Refresh经实时configuration/recovery-state读取，不用Dashboard缓存或默认“未暂停”。两次读取不是原子联合快照。严格核对scope/revision、任务身份与选中项enabled/runAs/trigger，只有存在、启用、current_user/logon且已初始化paused才可准备；配置版本/类型/目标/无关任务字段不充当资格或健康。configurationRevision是诊断和原准备关联，不是目标pin/配置CAS。 |
| FLOW — 准备与单次确认 | Prepare再次只读，生成独立PreparationId且Confirmed=false，零操作ID/零pending。Submit精确匹配该准备的显式确认，在闸内claim后以独立生命周期CheckingConfirmation重新观察；已知资格丢失或恢复revision变化则不生成ID、保留或发送，要求重新准备/确认，绝不换修订重试。复核通过才mint一个UUIDv7、reserve成功后仅一次提交。复核到后端之间仍有竞态，Agent最终裁决；既有CAS冲突降Unknown限制未改。 |
| DATA — 清除与历史 | 仅现场严格验证的非replayed成功ACK或具体sealed AgentClient可证明写前的未包装IO/Timeout/OCE/Argument异常，才消费fresh Reservation精确清档；不能推广为任意transport异常分类。false/replay/写后unknown保守锁原ID，清除失败StorageBlocked。成功后新读当前状态，绝不应用ACK Data，后读失败不抹掉已知Completed；当前unpaused、原ID NotFound/CompletedHistory/ResultUnavailable及hint丢失/改写均不能解锁、改查他人ID或重提。 |
| FLOW — 视图生命周期组件 | [Core workflow](<tests/SystemAutorun.Core.Tests/RecoveryWorkflowTests.cs>)以generation屏蔽迟到读。Dismiss须匹配本视图cancellable token，默认或另一owner无权取消；TryDiscard须精确Preparation引用，不能丢另一份/后一次准备。读失效不冒称资源已退出；调用方仍应取消token并等待实际读。确认claim后不接view cancellation，关窗/取消awaiter不能撤回提交或解锁；已知Completed/NotSent及锁态不被关闭、读失败或迟到结果覆盖。这是组件测试，不是原生窗口关闭/焦点旅程。 |
| DATA / FLOW — production AgentServer 4例 | [真实宿主集成](<tests/SystemAutorun.IntegrationTests/RecoveryWorkflowIntegrationTests.cs>)复用原ConfigurationControlPlaneTests.ServerFixture：v2真实配置/恢复暂停→准备→确认→ACK清档并新读→再设暂停意图→另一次明确确认、新ID成功，物理hint恢复Missing、两个真实Journal记录，配置未改且observer无错。另3例在确认前已知revision/disabled/unpaused漂移，CheckingConfirmation零ID/零pending/零Journal mutation且原恢复字节不变。无监测循环或真实工作负载。 |
| DATA / FLOW — test-framed真实业务组件4例 | [专用封装](<tests/SystemAutorun.IntegrationTests/RecoveryWorkflowDispatchFixture.cs>)是测试Pipe framing，不是production AgentServer；内部用真实Dispatcher/controller/RecoveryStore/Journal，hooks在Journal外，异常由Dispose await，另断言WorkloadCalls=0防吞错。2例扣留已完成ACK，含MaximumResultBytes=1真实ResultOmitted→ResultUnavailable，历史查询与当前读都保留原hint；新Stop意图字节不被历史改写。1例在preflight后、实际dispatch前改恢复意图，验证原revision/ID保留，observer精确为recovery_revision_conflict而非Executor异常，原Journal为Unknown且单发。1例Journal已完成但ACK未交付时取消视图/关闭，仍持hint、同名重开共用busy，放行ACK后才精确清档。实际可取得RootWriteLease，证明磁盘租约未跨RPC。 |
| Core分项与红例 | [观察器测试](<tests/SystemAutorun.Core.Tests/RecoveryObservationClientTests.cs>)＋[workflow测试](<tests/SystemAutorun.Core.Tests/RecoveryWorkflowTests.cs>)最终[54/54](<docs/visual/sa-20261104/r90-recovery-workflow/focused-core-r3.txt>)，使用真实CurrentUserOnly Pipe但合成业务数据，不冒称production AgentServer；覆盖缺失/重复/错类型资格字段、准备篡改/旧确认、迟到/跨view、预发Connect超时、fresh后读新Stop、未知历史、坏/丢hint与clear只读失败。预发例复用R89私有published断点，等待真实listener退出后由sealed client Connect超时，验证精确清档/Missing/可再保留，而非假transport抛错。安全复核[三项红例](<docs/visual/sa-20261104/r90-recovery-workflow/review-three-before.txt>)实证无owner关闭、版本/无关字段越界及缺少确认后复核，已修并纳入54例；“取消永久busy”[green-before](<docs/visual/sa-20261104/r90-recovery-workflow/review-cancel-before.txt>)未复现，原ReadCore catch以无token ApplyRead复位，保留该回归而非照抄推断改代码。 |
| 夹具红例与修正 | 首轮集成[4过4失败](<docs/visual/sa-20261104/r90-recovery-workflow/focused-integration.txt>)全部失败于新test-framed夹具先Resolve后写autorun.yaml，未启动dispatcher；改为先写配置再Resolve。四个构造失败空root均经[精确普通空目录审计](<docs/visual/sa-20261104/r90-recovery-workflow/constructor-cleanup.json>)后清理，未删未知内容。修正后[8/8](<docs/visual/sa-20261104/r90-recovery-workflow/focused-integration-r3.txt>)通过。 |
| ENG — 冻结矩阵 | [最终34步](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/steps.json>)全部exit0，Debug/Release各 **1761/1761、0跳过、构建0 warning/error**。每配置Core970（新增54）、CLI374、配置26、Monitor30、原恢复控制器29、新workflow8、pending crash8、self workflow7、typed client16、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/test-summary.json>)与[合计](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/totals.json>)逐testName核对新54＋8、R89原85及R88原102两配置全过。这是筛选矩阵，不是integration全集、恢复GUI像素/键鼠或整体G3。 |
| 保全 / 清理 | [继承110项](<docs/visual/sa-20261104/r90-recovery-workflow/inherited-inputs.json>)＋[另6项依赖](<docs/visual/sa-20261104/r90-recovery-workflow/before-dependencies.json>)，[原status](<docs/visual/sa-20261104/r90-recovery-workflow/before-status.txt>)/[diff-stat](<docs/visual/sa-20261104/r90-recovery-workflow/before-diff-stat.txt>)保留；[既有审计](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/existing-source-audit.json>)确认116项全部未改，新增7项共[冻结123项0漂移](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/source-audit.json>)。[终检](<docs/visual/sa-20261104/r90-recovery-workflow/matrix-accepted/cleanup-after.json>)相关进程、私有HKCU/HKLM键、fixture服务与SystemAutorun临时候选均0，diff-check0；共享空父目录仅在精确普通非reparse/无内容验证后移除，未删未知数据、杀无归属进程或安装/发布/提交。 |
| 后续仍开放 | 最终只读复核无新增必修，建议补CheckingConfirmation读取失败的零ID断言、Query取消/超时Querying复位，以及production宿主参与的确定预发失败闭环；本片Core真实Connect负例不冒称该宿主闭环。下一片接已批r1 VM/按需窗口/更多入口，沿owned read token关闭、独立confirmed lifetime及既有focus合同，再做RG01–06原生VIS/FLOW/DATA/ENG；CLI写端协同接线/全业务覆盖与整体目标继续开放。无UI变化新增VIS N/A，不能把本片DATA/ENG组件证据等同恢复GUI或整个G3。 |

## 2026-11-04 — 恢复 pending 与原 ID 历史回执（R89，workflow/GUI仍未接）

本片落实[已批恢复工具r1的Core顺序](<docs/design-system.md#2026-11-04--恢复状态工具-g2-候选-r1r87>)，新增[专用hint/观察模型](<src/SystemAutorun.Core/Recovery/RecoveryPendingOperation.cs>)、[RecoveryPendingStore](<src/SystemAutorun.Core/Infrastructure/RecoveryPendingStore.cs>)及[AgentRecoveryReceiptClient](<src/SystemAutorun.Core/Ipc/AgentRecoveryReceiptClient.cs>)，配套两个Core测试与三个集成测试/宿主文件。旧产品仅将[Journal原指纹方法](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L406>)由private改internal、方法体不变；旧测试PE只加两行专用fixture分发。没有恢复策略、RPC、CLI语法/JSON、GUI、发行布局或原生Run/Boot变更；不把这三个组件冒称完整恢复协调器。

| 类别 | 实际结果与限制 |
| --- | --- |
| DATA — 持久原ID | hint按物理root目录＋当前Windows主体＋OrdinalIgnoreCase服务名隔离，内容保留原始case名称、root/subject、恢复expectedRevision、canonical UUIDv7及UTC时间。调用方确认后提供原ID；独占同目录临时写、WriteThrough/Flush(true)、不覆盖rename、原字节回读成功才返回Reservation。未成功返回不得发送；不承诺硬件掉电或本账户恶意编辑防护。原RootWriteLease只覆盖短磁盘段，不跨RPC；保留一名不作为全root逻辑封锁。 |
| DATA — 读取与清除 | 缺档纯读不建锁、不修复或清理孤立文件；严格required/大小写/重复/未知字段、4096字节、schema/UUIDv7/revision/UTC与root/主体/名称绑定，坏档或不可读返回Unavailable且不猜ID。重开只读hint，不恢复清除能力；只有fresh Reservation能单次验证原字节再清档，调用方必须另证确定预发失败或现场非重放ACK。超时、历史查询或当前已解除均不是该证明；本片尚无workflow替调用方落实此纪律。 |
| DATA — 历史关联 | 只发原ID的operation查询，核对live root/主体、禁止外层mutation operationId，再复用AgentOperationClient.ReadReceipt的严格骨架，核对命令、原ID、原始case名称及原revision＋null payload的Journal指纹。NotFound/Running/Unknown/ResultUnavailable/CompletedHistory只回元数据，不应用历史Data、不清档、不生成ID/重提/start；历史root存在须匹配，缺root可由已校验live query承载。RPC沿既有Journal读路径，可能取root锁/清其孤立临时文件，不冒称纯离线或全程无磁盘写。 |
| DATA — 并发与坏档实测 | [Core测试](<tests/SystemAutorun.Core.Tests/RecoveryPendingStoreTests.cs>)覆盖跨实例/名称case、并发同名只一ID、异名独立、foreign root/subject/name、缺字段与坏字节、取消/故障/发布回读、改档/空白/只读/删除/目录、cap单次与未知临时文件保留。[真Pipe合成回执](<tests/SystemAutorun.Core.Tests/AgentRecoveryReceiptClientTests.cs>)覆盖各历史态、独立指纹向量、命令/name/ID/revision/payload错配、畸变外层信封及业务错误原码；它是合成Journal数据，不冒称真实Agent产生全部状态。 |
| FLOW / DATA — 真实隔离组件 | [3例真实Pipe/Dispatcher/RecoveryStore/Journal](<tests/SystemAutorun.IntegrationTests/RecoveryPendingIntegrationTests.cs>)覆盖保留→单名解除→原ID历史查询保持hint→新Stop不被旧历史覆盖、未发送原ID的NotFound仍封锁、真实CAS冲突被原Journal降为Unknown仍保留原ID/revision/字节。复用私有root和抛错Executor/Probe，无监测循环或真实工作负载；NotFound的预期observer诊断有精确断言。[4例独立PE崩溃/坏档](<tests/SystemAutorun.IntegrationTests/RecoveryPendingCrashTests.cs>)在flushed/published/reserved强杀owned映像，跨新进程重读不清档/重提；reserved后另名跨进程仍能保留。flushed孤立临时不自动清理，未发布且从未发包才允许新intent；测试不宣称真实丢ACK工作流已接通。 |
| 红例与审查修正 | [首个focused](<docs/visual/sa-20261104/r89-recovery-pending/focused-core.txt>)为测试Assert.Throws的async分析器编译错误，改为await ThrowsAsync；[r2 Core82/82](<docs/visual/sa-20261104/r89-recovery-pending/focused-r2-core.txt>)、[集成6过1失败](<docs/visual/sa-20261104/r89-recovery-pending/focused-r2-integration.txt>)中唯一红例误把预期NotFound诊断当unexpected，修为精确operation/operation_not_found断言。安全角色指出并发reader会让单次clear失败；[3例红绿对照](<docs/visual/sa-20261104/r89-recovery-pending/review-before.txt>)实证2绿/1 sharing-violation红，[最小修正](<docs/visual/sa-20261104/r89-recovery-pending/review-fix.patch>)仅读句柄增加Delete共享，[同3例全过](<docs/visual/sa-20261104/r89-recovery-pending/review-shared-delete.txt>)且在慢读仍持句柄时已清除并读回Missing、旧reader仍可完成。另锁定合法JSON仅加空白也必须发布回读失败，以及只读清档失败保持原字节并消耗能力；没有未经实测套用Move覆盖打开目标的快照方案。 |
| ENG — 冻结后矩阵 | [最终32步](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/steps.json>)全部exit0，Debug/Release各 **1699/1699、0跳过、构建0 warning/error**。每配置Core916（新增85）、CLI374、配置26、Monitor30、原恢复控制器29、pending crash8（新增4）、workflow7、typed client16（新增3）、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/test-summary.json>)及[合计](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/totals.json>)逐testName核对新85＋4＋3与R88原102两配置均全过。这是筛选矩阵，不是integration全集、GUI真实像素/键鼠或整体G3。 |
| 保全 / 清理 | [继承102项](<docs/visual/sa-20261104/r89-recovery-pending/inherited-inputs.json>)及[原status](<docs/visual/sa-20261104/r89-recovery-pending/before-status.txt>)/[原diff-stat](<docs/visual/sa-20261104/r89-recovery-pending/before-diff-stat.txt>)保留。[继承审计](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/existing-source-audit.json>)验证100项未改、2项严格逐字节机械变换；新文件8项共[冻结110项](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/source-audit.json>)0漂移。[清理终检](<docs/visual/sa-20261104/r89-recovery-pending/matrix-accepted/cleanup-after.json>)确认相关进程、私有HKCU/HKLM键、fixture服务与SystemAutorun临时候选均0、diff-check0；只在精确绝对路径/普通非reparse/空内容验证后清理测试共享父目录，未删未知数据、杀无归属进程或安装/发布/提交。 |
| 后续仍开放 | 通用角色复核无新增阻断，建议的真实fresh ACK→精确clear→再保留、确定预发拒绝clear、真实超限回执ResultUnavailable闭环尚未做，随下一片活workflow补验；不把Core合成/分立组件证据当这些闭环。下一片接按名字绑定的资格读取、逐次确认、先保留后单次发送、known/unknown与关闭/迟到结果协调，再接已批r1 VM/按需窗口/更多入口与RG01–06。恢复GUI未实现、VIS/FLOW未验，当前无UI变化新增VIS为N/A；原Agent CAS冲突降级限制未改，CLI跨端/原生输入/DPI及整体目标继续开放，不重复审批已批范围。 |

## 2026-11-04 — Core 类型化恢复客户端（R88，GUI及pending仍未完成）

本片直接落实[已批恢复工具r1的实施顺序](<docs/design-system.md#2026-11-04--恢复状态工具-g2-候选-r1r87>)，只增加[共享客户端](<src/SystemAutorun.Core/Ipc/AgentRecoveryClient.cs>)与[Core边界](<tests/SystemAutorun.Core.Tests/AgentRecoveryClientTests.cs>)/[真Pipe与Console集成](<tests/SystemAutorun.IntegrationTests/RecoveryClientTests.cs>)三项源码。它复用原`recovery-state`、`recovery-resume`和原Journal，不修改Agent/RecoveryControlStore/Journal策略、CLI语法、GUI或发行布局。这里的ACK是本次响应或重放的历史快照，不是完整pending协调，也不是当前运行健康。

| 类别 | 实际范围与边界 |
| --- | --- |
| DATA — 类型化观察 | ReadAsync严格读Initialized、恢复revision及完整task字段；区分未初始化空态与已初始化空态，不由DTO默认值填补缺失bool/字段。限制原1024项/字节大小、拒绝snapshot和task重复/大小写混淆属性、重复名、坏UUIDv7/摘要/时间/enum/detail，以及暂停时间/尝试骨架矛盾；外层revision须与Data一致，集合只读。不计算健康、资格或配置CAS，不将missing/坏档补空或初始化。 |
| DATA — 单次提交 | 名称/revision/明确确认/外部原UUIDv7缺失或坏值在root/transport前拒绝；不生成ID、不重试、不rebase、不代start。live ACK核对root/原ID、有效快照及目标unpaused+intent；失败丢弃Data，保留code/ID/replay，不把失败Data当当前状态。调用方仍须观察已初始化/暂停、确认并在发出前持久保留ID；本片没有假称这些上层约束已经接线。 |
| 未知与历史 | 写后断链、错root/原ID/revision、缺失/重复Data和坏成功体均保持原UUIDv7 unknown；写前取消/超时不伪称已接受。重放ACK的旧unpaused不能覆盖后续stop观察，成功也不说明服务已运行或允许清pending。历史Journal命令/名字/fingerprint关联、跨进程pending及未知解锁仍属下一片；不对operation_not_found/running/unknown/omitted推导重提。 |
| 后端现状限制 | [既有Resume CAS](<src/SystemAutorun.Agent/AgentTaskController.cs#L89-L102>)抛`recovery_revision_conflict`，但原Journal的uncertain分类会转成`outcome_unknown`并记Unknown；新增真Pipe用例固定该事实，文件不变、仅原ID回执。客户端不解析文本猜冲突，不把它当普通拒绝/新确认许可；本片不更改后端分类，RG03完整冲突UX未验收。 |
| FLOW / DATA — 真隔离边界 | 复用CurrentUserOnly真Server/Dispatcher/Workspace/RecoveryStore/Journal的私有root，五例覆盖missing只读、有效单名解除并保留另一名暂停、旧ID历史重放不覆盖新stop、CAS降级unknown、损坏原文保持。独立Console PE从同一Agent读回，与typed/wire逐记录和revision比较；配置字节、configuration revision和全局AutoRecover=false均保持。Executor/Probe抛错禁止调用，ObservedErrors保证有效路径无被吞的执行断言；没有监测worker/真实服务进程或生产数据。读取可能取得RootWriteLease并创建锁文件，不冒称全程无文件系统写。 |
| 红例与复核 | [首轮构建](<docs/visual/sa-20261104/r88-recovery-client/focused-build.txt>)0 warning/error，但[Core原85例7失败](<docs/visual/sa-20261104/r88-recovery-client/focused-core.txt>)暴露业务异常被InvalidOperation过滤误转协议错误，以及case-insensitive JsonNode夹具没有真正保留大小写冲突。安全角色只读指出记录骨架、读协议异常归一化和只读集合断言不足，Lead最小修正；[r2 focused](<docs/visual/sa-20261104/r88-recovery-client/focused-r2-core.txt>)95/95。其后复核再发现信封类型错配的InvalidOperation裸逸出；[真Pipe新9例4红/5绿](<docs/visual/sa-20261104/r88-recovery-client/review-r3-before.txt>)复现id/version/root/顶层array四支，修复后[102/102 focused](<docs/visual/sa-20261104/r88-recovery-client/focused-r3-core.txt>)全过，同时覆盖已初始化空态。原Agent/transport文件未动，非吞掉业务异常。 |
| ENG — 最终矩阵 | [修正后32步矩阵](<docs/visual/sa-20261104/r88-recovery-client/matrix-accepted/steps.json>)全部exit0；Debug/Release均0 warning/error，各 **1607/1607、0跳过**。每配置Core831（新增102）、CLI374、配置26、Monitor/Dashboard30、原恢复控制器29、pending crash4、workflow7、typed client13（新增5）、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r88-recovery-client/matrix-accepted/test-summary.json>)与[合计](<docs/visual/sa-20261104/r88-recovery-client/matrix-accepted/totals.json>)逐testName核对新增102+5两配置均全过。这是筛选回归，不是integration全集或整体G3。审查前各1600项全过的[首矩阵](<docs/visual/sa-20261104/r88-recovery-client/matrix/totals.json>)完整保留，[取代说明](<docs/visual/sa-20261104/r88-recovery-client/review-supersession.json>)及[最小修正差异](<docs/visual/sa-20261104/r88-recovery-client/review-r3-fix.patch>)明确它不替代最终输入。 |
| 保留 / 清理 | [99项继承输入](<docs/visual/sa-20261104/r88-recovery-client/inherited-inputs.json>)及[原status](<docs/visual/sa-20261104/r88-recovery-client/before-status.txt>)、[原diff-stat](<docs/visual/sa-20261104/r88-recovery-client/before-diff-stat.txt>)保留；新产品/测试仅3个新文件，99项旧输入未改。最终[102项冻结源终检](<docs/visual/sa-20261104/r88-recovery-client/matrix-accepted/source-audit.json>)0漂移，[清理终检](<docs/visual/sa-20261104/r88-recovery-client/matrix-accepted/cleanup-after.json>)确认相关进程、私有HKCU/HKLM键、fixture service和SystemAutorun临时候选均0、diff-check0。仅按精确路径/普通非reparse/空内容验证后清理测试共享父目录；未删未知数据或杀无归属进程，未安装/发布/提交。 |
| 继续开放 | 新代码尚未接GUI或CLI写流程；未实现pending持久化、原ID历史回执协调和恢复窗口/入口，RG01–06及GUI VIS/FLOW均未验收。本片未改UI，新增G2/VIS为N/A；原84项GUI自动回归不代替真实像素/键鼠/DPI证据。下一片直接接Core pending/协调器，再接已批r1工具，不重复审批，不安装/发布/提交或操作真实Run/Boot。整体目标继续开放。 |

## 2026-11-04 — 恢复状态 GUI G1 批准与 G2 候选（R87）

本片从实际缺口接续，没有把主屏r4/共享样式或既有CLI recovery RPC当作新GUI许可。Lead核对
[原TaskDetail只读数据](<src/SystemAutorun.App/Dialogs/TaskDetailWindow.xaml.cs>)、[现有任务命令](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L406-L441>)及
[Agent恢复CAS/资格](<src/SystemAutorun.Agent/AgentTaskController.cs#L89-L105>)，确认App尚无恢复状态接线。用户在本地问答明确选择
**“批准流程，继续 G2（Recommended）”**，批准[最小G1-recovery-gui-r1及RG01–06](<docs/architecture.md#2026-11-04--恢复状态-gui-g1-r1r87交互已批准>)。在真实r1与精确补充展示后，用户另行选择 **“批准 r1、补充并实施（Recommended）”**，批准G2-recovery-gui-r1与最小实施范围；后一次确认才授权实施，不从G1/生成许可推断。

| 项目 | 当前事实与边界 |
| --- | --- |
| G1 | 当前服务更多入口的按需恢复状态；已暂停可明确确认解除，不直接启动、不改全局开关。未初始化/坏档/离线不伪正常，本片不新增pause/初始化修复/远程或SYSTEM/Boot。名称级意图不冒充配置CAS/目标pin，冲突重读后再确认，未知原ID跨重开/重启保留，不盲重提。 |
| G2 产物 | [r1真实生成PNG](<docs/visual/sa-20261104/recovery-gui-g2-r1.png>)1536×1024 / 1,373,654 bytes；已完整解码、核对PNG格式/后缀、字节一致与SHA256，并查看像素。[校验记录](<docs/visual/sa-20261104/recovery-gui-g2-r1.image.json>)与[完整prompt](<docs/visual/sa-20261104/recovery-gui-g2-r1.prompt.md>)保留；只生成一次、沿用户默认参数/模型，实际引擎未回报，无变体/回退。 |
| 输入与像素 | 只发送RG-F1合成服务/路径/时间/ID和功能、整体风格，未发送运行图/批准图/代码/账户/秘密，也未给位置、相邻关系或分区版式。Lead在本地看了r4/state-r2原图；r1真实覆盖暂停、确认与未知同一工具。它不是运行界面，1536画布不是DIP窗口。 |
| G2 已批补充 | [精确补充及已批实施顺序](<docs/design-system.md#2026-11-04--恢复状态工具-g2-候选-r1r87>)要求确认/未知保留目标名与root、删解除旁Play和装饰副标题/重复未知句、去生成纹理/重字；同一工具切状态，沿现有纯颜色焦点/无外框/桌面范围；Core typed/pending/回执协调→GUI工具与入口→分项真实验收。该图片、补充和范围均已集中批准，不再重复申请同一范围。 |
| 复核 | 通用角色只读定位缺口，视觉角色看图给补充线索；Lead抽查并保留有效重复文案建议，不采纳把语义圆形图标当禁圆环图表、或声称已证实动作居中的错误解释。所有状态来源/权限仍由Core/Agent约束，不从图推业务。 |
| 实现 / 验收 | **未改App/Core/Agent/CLI或测试，未构建、未启动测试/后台/真实工作负载。** 图像仅用于G2，VIS/FLOW/DATA/ENG实现验收均未开始；[批准产物终检](<docs/visual/sa-20261104/recovery-gui-g2-r1.final-audit.json>)确认图片身份/批准、设计链接与diff-check0；R86的99项选择输入仍0漂移，不借其回归替新GUI。未产生需回收的测试进程或临时root，交付PNG与设计记录保留，不安装/发布/提交。 |
| 接续 | 已具G1/G2/最小实施授权，下一片直接按G1 RG01–06接Core类型化共享契约与未决持久化/回执核验，再接窗口与入口，按真实隔离双端/键鼠/像素/双配置分别验收。不改Agent/Core恢复策略、不新增RPC或CLI语法，不重复审批已批r1。整体目标仍开放。 |

## 2026-11-04 — CLI 配置校验人读差异（R86，整体 G3 仍未完成）

本片补[已批CLI人读](<docs/architecture.md#cli-与共享数据契约g1-r1-已批准>)及[既有文档校验语义](<docs/architecture.md#L864-L876>)，产品仅改[CliOutput](<src/SystemAutorun.Cli/CliOutput.cs>)，新增[CliConfigurationTextTests](<tests/SystemAutorun.IntegrationTests/CliConfigurationTextTests.cs>)。原`config validate --file PATH/-`已经共享校验并返回diff，但人读changes只为最长2000字符JSON转储，不能直接区分增/删/变名单与unknown。没有命令、选项、RPC、DTO、Core校验或持久化流程、权限和GUI变更；G2/VIS为N/A，不据此关闭桌面余项。

| 类别 | 实际范围与边界 |
| --- | --- |
| DATA — 精确复用返回值 | 只识别完整schema1 ConfigurationValidationResult的人读形状，列出Agent原有任务新增/移除/变更名单与各自数量、settingsChanged、候选SHA256/字节数、有效候选任务数（invalid为unknown而非Agent失败占位0）、capturedAt及观察到的workspace revision。来源scope取原JSON，不用本地computed getter替换；reason/error为原值，无新warnings数组。没有逐属性old/new值，CLI不重算diff、读磁盘或重载；保留原列表顺序。 |
| 空 / 无效 / 不可比 | 有效且三列表为空、settingsChanged=false时仅称“no listed known-field changes”，不称字节相同或完整验证。invalid候选、changes=null、Available=false明确为unavailable，不把不可比当空；不可用数组即使含cache也不当当前增删名单展示，reason/error保留。unknown/partial/malformed/future形状沿旧raw呈现，不添protocol错误或更改exitcode。configuration_error的invalid数据在原stderr诊断后专用展示；其它失败不冒充valid摘要。 |
| 范围 / 风险说明 | 原scope明确未知字段不作语义校验、override参与且保留、独立preferences保留但不验证。footer明确这是known-field摘要而非byte/text diff；只描述捕获点，不预留修订、不授权apply，不探测目标或运行工作负载，后续需另行review/revalidate及确认。空diff不表示注释/未知字段/顺序/override遮蔽前的文本未变。 |
| 输出 / 兼容 | 专用人读的动态字符串复用SafeException，过滤控制、Unicode Format/LineSeparator/ParagraphSeparator，截断明确指向json；每一返回名字均列出，单名字仍有160字符显示上限，scope/error/reason1000上限，不再截断整个名单为2000字。JSON最前分支和整个envelope序列化原封不动，6类有效/无效/未来/残缺结果逐字符核对；元数据与未知扩展原值保留。非本切片既有raw/其它文本分支不重构。 |
| 红例 / focused | [原产品29例13失败/16通过](<docs/visual/sa-20261104/r86-config-text/before-fix.txt>)复现缺专用摘要、长名单截断及人读动态串未走安全呈现；随后另补“无关失败不冒充valid”用例，合计30例，[focused全过](<docs/visual/sa-20261104/r86-config-text/focused.txt>)、构建0 warning/error。覆盖typed/wire、来源scope、空/不可比/无效、10种形状fallback、6种JSON原envelope精确保持、160项名单、长串/控制/双向文字。 |
| FLOW / DATA — 隔离真集成 | 复用[控制面夹具](<tests/SystemAutorun.IntegrationTests/ConfigurationControlPlaneTests.cs#L176-L227>)，独立Console PE经CurrentUserOnly Pipe到真实Dispatcher/Workspace；工作负载执行器/探针均抛错禁止调用。UTF-8 file（含BOM、注释、混合换行/未知字段）、中文名字，三类任务diff与settings变化；stdin的重排/未知字段/override遮蔽产生无列出差异但候选hash不同；invalid候选/损坏原文分别exit2与合法候选但diff不可比。各旅程在初次configuration-document读取完成后取文件名/mtime/内容哈希基线，再验证原配置/override/现存文件均保持，无backup/apply/回执，fixture仍存活并校验ping。既有读取/validate都会取得RootWriteLease（空root可能新建锁文件），这不是“无锁/无文件系统写”；本片不改原协调机制，指纹不证明初始锁文件从未创建。仅temp root；这些是文档真实集成，不冒称workload/远程/生产或原生UI验收。 |
| 独立复核 | 安全角色未发现新增安全/形状必修，但核对出invalid候选的TaskCount=0是服务端失败占位，不能当真实0任务呈现；[单测/真PE负例2失败、另2通过](<docs/visual/sa-20261104/r86-config-text/review-before.txt>)固定该事实，Lead补“unknown (candidate invalid)”，JSON仍保留Agent原TaskCount=0；另补仅settings变化、invalid/unavailable不显示empty/settings的边界，共31例。原30例矩阵已全过，[保留为审查修正前证据](<docs/visual/sa-20261104/r86-config-text/review-supersession.json>)而不替代新输入最终验收。 |
| ENG — 最终结果 | [审查修正后的双配置30步矩阵](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/steps.json>)全部exit0；Debug/Release均0 warning/error，各 **1471/1471、0跳过**。每配置Core729、CLI374（原343＋新增31）、配置文档/控制面26、Monitor/Dashboard30、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/test-summary.json>)及[合计](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/totals.json>)保留，逐testName确认31新增两配置均全过。这仍是筛选矩阵而非integration全集/整体G3通过，GUI84自动回归不代替原生旅程。 |
| 保留 / 清理 | [继承审计](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/existing-source-audit.json>)保留93项继承输入、5项定向核对的配置依赖及原formatter，仅1个旧产品文件声明变更、97旧项不变，再加新测试共99项；[源终检](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/source-audit.json>)与[清理](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/cleanup-after.json>)确认99项0漂移，相关进程、私有HKCU/HKLM键、夹具service、SystemAutorun临时候选均0，diff-check0。仅按精确绝对路径、普通非reparse、空内容确认清理共享测试父目录，未删未知数据或杀无归属进程。原30例矩阵、所有红例及原副本均保留，Lead复核[最终产品差异](<docs/visual/sa-20261104/r86-config-text/matrix-accepted/diff-CliOutput.cs.patch>)；无DTO/校验/写入/GUI变更。 |
| 继续开放 | 专用config-diff人读不等于整体parity/G3完成；GUI恢复暂停入口、R80/R82原生输入/DPI/VIS及原有自启reader等限制继续开放。未安装、发布、提交、操作真实Run/Boot。 |

## 2026-11-04 — CLI monitor 按服务筛选（R85，整体 G3 仍未完成）

本片补[已批按服务筛选](<docs/architecture.md#cli-与共享数据契约g1-r1-已批准>)，只改[参数约束](<src/SystemAutorun.Cli/CliArguments.cs>)与[CLI接线、局部投影及help](<src/SystemAutorun.Cli/CliApplication.cs>)，新增[隔离用例](<tests/SystemAutorun.IntegrationTests/CliMonitoringFilterTests.cs>)。没有GUI交互/视觉变化，G2/VIS为N/A；不以CLI证据关闭桌面余项。用法是 `monitor status --task NAME` 或 `monitor watch --task NAME --count N --interval SECONDS`，可配 `--json`；原 `monitor history NAME` 和 `logs --task NAME` 不变。

| 类别 | 实际范围与边界 |
| --- | --- |
| DATA — 既有采样的局部视图 | `--task` 为OrdinalIgnoreCase完整名称，不做substring、通配符或隐式Trim。仅在response.Ok时投影schema1快照，每帧重新按返回任务定义用**共享MonitoringTaskIdentity.For**核对process/runtime身份，防同名旧目标混入；无相应定义时不关联进程/runtime。只替换tasks.data、processes.data、runtime，断开JsonNode树保留未知根/组/行字段；Agent/root/host/persistence与组Available/ObservedAt/AttemptedAt/Error不变，group元数据仍描述原全局来源。它不是当前磁盘配置查询，不加configuration RPC、不复采，不在CLI重算健康或uptime。 |
| 空 / cache / 不兼容 | 无匹配是空view，继续watch且exit0；既不证明存在/不存在，也不表示健康或时长0。来源不可用时原诊断cache和flags保持，原null保持null，不转not_found；可读输出/helper与help说明这些限制。不能安全投影（schema/数组/名字/重复选中定义/身份字段缺失等）则protocol_mismatch/exit3、data=null，保留requestId与revision，不冒充空成功或回退全量。无`--task`的数据路径保持；显式投影保留JSON值与schema，不保证原字符转义形式相同。 |
| 输入 / 权限 | status/watch白名单只允许root/json/timeout/non-interactive/task与watch的count/interval；yes/file/revision/operation-id/export及其它不适用参数在连接前exit2。--task不外发为Name/payload，不新增路由/RPC/DTO/Agent/权限/采样行为；不自动启后台、UAC或启停工作负载。Agent错误不尝试投影，原error/exit行为保持。 |
| 红例 / 根因 | 首次新测试[漏using System.IO导致编译失败](<docs/visual/sa-20261104/r85-monitor-filter/before-build.txt>)，仅补测试import；随后[原产品39例27失败/12通过](<docs/visual/sa-20261104/r85-monitor-filter/before-fix.txt>)复现参数未支持。实现后[38通过/1失败](<docs/visual/sa-20261104/r85-monitor-filter/after-fix.txt>)：测试错误比较日期中的`+`与合法等价`\u002B`原字面，改为严格JSON结构/值相等，同时仍核对未知字段和原对象不变。补health/runAs缺失的身份负例。 |
| 真实Console解码取证 | [首轮矩阵](<docs/visual/sa-20261104/r85-monitor-filter/matrix/steps.json>)停在debug-cli（333通过/1失败，未冒称整体通过）；[完整诊断](<docs/visual/sa-20261104/r85-monitor-filter/unicode-diagnostic.txt>)显示正确Unicode选择与01:00:10数据均存在，但管道捕获把UTF-8中文解为GBK乱码。CLI入口原已显式UTF-8，故只给测试ProcessStartInfo stdout/stderr指定严格UTF-8，保留中文原断言，不改产品或跳过测试。[失败后来源审计](<docs/visual/sa-20261104/r85-monitor-filter/failed-matrix-followup.json>)确认仅新测试改诊断/decoder，产品冻结输入未变。 |
| 独立审查处置 | 安全角色发现仅查health顶层键仍可把缺失timeoutSeconds等子字段反序列化成默认值，导致静默空成功而非既定protocol_mismatch。该项按必修处理，[7个已对齐目标身份的health字段缺失负例全部复现exit0而非3](<docs/visual/sa-20261104/r85-monitor-filter/review-before.txt>)，另加2个process来源不可用/null-cache用例；41例矩阵保留但不能作为修复后的最终证据。Lead补齐非null health的7个已知字段形状闸，合法health:null与未知子字段保留，复用原typed反序列化及共享identity；不采用会丢弃合法未知子字段的全对象roundtrip比较。最终50例两配置全过，审查项已关闭；Lead复核[产品接线/投影差异](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/diff-CliApplication.cs.patch>)及[参数差异](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/diff-CliArguments.cs.patch>)。 |
| FLOW / DATA — 集成范围 | 新夹具是独立Console PE→CurrentUserOnly Pipe→真实Dispatcher/AgentMonitor；用3个合成任务（含近似名称、中文及空格）与受控时钟预备2次采样，禁用历史写入、不运行周期循环。覆盖全量/单项JSON、中文人读、2帧JSONwatch、空筛选与失鲜cache、未复采/文件集mtime及内容不变、无Journal/回执与ping存活；Source、进程/主机数据均为合成，不冒称真实工作负载/资源验收。Fake transport另覆盖动态下一帧移除、旧身份、error passthrough、未知字段/原对象不变；未实测Ctrl+C或断连。最终该旅程与其它新增用例均在两配置通过。 |
| ENG — 最终结果 | [修复审查项后的双配置28步矩阵](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/steps.json>)全部exit0；Debug/Release均0 warning/error，各 **1414/1414、0跳过**。每配置Core729、CLI343（原293＋新增50）、Monitor/Dashboard30、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/test-summary.json>)和[合计](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/totals.json>)保留，并按testName单独确认50新增全部通过。仍是筛选矩阵，不是integration全集或整体G3通过，GUI84仅自动回归不代替原生旅程。 |
| 保留 / 清理 | [继承审计](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/existing-source-audit.json>)确认92旧输入仅2项声明变更、90保持，再加新测试冻结93项；[终检](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/source-audit.json>)与[清理](<docs/visual/sa-20261104/r85-monitor-filter/matrix-accepted/cleanup-after.json>)确认93项0漂移，相关进程、私有HKCU/HKLM键、夹具service、SystemAutorun临时候选均0，diff-check0。仅清理精确绝对路径、非reparse且为空的共享测试父目录，未杀无归属进程/删未知目录。[41例矩阵被审查修复取代的说明](<docs/visual/sa-20261104/r85-monitor-filter/review-supersession.json>)保留其真实通过结果与新发现边界，原副本/所有失败证据均保留。 |
| 继续开放 | 按服务快照筛选实现不等于整体parity完成；config-diff专用文本、GUI恢复暂停入口、R80/R82原生输入/DPI/VIS等原余项继续开放，R83 reader限制不变。未安装、发布、提交或真实Run/Boot写入，整体G3不关闭。 |

## 2026-11-04 — CLI uptime / 历史文本呈现（R84，整体 G3 仍未完成）

本片沿[已批 CLI 人读输出与 uptime 语义](<docs/architecture.md#cli-与共享数据契约g1-r1-已批准>)，只修改[文本呈现](<src/SystemAutorun.Cli/CliOutput.cs>)并新增[隔离测试](<tests/SystemAutorun.IntegrationTests/CliMonitoringTextTests.cs>)。原文本虽保留原始嵌套数据，但 runtime 仅为最长2000字符的 JSON 转储，不直接显示两类持续时长；历史百分比也缺少覆盖率/unknown与SLA的可读解释。本片不新增命令、选项、筛选、RPC、DTO、采样、权限或GUI交互；现有G1覆盖此人读能力，无桌面视觉变更，G2/VIS为N/A。服务筛选等剩余parity不在本片冒充完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| 状态 / watch | 只替换 schema1 的runtime人读分支：每行保留task/identity/observedAt、process source/start/identity、health source/since，显示process-uptime与continuous-health，均截至**该行观测时间**，不按CLI当前时钟增长。必须有可用任务组与相符时间；进程时长还须windows类型、可用进程组、明确原生来源及启动身份；windows_service/WSL不借共享宿主启动时间。禁用、失败、未观察、未来时间、未知来源等不显示正健康时长，unknown不变成0，测得0仍是0；不在CLI重建跨周期continuity。其它组原available/observedAt/attemptedAt/error与诊断cache保持原输出，footer明确不可当实时健康。 |
| 历史 / JSON | 历史文本直接呈服务端availabilityPercent、coveragePercent及healthy/fault/unknown秒数，保留窗口、identity、latest/retained时间、truncated与warning；不重算比率，也不冒称精确SLA或服务自身uptime。区间与bucket给计数，并提示JSON取完整数据。JSON/NDJSON仍在任何新解析前序列化原信封；原数据、metadata和错误/退出码策略不变。完整形状且schema1才用新呈现，不识别/缺字段/未来schema回退旧原始metadata，不让缺失数字字段变默认0；新动态字符串复用控制/Cf/行分隔清洗与截断提示。 |
| 红例 / 审查处置 | [原产品27例中26失败、1旧形状回退通过](<docs/visual/sa-20261104/r84-monitor-text/before-fix.txt>)，复现缺少可读持续时长与历史语义，不冒称原JSON缺业务数据。[首轮26文本通过/1夹具失败](<docs/visual/sa-20261104/r84-monitor-text/after-fix.txt>)：夹具漏必需health，[诊断快照](<docs/visual/sa-20261104/r84-monitor-text/fixture-diagnostic.txt>)中后台正确unavailable/runtime空；补合成health而不改服务端。接着[29通过/1夹具失败](<docs/visual/sa-20261104/r84-monitor-text/final-focused.txt>)暴露独占writer.lock不能普通读取：仍比较该0字节租约的文件名/长度/mtime，空内容由长度0证明，其余文件SHA256，不跳过文件/不改变租约共享。安全角色另指出历史To超mock now，按真实契约把时钟推进窗口末端、放在watch断言之后；补完整schema2回退和四组原metadata断言。只读审查未见新增产品安全/事实阻断，Lead复核[冻结差异](<docs/visual/sa-20261104/r84-monitor-text/matrix/diff-CliOutput.cs.patch>)并负责改码/实测。 |
| FLOW / DATA — 真Console、隔离数据 | 新夹具用独立Console PE→CurrentUserOnly Pipe→真实Dispatcher/AgentMonitor/MonitoringHistoryStore；只由合成Source和可控时钟主动准备2次采样，不运行采样循环、不访问宿主监测/真实工作负载/探针。读取现成结果，覆盖status、2帧文本watch、2行JSONwatch、历史文本/JSON与直读服务端结果核对、31秒后失鲜转runtime空；查询前后采样调用数不增，根内文件集/长度/mtime/内容不变，未新增回执/Journal，末尾ping仍通且server errors空。正例数据全部合成，不冒称真实服务uptime或真实资源验收；未实测Ctrl+C/断连。 |
| ENG — 最终结果 | [双配置28步矩阵](<docs/visual/sa-20261104/r84-monitor-text/matrix/steps.json>)全部exit0，Debug/Release各0 warning/error、各 **1364/1364，0跳过**。每配置Core729、CLI293（原261＋新增32）、Monitor/Dashboard30、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84；[逐TRX摘要](<docs/visual/sa-20261104/r84-monitor-text/matrix/test-summary.json>)与[合计](<docs/visual/sa-20261104/r84-monitor-text/matrix/totals.json>)保留。按testName单独核实32新增用例全部通过，包括独立Console旅程；明确这是筛选矩阵，不是integration全集或产品G3总通过，GUI84仅作原功能自动回归。 |
| 源保留 / 残留 | [原输入审计](<docs/visual/sa-20261104/r84-monitor-text/matrix/existing-source-audit.json>)证实继承91项仅1项声明变更、其余90项保持，再加新增测试冻结92项；原副本及所有失败日志保留。[冻结终检](<docs/visual/sa-20261104/r84-monitor-text/matrix/source-audit.json>)与[清理记录](<docs/visual/sa-20261104/r84-monitor-text/matrix/cleanup-after.json>)确认92项0漂移；相关进程、私有HKCU/HKLM键、夹具service与SystemAutorun临时候选均0，diff-check0。只删除精确绝对路径复核、非reparse且为空的共享测试父目录，没有杀无归属进程或删除未知目录。 |
| 继续开放 | R83只读例外与reader限制、R80/R82的原生输入/DPI/VIS余项、GUI恢复暂停入口、monitor按服务筛选、config-diff专用文本及其余整体parity继续开放。无安装、发布、提交、真实Run/Boot写入，未关闭整体G3。 |

## 2026-11-04 — 旧异常聚合 CLI 只读对等（R83，整体 G3 仍未完成）

本片沿[已批完整 CLI 业务对等范围](<docs/architecture.md#sa-20261031--简化-gui完整-cli-与服务监测>)补旧 GUI 聚合入口；[日志 r2 保留条款](<docs/design-system.md#2026-11-03--日志-g2-候选-r2r58>)继续有效，不删除旧页、不冒称共享 LogViewer 已统一此 reader。只改[CLI 参数](<src/SystemAutorun.Cli/CliArguments.cs>)、[CLI 接线及 help](<src/SystemAutorun.Cli/CliApplication.cs>)、[文本输出](<src/SystemAutorun.Cli/CliOutput.cs>)，新增[隔离测试](<tests/SystemAutorun.IntegrationTests/CliExceptionLogTests.cs>)；Core DTO、Agent/reader、GUI、权限和原有 self/pending 保护不变。没有桌面交互/视觉变更，G2/VIS 为 N/A，不以 CLI 证据补关此前 UI 缺口。

使用已运行的 Agent：`sautorun --root DIR logs exceptions [--level warning|error|critical] [--search TEXT] [--json]`。这是旧 `exception-logs` 的单次只读查询，不是共享 `logs` 的全源搜索或分页。

| 类别 | 实际结果与边界 |
| --- | --- |
| 接线 / 同义筛选 | 独立 route 放在 `logs` 前，沿现有[共享 DTO](<src/SystemAutorun.Core/Models/MonitoringSnapshots.cs#L49-L67>)与[原 RPC](<src/SystemAutorun.Agent/AgentCommandDispatcher.cs#L166>)读取。仅允许只读传输选项及本地 level/search；后两者按[旧 GUI](<src/SystemAutorun.App/ViewModels/MainWindowViewModel.cs#L1248-L1270>)的级别与 Source/Message/FileName 文本匹配，只过滤已返回至多200条的 Entries，不改变 SourceCount/Truncated/CapturedAt。不带 name/payload/revision/operationId，不启动 daemon/UAC，不做重试或回执查询。 |
| 红例 / 边界 | [原产品40例](<docs/visual/sa-20261104/r83-exception-cli/before-fix.txt>)中18失败、22选项拒绝已通过，缺入口统一报输入错误；[首轮接线40/40](<docs/visual/sa-20261104/r83-exception-cli/after-fix.txt>)。随后补missingLevel防默认Warning、文件数/锁文件/旧daemon unknown_command与原文保留测试。必需快照/条目字段、空值、枚举、负计数等失败返回 protocol_mismatch/exit3、data=null并保留requestId。22类非法选项在连接前exit2；后端权限/超时/旧命令不支持出口用注入response验证，不冒称真实ACL或计时验收。 |
| FLOW / DATA — 隔离真实链路 | 新增用例用独立 Console PE→CurrentUserOnly Pipe→真实 Dispatcher/Orchestrator/ExceptionLogReader→GUID私有root文件；NeverRun/NeverProbe阻断任务执行/健康探针，没有常驻采样。覆盖普通多源/排序、205条取200、33文件取32、共享锁文件返回尝试数但无 available、空结果、筛选后不再取更早数据、人读/JSON输出；与GUI使用同一RPC DTO核对。根内文件名/字节/mtime前后不变、无新Journal/回执文件、后台未停止且可ping，日志样例全部合成。未读取真实日志、启动发行宿主或做原生Run/Boot写测。 |
| 文本 / 旧通道限制 | 专用文本分支展示capture/sourceCount/entries/truncated与逐条事件；清C0/C1/Cf及行分隔，超长明确提示用JSON，JSON保留原字符串。help/footer明确：文件名扫描来自root/log_dir而非log_sources；32文件/240行/200条是保留结果上限，旧reader仍可能扫整文件，启发式丢普通行、吞文件读错误并可能回落mtime，truncated仅报告文件/条数上限。因此空/false不证明健康、可用或完整；未擅自修reader或新增DTO字段，旧日志工具/详情继续保留。 |
| 审查与误报处置 | 通用角色做只读跨目录映射；安全角色前/后审，后审以预改副本对当前3份实现比较，未发现新增阻断；Lead复核[参数差异](<docs/visual/sa-20261104/r83-exception-cli/diff-CliArguments.cs.patch>)、[接线差异](<docs/visual/sa-20261104/r83-exception-cli/diff-CliApplication.cs.patch>)与[输出差异](<docs/visual/sa-20261104/r83-exception-cli/diff-CliOutput.cs.patch>)，负责改码/实测。第一矩阵[260通过/1失败](<docs/visual/sa-20261104/r83-exception-cli/debug-cli.txt>)为新增bidi测试的文化比较误报：干净ASCII串对U+202E的CurrentCulture查找仍报index0，[独立诊断](<docs/visual/sa-20261104/r83-exception-cli/bidi-assertion-diagnostic.json>)的Ordinal为-1。改为显式Ordinal保留字面码点拒绝断言，未改产品或放松过滤；原失败/TRX保留，[重跑审计](<docs/visual/sa-20261104/r83-exception-cli/rerun-audit.json>)证明只变该测试。 |
| ENG — 最终双配置 | [独立重跑26步](<docs/visual/sa-20261104/r83-exception-cli/matrix-final/steps.json>)全部exit0，Debug/Release各0 warning/error、各 **1302/1302，0跳过**。每配置Core729、CLI261（原197＋新增44＋共享日志控制面20）、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84，见[逐TRX摘要](<docs/visual/sa-20261104/r83-exception-cli/matrix-final/test-summary.json>)；44例新增用例均单独按TRX testName核实通过。明确筛选矩阵，不是integration全集或产品G3总通过；GUI84只作旧功能自动回归，不补原生输入/VIS现场证据。 |
| 源保留 / guard | 首次[guard拒绝](<docs/visual/sa-20261104/r83-exception-cli/guard-diagnostic.json>)在构建前发生：继承89输入原本未含CliOutput，而非发现额外改码；用本片开始即保存的[预改输出副本](<docs/visual/sa-20261104/r83-exception-cli/before-code/CliOutput.cs>)补入原始输入到90，仅3项声明变化，其余87项保持，再加新增测试冻结91项。没有以改后代码填旧hash或绕开guard，原失败矩阵未覆盖。[最终原始输入审计](<docs/visual/sa-20261104/r83-exception-cli/matrix-final/existing-source-audit.json>)及[终检](<docs/visual/sa-20261104/r83-exception-cli/matrix-final/cleanup-after.json>)：91冻结输入0漂移，相关进程/私有HKCU与HKLM键/夹具service/SystemAutorun临时候选均0，diff-check0；仅删除精确路径核验、非reparse且为空的共享测试父目录。tests下dump的glob检查为0，既有未提交工作、失败/TRX和证据保留。 |
| 保持开放 | 本片不证明完整CLI parity或产品G3；实际Ctrl+C/物理输入、真实资源/业务日志和发行部署未验，reader既存完整性/IO预算限制未关闭。R80/R82的GUI原生输入、DPI及裁切余项，GUI恢复暂停入口、CLI uptime/config-diff专用文本等仍在原待办范围；新增GUI交互仍须受影响G1/G2。无安装、发布、提交或真实Run/Boot写入。 |

## 2026-11-04 — 监控身份区裁切修复与 CLI 覆盖复核（R82，UI / 整体 G3 仍未完成）

本片按[r4 辅助窗口保留条款](<docs/design-system.md#2026-10-31--用户批准的-r4-主界面>)及同文记录的监控裁切问题走小修复路径。只修改[监控 XAML](<src/SystemAutorun.App/Dialogs/MonitoringWindow.xaml#L260-L309>)与[既有窗口测试](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs#L57-L93>)，不改页面信息架构、其他组件、业务、焦点、IPC 或权限；不重开已批视觉方向。以下是分项证据，**不是 UI 已完成或产品 parity 总通过声明**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 根因 / 红例 | 固定82 DIP标题行减去上下内边距后容不下三行身份文字；垂直居中挤入内边距并裁掉末行，长名称还遮掉状态徽标。[未改产品上的8例全部失败](<docs/visual/sa-20261104/r82-monitor-header/before-fix.txt>)，已查看[普通内容修复前图](<docs/visual/sa-20261104/r82-monitor-header/before-images/monitoring-header-1120x700-normal.png>)和[最小窗口长内容修复前图](<docs/visual/sa-20261104/r82-monitor-header/before-images/monitoring-header-1040x650-long.png>)。没有靠跳过、缩小字体或硬编码样例掩盖失败。 |
| 最小修复 | 标题行改按内容取高并保留82 DIP下限；同排名称约束宽度、保留相邻状态徽标，名称/OS/连接省略时保留完整提示。连接辅助文字恢复已批至少12 DIP。普通内容的水平关系不变，没有新面板、边框、颜色或导航；[产品差异](<docs/visual/sa-20261104/r82-monitor-header/diff-MonitoringWindow.xaml.patch>)与[测试差异](<docs/visual/sa-20261104/r82-monitor-header/diff-MonitoringWindowTests.cs.patch>)可复查。 |
| VIS — 有限实装证据 | Lead已查看修复后全部8张运行图片，覆盖窗口自身最小1040×650、1120×700、1320×820和1620×651 DIP × 普通/长合成内容。三行完整、长名称不再挤掉状态或右侧指标，例见[最小长内容](<docs/visual/sa-20261104/r82-monitor-header/after-images/monitoring-header-1040x650-long.png>)与[宽屏长内容](<docs/visual/sa-20261104/r82-monitor-header/after-images/monitoring-header-1620x651-long.png>)，完整尺寸/哈希见[图像索引](<docs/visual/sa-20261104/r82-monitor-header/images.json>)。这些是已Show离屏HWND的WPF视觉树渲染，不是前台桌面帧，也不含原生标题栏或工作区容纳证据。[现场日志](<docs/visual/sa-20261104/r82-monitor-header/after-fix.txt>)八例WPF与GetDpiForWindow均144，即150%，不是人为图片放大；不据此关闭self工具、100%/125%、跨屏或物理DPI旅程。 |
| FLOW / DATA — 限定范围 | [修复后8/8](<docs/visual/sa-20261104/r82-monitor-header/after-fix.txt>)在真实WPF控件上断言内容上下界、名称/徽标/指标不重叠、完整提示绑定及连接信息变更后TextBlock/ToolTip同步更新。夹具App禁runtime startup、GUID root和现有隔离Pipe；本片未加载真实资源/异常数据、发self写入或操作Run/Boot。**物理悬停、提示弹出像素、键盘/焦点仍未验**；没有打开可能被WPF挪到用户屏幕的离屏popup，不用逻辑绑定冒称完整用户旅程。 |
| KFL / 保留缺口 | ALN-3/4、CON-2及此处内容容纳有上述有限证据；INT/VER完整验收保持开放。旧监控表格时间列裁切、其他旧11 DIP辅助样式、self跨owner/复制/原生键盘与完整DPI矩阵仍未关闭。没有改共享token或把整页监控VIS标过，具体处置见[审查附件](<docs/visual/sa-20261104/r82-monitor-header/review-disposition.md>)。 |
| ENG — 双配置 | 冻结后的[26步](<docs/visual/sa-20261104/r82-monitor-header/steps.json>)全部exit0，Debug/Release各0 warning/error；每配置Core729、CLI197、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI84（新增8例），各 **1238/1238、0跳过**，见[逐TRX摘要](<docs/visual/sa-20261104/r82-monitor-header/test-summary.json>)。这是沿R81的明确筛选矩阵，不是integration全集或G3总通过。 |
| 保留 / 清理 | 继承89源仅声明的2项变化，另87项不变，[预改副本/差异审计](<docs/visual/sa-20261104/r82-monitor-header/existing-source-audit.json>)保留。矩阵后89源[终检0漂移](<docs/visual/sa-20261104/r82-monitor-header/cleanup-after.json>)，相关进程、私有HKCU/HKLM键、夹具service、SystemAutorun临时候选均0，diff-check0；另以glob检查tests下dump为0。仅清理完整路径核验、非reparse且为空的共享测试父目录，未终止无关进程。原失败/TRX与图像作为证据保留。 |
| CLI复核 / 下一片 | 已有任务/配置/远程/启动项只读与纳管/self/doctor/共享日志路由不等于全旅程已验。抽查确认旧`exception-logs`聚合仍由GUI使用，但CLI无对等RPC入口，可优先补其只读对等和隔离Console→Pipe验收。**拒绝删除旧异常页或淘汰RPC的建议**：日志r2已批范围要求保留旧聚合与状态详情。GUI恢复暂停入口需受影响G1/G2；uptime及config diff专用文本是呈现候选，现有JSON可用不等于缺功能；status尾读统一需另核实读取边界。具体[逐项处置](<docs/visual/sa-20261104/r82-monitor-header/review-disposition.md>)不构成扩项许可。仍无安装、发布、提交、发行宿主或真实Run/Boot写测。 |

## 2026-11-04 — CLI 接入共享 self 恢复线索（R81，整体 G3 仍未完成）

本片按[已批 self 精确补充](<docs/design-system.md#已与-r1-一并批准的精确补充>)接闭 CLI 与 GUI 的同 root pending 保护，不重开审批或扩展权限。只修改[CLI 实现及 help](<src/SystemAutorun.Cli/CliApplication.cs>)、[既有 CLI 测试](<tests/SystemAutorun.IntegrationTests/SelfStartupCliTests.cs>)，新增[隔离协作用例](<tests/SystemAutorun.IntegrationTests/SelfStartupCliPendingTests.cs>)；CLI 语法、IPC/receipt v1、pending 格式、Windows 准入、GUI 布局和既有未提交工作保持。因没有 GUI 变化，G2/VIS 为 N/A；本片不是完整 CLI parity、真实发行宿主或产品 G3 完成声明。

| 类别 | 实际结果与边界 |
| --- | --- |
| 缺口与最小接线 | 原 CLI 只向 stderr 打印“reserved”，没有持久预留或读取 GUI pending。[原实现上的8例全部失败](<docs/visual/sa-20261104/r81-self-cli-pending/before-fix.txt>)。现在严格校验本次确认输入后，使用既有 store 原子发布并回读，成功后才公告/Flush 原 ID、构造 transport、单次发送，再走共享 ACK reader。prepare 只读检查；已有有效记录报 self_busy/exit6并保留存量原 ID，不可读或坏记录报 pending_storage_unavailable/exit3且不捏造 ID。两者均在连接前封锁，不覆盖或删除记录。 |
| 发布失败 / 发送边界 | Reserve 任意失败都不发送、不取得清理权，并严格复读磁盘；可能已发布时报告存量原 ID，不能把新候选 ID 当恢复证据。只有 Missing 加本地取消才沿未发送取消分支。工厂/诊断 Flush 前失败可证明未调用 SendAsync；进入任意注入 transport 后的原始 IO/超时/取消/权限/参数异常则一律原 ID Unknown。具体 PipeTransport 才沿既有 AgentClient attemptedWrite 分类，未知异常中的外来 ID 不能替换实际预留 ID。局部租约等待上限取 min(所选timeout,15秒)，不称全程延迟保证。 |
| 清理 / 历史 | 仅新鲜、非 replay 的成功或 not_executed，以及可证未派发，才用本次 fresh capability 单次条件清理；使用 None token，不随 caller 取消中断。清理无法核实时返回 pending_clear_unverified/exit6、原 operation/request ID，不重试、不谎报成功；失败 Data 仍隐藏，消息区分“已收 ACK”与“清理未核实”，回执由原 ID 另查。未知、其他拒绝、replay、Completed、历史 Data、现值匹配或查询均不清理。历史 replay 的既有 ACK/exit 行为兼容，但 stderr 明示 pending retained，后续 prepare/set 封锁；没有隐式预查、换 ID、重发、强制清除、自动修复或 UAC。 |
| DATA / FLOW — 新增29例 | [独立 Console 与 CurrentUserOnly Pipe 用例](<tests/SystemAutorun.IntegrationTests/SelfStartupCliPendingTests.cs>)覆盖有效/坏文件/目录分别阻断 prepare/set、发布与公告先于工厂、显式/自动 ID、6种不透明发送异常、caller取消不打断已知结果清理、交换前后诊断写失败、5种 fresh/replay/conflict 判定、替换字节/目录不能被旧 capability 清理、短租约预算、**真实请求被读取后断管**、已经打开的 GUI workflow 复读并封锁、GUI 发起的未知阻断独立 CLI，以及两个 Console 同时竞争时只有一个未决交换。查询/再提交后原 pending 字节不变。该新夹具没有 runtime、native adapter、dispatcher 或 Journal；GUI 部分是 workflow，不冒称 WPF/原生输入旅程。 |
| 既有测试保留与审查 | [预改副本及两项精确差异](<docs/visual/sa-20261104/r81-self-cli-pending/existing-source-audit.json>)可复查。6条独立 Console→真实 Agent/Pipe→同一私有 Journal/GUID 原生转换仍验证 native bytes、调用数、原 ID 和单 Journal；replay 存储比较只剔除**新增且另行校验**的精确 pending 路径，其他文件继续逐字节核对，查询后 pending 不变。5个原“pre-dispatch”桩移到实际 factory-before-SendAsync 边界，新增6例反证进入任意 SendAsync 后不能凭异常类型清锁；真实无服务器 deadline 仍验证未写出清理。[安全咨询逐项处置](<docs/visual/sa-20261104/r81-self-cli-pending/review-disposition.json>)拒绝用 terminal replay 或更多错误码放宽已批清理规则，也不为历史回执额外发送预检。 |
| 红绿与失败留痕 | 首次新并发夹具使用 Task.Result 被 xUnit1031 拒绝，保留[原构建日志](<docs/visual/sa-20261104/r81-self-cli-pending/before-fix-build.txt>)；改为 await Task.WhenAll，不禁规则。随后在未改产品上获得上述8条红例；第一次定向90/90，补足取消、诊断及外来 ID 后[最终95/95](<docs/visual/sa-20261104/r81-self-cli-pending/focused-r2.txt>)。原失败/TRX保留，不跳过或硬编码结果。 |
| ENG — 双配置 | [冻结后26步](<docs/visual/sa-20261104/r81-self-cli-pending/steps.json>)全部 exit0，Debug/Release 各0 warning/error；每配置 Core729、CLI197、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI76（self23），各 **1230/1230、0跳过**，见[逐TRX摘要](<docs/visual/sa-20261104/r81-self-cli-pending/test-summary.json>)。这仍是明确筛选矩阵，不是 integration 全集、生产 self 正向宿主或 G3 总通过。 |
| 保留 / 清理 | 继承88项中只有声明的2项变化，另外86项不变；新增1测试源后冻结89项，矩阵后[终检0漂移](<docs/visual/sa-20261104/r81-self-cli-pending/cleanup-after.json>)。相关进程、SystemAutorun临时候选、私有HKCU/HKLM键、夹具service均0，diff-check0；仅删除已核验完整路径、非reparse且为空的共享测试父目录，未终止无关进程。测试日志和冻结证据保留，不当作临时垃圾删除。 |
| 未完成 / 接续 | 保护只约束使用共享 store 的合作者，不能声称旧客户端、直接 Pipe 或恶意同账户也受此闸约束；不承诺掉电持久性、跨 root 全程独占、receipt 永久保留或坏记录自动恢复。旧/冲突 ID 与过期历史可能继续只读封锁，不自动放行。R80原生键盘/跨 owner 焦点、复制和125%/150% DPI等 VIS/FLOW 缺口仍开；监控既有裁切、全 GUI/CLI parity、uptime/log及整体 G3继续接续。没有真实 Run/Boot 写测、目标启动、发行宿主、安装、发布或提交。 |

## 2026-11-04 — self 跨 owner 模态修复（R80，G3 仍未完成）

沿[已批 self r1 补充](<docs/design-system.md#已与-r1-一并批准的精确补充>)的同 root 工具、明确确认及返回入口规则走小修复路径，不改布局、主题、IPC/pending 数据语义、CLI 语法或 native 权限。只改两项产品代码与同一 GUI 测试文件，其他未提交工作保留；**本片不是 UI 或整体目标完成声明**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 根因与修复前证据 | 使用真实 Main 菜单、主机按钮和 Monitoring 按钮的 handler 复现，[最初六例全部失败](<docs/visual/sa-20261104/r80-self-owner/before-fix.txt>)；[Win32 轨迹](<docs/visual/sa-20261104/r80-self-owner/before-fix/owner-prepared-True.txt>)显示原工具进入模态页后仍可见、但 nativeEnabled=false。WPF [ShowDialog 源码](<https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Window.cs>)会禁用线程上所有已显示且启用的窗，不仅是 Owner；原复用路径还只保留第一次 owner/入口。 |
| 最小修复 | [Main 的监控调用](<src/SystemAutorun.App/MainWindow.xaml.cs#L175-L185>)先暂藏已开的 modeless 工具，不处置其视图/准备/已确认 workflow；[同一窗口协调](<src/SystemAutorun.App/Dialogs/SelfStartupWindow.xaml.cs#L126-L160>)在监控入口显式重开时改挂当前 owner 并重新绑定尺寸通知。未在监控重开时，正常返回或弹窗构建抛错后恢复原窗，以 ShowActivated=false 避免主动激活。关闭按最新入口安排焦点，仅在该 owner 可见/活动/可用时执行；调度失败不能跳过清理和单窗表移除。没有调用 native EnableWindow、强抢前台或重建另一份确认视图。 |
| FLOW / DATA — 隔离回归 | [六例修复后通过](<docs/visual/sa-20261104/r80-self-owner/after-fix.txt>)覆盖 prepared/unknown/submitting × 从监控重开/不重开；断言同一窗口/模型/准备实例，旧 owner 尺寸变动不再控制工具，新 owner 尺寸更新生效，模态正常返回先于 STA 清理。未知/在途原 ID 与 pending 字节不变，只有显式确认的 submitting 例各一次 set；关闭新 owner 不取消已确认提交。另补弹窗构建异常恢复与实际菜单入口正常开关，共新增8例，见[测试](<tests/SystemAutorun.IntegrationTests/SelfStartupGuiTests.cs#L230-L414>)。这些是合成 CurrentUserOnly Pipe，不是生产 Journal/native GUI 写测。 |
| VIS — 已看实际窗口 | 已查看 PrintWindow 的[初始工具](<docs/visual/sa-20261104/r80-self-owner/tool-initial.png>)、[主屏打开的确认页](<docs/visual/sa-20261104/r80-self-owner/tool-prepared-main.png>)、[监控重开的确认页](<docs/visual/sa-20261104/r80-self-owner/tool-prepared-monitoring.png>)、[取消后监控](<docs/visual/sa-20261104/r80-self-owner/monitoring-after-cancel.png>)及[正常返回主屏](<docs/visual/sa-20261104/r80-self-owner/main-returned.png>)。100% DPI、主屏1320×820、确认工具720×504；重挂前后同一 HWND，确认页 PNG 逐字节相同，见[图像身份](<docs/visual/sa-20261104/r80-self-owner/images.json>)。长根换行、确认/取消、技术展开及既有颜色焦点没有新增裁切或操作外框；没有凭图片推导新的对比度或完整 DPI 通过。监控原有连接信息末行裁切仍保留，不将整页监控 VIS 标过。 |
| FLOW — 后台语义入口 | 独占测试进程中实际通过 UIA Invoke 依次打开设置菜单/self、准备、Main 主机按钮、Monitoring self、取消、返回、收起主屏；观察同一 HWND 重开后 native/UIA 均可用，回到主屏后主屏重新可用。[线端记录](<docs/visual/sa-20261104/r80-self-owner/native-entries-after.json>)只有一次 status 与一次 prepare、没有 set，最终 Ready。此路径不是直接调用 helper，但**UIA Invoke 不是物理鼠标/键盘**，不冒称原生焦点恢复。测试 App 禁用 runtime startup，仅在测试进程通过私有字段绑定 GUID 根，没有增加生产目标覆盖 API。 |
| FLOW / KFL — 未验 | 尝试激活主屏被 Windows foreground lock 拒绝后，不发鼠标/键盘、不再次激活或换机制绕过。UIA 的 HasKeyboardFocus 只记逻辑状态；原生 Tab/Shift+Tab/Escape、跨 owner 焦点恢复、复制成功/失败、标题手势、125%/150% DPI 仍未验。INT-8/9 的模态与 modeless 衔接有上述分项证据，INT-3/5/VER-4/6 不据构建或 UIA 旗标宣称全过，详见[审查处置与 MCP 观察索引](<docs/visual/sa-20261104/r80-self-owner/review-disposition.json>)。不读取或更改用户剪贴板。 |
| ENG — 双配置与终检 | [冻结后26步](<docs/visual/sa-20261104/r80-self-owner/steps.json>)全部exit0；Debug/Release各0 warning/error，每配置 Core729、CLI168、pending crash4、workflow7、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI76（self23），各 **1201/1201、0跳过**，见[逐TRX摘要](<docs/visual/sa-20261104/r80-self-owner/test-summary.json>)。这是筛选矩阵，不是integration全集或G3总通过。继承88源仅声明的3项变化、另85项不变，[预改副本/差异](<docs/visual/sa-20261104/r80-self-owner/existing-source-audit.json>)可复查；最终88源0漂移，[终检](<docs/visual/sa-20261104/r80-self-owner/cleanup-after.json>)相关进程、SystemAutorun临时候选、私有HKCU/HKLM键、夹具service均0，独立glob检查tests下dump为0，diff-check0。六张自有工具截图已归档、精确临时文件与GUID探针已[核验清理](<docs/visual/sa-20261104/r80-self-owner/probe-cleanup.json>)；未终止无关进程。既有[修复前红例](<docs/visual/sa-20261104/r80-self-owner/trx/before-fix.trx>)保留，不改断言掩盖失败。 |
| 仍需接续 | 原生 UI/DPI 缺口等待可用前台环境再验，不重复强抢焦点；期间可继续已批 CLI 对同一 GUI pending 的跨进程保护及未知只读契约，不能把现有 CLI 回归通过当成该接线已完成。总体 GUI/CLI/uptime/log 目标继续进行。无真实 Run/Boot、发布宿主、安装、发布、提交或无关进程终止。 |

## 2026-11-04 — self 按需登录启动工具（R79，G3 仍分项待验）

本片按已批 G1/G2 接入[按需窗口](<src/SystemAutorun.App/Dialogs/SelfStartupWindow.xaml>)和[视图模型](<src/SystemAutorun.App/ViewModels/SelfStartupViewModel.cs>)，不重开既有审批，不扩大 native、包准入、Journal、Boot 或 CLI 语法。两处入口已经替换，但下面的测试与现场观察**不等于本页或整体 G3 完成**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 范围与入口 | 主菜单和监控页改为“登录启动…”，统一调用同 physical-root 工具；App 不再启动时读取旧 self bool，也不再由本地 SetSelfStartup 直写 Run。[八项既有文件差异与预改副本](<docs/visual/sa-20261104/r79-self-window/existing-source-audit.json>)分别保留，其他设置、Boot、主屏和监控业务不重排。实际两个入口、跨 owner 模态场景与返回焦点尚未完成旅程验收。 |
| 明确确认与关闭 | 意向只发只读 prepare；仅 ConfirmCommand 把本次准备的 Confirmed 升为 true，原 ID 仍先持久化后单次发送。准备归属使用对象引用，而非可相等的 record 值；Core 新增锁内 TryDiscardPreparation，仅丢弃同一未确认实例，输给提交竞争时返回 false，不取消发送。Closed 兜底停表、解绑 owner、取消并等待只读工作；已确认 workflow 不使用 view token，也不随关窗释放。Closed 的同步失败不能跳过单窗表移除，清理任务另行观察。 |
| 登记真实性 | 独立读取 startup-self-status，不读 ACK.Data 或历史响应。准备时前态与独立读取时间分别标注；新意向使旧读数失效，generation 拒绝迟到初读覆盖较新状态。读取不可用保留未核对，不将“命令返回”写成“已验证登录启动”。 |
| DATA / FLOW — 隔离用例 | [Core35/35](<docs/visual/sa-20261104/r79-self-window/focused-core.txt>)含新增的值相等副本不能丢弃原计划、丢弃输给确认提交两例；[窗口/视图15/15](<docs/visual/sa-20261104/r79-self-window/focused-gui-r4.txt>)覆盖显式确认、反证 ACK、登记读取失败、初读迟到、准备关闭/迟到、直接关窗与 owner 销毁不取消在途提交、重开共享 ID、6类原 ID 观察、坏 pending 无捏造键、单 root 窗口复用与实际几何。后者使用真实 WPF 和 CurrentUserOnly Pipe，但服务端数据为合成夹具，**没有 native Run/Boot 适配器**；不冒称生产 Journal/native GUI 成功旅程。 |
| VIS — 已查看的像素 | 已查看 100% DPI 的[640 DIP 确认页](<docs/visual/sa-20261104/r79-self-window/screenshots/confirm-1120x700.png>)、[宽 owner 下确认页](<docs/visual/sa-20261104/r79-self-window/screenshots/confirm-1620x651.png>)、[未知页](<docs/visual/sa-20261104/r79-self-window/native-unknown/initial.png>)及[未找到回执](<docs/visual/sa-20261104/r79-self-window/native-unknown/not-found.png>)。三个 owner 尺寸下实际工具宽度为640/720/720、[几何与实际 DPI](<docs/visual/sa-20261104/r79-self-window/screenshots/geometry.json>)单列；测试模拟用户 resize 时显式切 WPF Manual 模式，不能仅据 requested Width 认定成功。首轮空提示占位及技术栏实心背景已经最小修正，旧图保留在证据目录。125%/150% DPI、未知页完整尺寸矩阵和所有焦点控件尚未通过 VIS。 |
| FLOW — 现场已做 | 私有进程中实际鼠标准备；窗口根持焦时 Enter 未提交；显式聚焦确认后空格提交，正常返回后显示独立“未登记”，而不是夹具 ACK 的 registered=true。[线端摘要](<docs/visual/sa-20261104/r79-self-window/native-confirm-r2/after.json>)只有一次 set、两次独立 status，pending 已清。未知页原 ID 查询后仍显示“结果未知”，只一次 operation 请求，[前后 pending SHA 与 ID 不变](<docs/visual/sa-20261104/r79-self-window/native-unknown/after.json>)，没有 status/prepare/set。Escape 与关闭均完成，所有自有探针进程退出、目录随后验证清理。 |
| FLOW — 未通过部分 | 原定时器在 inactive 时消费状态切换、以及 Window 自身被视为有效控件，导致安全焦点遗漏；现在保留待补焦点并排除 Window 自身。最后一次原生复验遇 Windows foreground lock，拒绝的鼠标/Tab 调用均未发送输入；不强抢用户前台，不将逻辑焦点测试称为原生修复通过。完整 Tab/Shift+Tab、两真实入口/跨 owner 复用/返回焦点、复制成功/失败及系统标题手势仍待实测；本片未读取或更改用户剪贴板。见[现场观察与明确缺口](<docs/visual/sa-20261104/r79-self-window/native-observations.json>)。 |
| KFL 分项 | ALN/REU 的三桌面 owner 几何、MIN/CON/STA 的确认与未知文案、RAD/COL/ICO/TYP/SCR 的部分100%像素已核对；INT/VER 仅记录上述实际鼠标、空格确认、取消、查询与隔离生命周期。沿用既有纯色、零半径、图标族及颜色焦点组件，不新增 outline/ring/状态条。未把颜色组件的旧测试视为本窗口全部焦点、动画和 reduced-motion 已实测，也未把 N/A 的移动/浏览器项目补成冗余矩阵。 |
| 审查与修复 | [安全咨询逐项处置](<docs/visual/sa-20261104/r79-self-window/review-disposition.json>)保留七项 alpha 风险、最小修复、真实发现和未验边界。最初构建的 nullable 注册前态错误、夹具缺 enabled 参数错误均保留日志，修正后才运行测试；没有跳过、放宽断言或以源码代替像素。 |
| ENG — 双配置 | [冻结后26步](<docs/visual/sa-20261104/r79-self-window/steps.json>)全部exit0，Debug/Release各0 warning/error；每配置 Core729、CLI168、workflow7、pending crash4、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、GUI68，合计各 **1193/1193、0跳过**，见[逐TRX摘要](<docs/visual/sa-20261104/r79-self-window/test-summary.json>)。这是明确筛选的矩阵，不是integration全集或G3总通过。[终检](<docs/visual/sa-20261104/r79-self-window/cleanup-after.json>)冻结88项0漂移（继承81、另补3项已有输入及4项新文件）；继承81仅5项声明变化、另76项不变，全部8项修改均与预改副本比对。相关测试进程、临时候选、私有HKCU/HKLM键、夹具service、dump均0，diff-check0；只删已核验空普通目录及明确自有探针/截图，保留其他未提交工作。 |
| 仍需接续 | 完成上述 VIS/FLOW 缺口，并复核主屏已打开工具后再进入监控模态页的可用性；不能凭 helper 直接开窗称两实际入口已验。既有 CLI 仍未消费 GUI pending，跨 CLI 仲裁尚未闭合；整体 GUI/CLI/监测目标保持进行中。无真实 Run/Boot 写测，无发布宿主、安装、发布或提交。 |

## 2026-11-04 — self 同 root 工作流协调（R78，GUI 尚未接入）

本片沿已批 self 最小范围，将 R76 typed 客户端与 R77 pending 接成[同 root 工作流](<src/SystemAutorun.Core/Ipc/SelfStartupWorkflow.cs>)及[纯元数据快照](<src/SystemAutorun.Core/Models/SelfStartupWorkflowSnapshot.cs>)。尚无生产 GUI 调用方，本片不改变用户界面，G2/VIS 因此 N/A；没有把后台生命周期测试称为真实关窗/焦点证据，也未变更既有 CLI 语法、IPC、Journal 或 Windows 准入。

| 类别 | 实际结果与边界 |
| --- | --- |
| 确认与单次发送 | physical-root 工厂共享一个实例与当前状态。Open 只共享初始化任务，始终返回 Current 而非初次 Ready；每次准备先纯读 pending，再发只读 prepare。调用方须在明确确认动作中自行提供 Confirmed=true，且所有其余字段等于本次准备；Core 不自行升确认。门内生成原 ID，await Reserve 成功后才单次 typed submit；双击、提交中重开、改方向/前态/context/版本或重用旧确认均不能二次发送。 |
| known / unknown | 先捕获继承 IOException 的 tagged unknown；具体密封 AgentClient 未包装的 pre-write IO/超时/取消/Argument 才能判未发送，不能泛用于任意 transport。仅 non-replayed fresh Ok 或 not_executed 可单次清 fresh reservation，其他错误/重放成功保持未知；清理失败 self-only StorageBlocked。已确认 workflow 不持有/释放窗口资源，清理用自己的 None token完成；未来 GUI 关闭不得取消或销毁在途 workflow。 |
| 取消与存储 | 审查发现 flush 后、发布前取消被误作永久 storage fault；已修正为“Reserve抛取消、未调用client、严格同root复读Missing”才返回 NotSent/cancelled，不删任何恢复记录。发布后取消读到原记录仍 Unknown；IO/无效/不可读仍封锁且不捏造 ID。初始化无法读取的封锁在本进程保持，无Reset/查询转Missing解锁API，这是明确的可用性限制，不是自动恢复承诺。 |
| 原 ID 只读观察 | Query只用原ID调用既有receipt v1 reader，额外要求self命令/空name，分别显示未找到、Running、Unknown、结果未保留、Completed有历史响应、查询失败；只保留观察枚举，不保留/应用opaque Data、登记bool或历史响应。查询不读/清pending、不准备、不发送、不换ID；Completed与匹配当前值均不解锁。 |
| DATA / FLOW — Core33 | [实际NTFS+CurrentUserOnly Pipe用例](<tests/SystemAutorun.Core.Tests/SelfStartupWorkflowTests.cs>)验证确认前无pending/lease、发送线端看到已持久化原ID、严格确认、预取消零ID、可证连接前超时清自己的hint、actual write后断线/坏ACK留原ID、重复/迟到任务仍共享在途状态、跨实例抢占失败保留另一原ID、存储坏记录无查询键且修磁盘不解活锁、清理readonly失败仅self封锁、8种历史观察两次查询字节不变。另以R77私有FS接缝对flushed/published取消作2例；没有注入可改phase分类的transport。复验[Core33/33](<docs/visual/sa-20261104/r78-self-workflow-r2/focused-core.txt>)。这些不是原生关窗、OS焦点或真实发布宿主证据。 |
| DATA / FLOW — Agent7 | [真实Agent/Journal与私有GUID原生适配器](<tests/SystemAutorun.IntegrationTests/SelfStartupWorkflowIntegrationTests.cs>)覆盖空/缺key/已为目标的启停6态及准备后原生漂移明确not_executed。每次只一Journal、原ID/owner一致、确认前不写，成功/确定拒绝后pending已清，重复旧确认不再派发；不启动目标。见[复验7/7](<docs/visual/sa-20261104/r78-self-workflow-r2/focused-integration.txt>)。该夹具日志仍为FixtureCommand；成功生产self命令的历史查询仅有真实Pipe+合成receipt覆盖，不冒称生产Journal/native查询旅程。 |
| 既有测试失败与最小修复 | [首轮GUI53实跑](<docs/visual/sa-20261104/r78-self-workflow/debug-gui.txt>)52过/1失败：Caption反向动画等170ms后仍为中间色#FFF1F1F2而非#FFF4F4F5。生产120ms/Completed清clock逻辑未改；[唯一既有源码差异](<docs/visual/sa-20261104/r78-self-workflow-r2/diff-ColorStateAssistTests.cs.patch>)只将该测试改为最多2s等待实际HasAnimatedProperties=false并新增断言，再保留精确颜色/绑定/几何/反向检查，不放宽色差、不跳过。两种reduce-motion[复验2/2](<docs/visual/sa-20261104/r78-self-workflow-r2/focused-gui.txt>)，原失败/TRX保留。 |
| ENG — 双配置 | [R78-r2冻结后26步](<docs/visual/sa-20261104/r78-self-workflow-r2/steps.json>)全exit0，Debug/Release各0 warning/error。每配置 Core727、CLI168、新workflow7、pending crash4、typed client8、Agent30、service43、Journal50、self crash8、历史宿主16、registration62、既有GUI53，合计各 **1176/1176、0跳过**；见[逐TRX摘要](<docs/visual/sa-20261104/r78-self-workflow-r2/test-summary.json>)。这是明确筛选矩阵，不是integration全集或新self GUI验收。[终检](<docs/visual/sa-20261104/r78-self-workflow-r2/cleanup-after.json>)冻结81项0漂移，继承77仅声明的测试等待差异、另外76项不变；相关进程、临时候选、私有HKCU/HKLM键、夹具service、dump均0，diff-check0，仅删验证为空普通目录的测试父目录，保留其他未提交工作。 |
| 审查 / 范围 | [安全咨询与逐项处置](<docs/visual/sa-20261104/r78-self-workflow-r2/review-disposition.json>)未报安全blocker，修正上述取消误锁。跨CLI仲裁尚未接闭：既有CLI仍自行保留传入ID并直发，未消费GUI pending；本片只保证同进程physical-root实例及合作pending写者，不称全客户端全局单次闸。来源工厂可能被ConcurrentDictionary多次构造，但构造器不连Pipe/开任务/持租约，只有返回实例开始工作；不加公共测试/目标覆盖或UAC入口。 |

**下一片**：接已批按需窗口和独立[现场登记读取](<src/SystemAutorun.Core/Ipc/AgentStartupRegistrationClient.cs>)，收到正常ACK也要另读，失败仍显示“登记未核对”；替换[App启动bool/旧写方法](<src/SystemAutorun.App/App.xaml.cs#L87-L89>)、[主菜单调用](<src/SystemAutorun.App/MainWindow.xaml.cs#L216-L220>)与[监控调用](<src/SystemAutorun.App/Dialogs/MonitoringWindow.xaml.cs#L20-L23>)，再分别验证真实窗口像素、键鼠、焦点、owner销毁和DPI。旧direct写仍存在且尚未替换，CLI完整parity/协同与整体G3未完成，整体目标保持进行；没有真实Run/Boot、发布宿主写测、安装或提交。

## 2026-11-04 — self 原 ID 持久恢复线索（R77，GUI 尚未接入）

本片继续已批 self GUI 最小实施范围，落实 Core pending 存取，**尚无生产调用方**。它是每 root 的恢复线索，不是 Journal 所有权、确认或原生权限；不改变 IPC/receipt v1、Windows 准入、CLI 语法或旧 GUI。G2/VIS 因无界面变化 N/A，新 self GUI 的 VIS/FLOW 仍未通过。

| 类别 | 实际结果与边界 |
| --- | --- |
| 严格记录 | [本地模型](<src/SystemAutorun.Core/Models/SelfStartupPendingOperation.cs>)只存 schema1、rootId、原 operationId、方向及 UTC 预留时间；没有确认、TargetContextId、原生目标或可重放 payload。[存取实现](<src/SystemAutorun.Core/Infrastructure/SelfStartupPendingStore.cs>)限制2048 bytes，拒绝未知/重复/大小写别名/缺失字段、错误类型、非规范 UUIDv7、错 root/时间及坏 UTF8。无效/不可用返回封锁且无可查询 ID，不补造、修复或覆盖旧记录。 |
| 写前保留 | 调用方逐次确认、保留原 ID 后才 Reserve；既有合作 RootWriteLease→私有同目录 CreateNew→WriteThrough/Flush(true)→不覆盖 Move→精确字节回读，成功才返回 fresh reservation。任何失败都不允许调用方发送；发布后取消/IO 不回滚目标。只清理已经成功创建的本次临时文件，进程被杀的孤立临时不被读取、提升或自动清除。这里只测试本机进程崩溃，不承诺掉电、硬件、所有远端文件系统或恶意同账户换根的原子性。 |
| 读取 / 清理 | Read 无合作租约、无建文件/修复/清理，区分 Missing/Present/Unavailable；文件缺失必须仍属有效同 root。Read/重启没有 fresh handle，也无按 ID 清除 API。仅 fresh handle 可单次条件清理，须调用方**另外证明未发送或 fresh 确定结果**，same root/精确 bytes 不等于业务证明；未知、回执查询、Completed 或历史 Data 永不作为清理依据。删除后验证失败/取消时文件可能已经删掉，不能声称所有清理错误都保留文件；未来协调器须对未核实清理保持 self 封锁。本片没有实现该业务判定或 UI 生命周期。 |
| DATA — 39例 | [Core 测试](<tests/SystemAutorun.Core.Tests/SelfStartupPendingStoreTests.cs>)验证严格形状/边界、只读零创建、跨实例保留/无恢复清锁能力、错 root 拷贝、并发单 ID、真实文件独占/readonly/目录故障、合作租约忙、写前/发布后 IO 与取消、精确 bytes 条件及清理单次失败封锁。它们没有调用 Agent，不把 reserved 当 admitted。 |
| FLOW — 独立进程4例 | [进程测试](<tests/SystemAutorun.IntegrationTests/SelfStartupPendingCrashTests.cs>)经[文件专用宿主](<tests/SystemAutorun.IntegrationTests/SelfStartupPendingCrashHost.cs>)在 flushed/published/reserved 三阶段真实终止独占 GUID 临时映像；活 owner 时另进程只读及抢占拒绝，终止后两个全新 PID 读取，发布后的原 ID 不丢、不清/不重发。发布前孤立临时保持字节不变且不是已发送权威；坏记录跨独立进程封锁。根/映像仍按既有私有白名单校验。这里只证明文件恢复，不是 GUI 关闭、实际 Agent 提交或 Windows 登录启动旅程。 |
| 首轮失败保留 | [首次构建](<docs/visual/sa-20261104/r77-self-pending/focused-build.txt>)因3处 xUnit2031 在测试前失败，按断言 predicate 重载修正，无禁用规则。[第二次崩溃测试](<docs/visual/sa-20261104/r77-self-pending/focused2-crash.txt>)2/4，因为测试错误地把独占 workspace lease 当普通数据读取；保持生产 FileShare.None，改为仅将该控制文件排除于数据快照、单独验长度、owner退出后验空 bytes，其他恢复数据/临时字节不删检查。[复验](<docs/visual/sa-20261104/r77-self-pending/focused3-crash.txt>)4/4，Core39/39；原日志/TRX保留。 |
| ENG — 双配置 | [冻结后24步](<docs/visual/sa-20261104/r77-self-pending/steps.json>)全 exit0；Debug/Release各0 warning/error。每配置 Core694、CLI168、新 pending crash4、typed client8、Agent30、service43、Journal50、既有 self crash8、历史宿主16、registration62、既有 GUI53，合计各 **1136/1136、0跳过**。见[逐TRX摘要](<docs/visual/sa-20261104/r77-self-pending/test-summary.json>)。这是明确筛选矩阵，不是 integration 全集或新 GUI 验收。 |
| 审查 / 保留 / 清理 | [只读安全咨询与处置](<docs/visual/sa-20261104/r77-self-pending/review-disposition.json>)未报本片 blocker；采纳写端 byte 上限，澄清删除后错误和 caller proof，非认证。[既有源唯一差异](<docs/visual/sa-20261104/r77-self-pending/diff-StartupHistoryCrashHost.cs.patch>)仅测试 Main 两行分流；继承72中另外71不变，新增5源，冻结77项0漂移。[终检](<docs/visual/sa-20261104/r77-self-pending/cleanup-after.json>)确认相关进程、临时候选、私有HKCU/HKLM键、夹具service、dump均0，diff-check0；只删除核对完整路径、普通且为空的测试父目录。保留其他未提交工作，不提交/安装/发布，无真实 Run/Boot 或发行目标写测。 |

**下一片**：在已批范围内实现同 root 共享协调器，落实真实“明确确认→await持久保留→Agent单次发送”及 known/unknown/存储错误生命周期，再接按需窗口、替换旧入口并独立复核登记。磁盘读取不是放行许可，跨重启未知继续只读原 ID；新 GUI 像素/键鼠/焦点/桌面 DPI、GUI 与实际提交组合、完整 CLI parity 和整体 G3 尚未完成，整体目标保持进行。

## 2026-11-04 — self 共享提交与 ACK 契约（R76，GUI 仍待接线）

本片落实已批 self GUI 最小实施范围的第一项后台前置条件，不改变 G1/G2、CLI 外部语法、receipt v1 或 Windows 准入/原生机制。新增 Core typed 提交并让 CLI 共用 ACK 读取；**尚未实现持久 pending、共享 GUI 协调器或替换两处旧 direct 写入口**。本片没有界面变化，G2/VIS N/A；这不豁免后续 GUI 的真实像素和交互验收，SG01–SG06 尚未整体通过。

| 类别 | 实际结果与边界 |
| --- | --- |
| 提交契约 | [共享 client](<src/SystemAutorun.Core/Ipc/AgentSelfStartupClient.cs#L17-L62>)只接受有效且已经明确 Confirmed=true 的请求及调用方保留的 UUIDv7。它拒绝未确认/坏请求/无效 ID，不替用户确认、不生成 ID、不持久化、不重试、不启动 daemon/UAC。持久预留仍是未来 GUI 调用方必须先完成的责任，注释不是存储实现。 |
| Root / 阶段 | [AgentClient 唯一增量](<docs/visual/sa-20261104/r76-self-client/diff-AgentClient.cs.patch>)是内部只读 RootId，原 attemptedWrite 和异常处理不变。typed 提交在发送前核对传输与预期 root；这是元数据相等检查，**不是目录 pin 或原子 check-and-send**，迟到路径/reparse 换根竞态未测。ACK 使用发送前捕获的 root ID，不在交换后重新解析可能已移动/不可用的文件系统根。服务端仍复核包、主体、root、TargetContextId 与确认。 |
| ACK / CLI | [CLI 差异](<docs/visual/sa-20261104/r76-self-client/diff-CliApplication.cs.patch>)删除局部读取器，仍走原传输一次，再调用共享 reader。reader 校验协议、root、原操作 ID、请求 ID、revision、矛盾状态、规范错误码及对象/大小；坏 ACK 转原 ID Unknown，并只保留有界请求 ID。成功 Data 克隆为 **opaque 历史**，失败 Data 隐去，不解释内部字段、当前登记、重试权限或登录启动证明。CLI 共享 reader；新 typed SubmitAsync 尚未接 GUI。 |
| FLOW / DATA — 新增 39+8 例 | [Core 39 例](<tests/SystemAutorun.Core.Tests/AgentSelfStartupClientTests.cs>)覆盖本地拒绝、显式 ID、错 root、坏 ACK、opaque 数据生存期、根消失后只按捕获 ID 读取；实际私有 Named Pipe 验证写前 deadline/取消，以及**实际读到单次请求后**断线、错字段或坏 ACK，保留原 ID。这里的 Pipe 服务是合成响应端，没有原生写入。[集成 8 例](<tests/SystemAutorun.IntegrationTests/SelfStartupClientTests.cs>)通过真实 Agent/同一 Journal/GUID 原生适配器验证六种转换、未确认不发送、Running/重复只观察、原 ID 重放不重开目标及两个活私有服务器错 root 均不写。私有 Journal 精确使用 FixtureCommand，不冒称生产宿主正向旅程。 |
| 首轮失败保留 | 首次构建 0 warning/error、Core39/39；[首次集成](<docs/visual/sa-20261104/r76-self-client/focused-integration.txt>)98/104，六个新增测试把私有 Journal 命令误当生产命令。核对既有 fixture 合同后，只改测试 import/精确常量断言/说明，未改生产逻辑或放宽检查；[复验](<docs/visual/sa-20261104/r76-self-client/focused2-integration.txt>)104/104，原日志/TRX 保留。 |
| ENG — 冻结后实跑 | [18 步矩阵](<docs/visual/sa-20261104/r76-self-client/steps.json>)全部 exit0。Debug/Release 构建各 **0 warning/error**；每配置 Core655、CLI168、typed client8、Agent30、service43、Journal50、registration62、既有GUI53，合计各 **1069/1069、0 跳过**。GUI53 仅旧回归，不是新 self GUI VIS/FLOW，也不是未筛选 integration 全集。逐 TRX 见[测试摘要](<docs/visual/sa-20261104/r76-self-client/test-summary.json>)。 |
| 审查 / 源码 | [只读安全咨询及处置](<docs/visual/sa-20261104/r76-self-client/review-disposition.json>)未报告本范围 blocker，不是认证或新权限批准。矩阵首个 preflight 在测试前发现继承的 R74 清单未含本次涉及的 AgentClient；[补覆盖记录](<docs/visual/sa-20261104/r76-self-client/baseline-extension.json>)保留原69项不变，以本片修改前正文副本补成70项工作基线。仅三份声明源码改变、另外67份不变，新增两份测试后冻结 **72 项，终检0漂移**；不忽略清单外旧文件，也不重写历史基线。 |
| 清理 | [终检](<docs/visual/sa-20261104/r76-self-client/cleanup-after.json>)确认相关进程、SystemAutorun 临时候选、HKCU/HKLM 私有夹具键、fixture service、dump 均0，diff-check exit0。只删除当场核对完整路径、非 reparse 且为空的共享测试父目录；保留原未提交工作，没有提交/安装/发布、真实 Run/Boot 写测或目标启动。 |

**下一片**：实现严格、原子持久的 pending-ID 存取及同 root 协调，再接已批按需窗口与两个入口；未知跨重启保留且只读查询，不凭 Completed、历史 Data、当前登记匹配或 pending 文件自行解锁/重发。独立现态复核、真实 WPF 像素/键鼠/焦点/桌面 DPI、实际发行宿主正向旅程、Boot 写边界、完整 CLI parity 和整体 G3 均未完成，总目标保持进行。

## 2026-11-04 — self GUI G1 批准与 G2 候选（R75）

**G1-self-gui-r1、G2-self-gui-r1 与精确补充/最小实施范围均已批准，已明确授权实施。** 用户先在看到流程范围后明确选择“批准流程，继续 G2（推荐）”，该次不是实装许可；随后看到真实r1、审阅结论、补充与实施范围，再独立选择“批准 r1、补充并实施（推荐）”。后一次确认才授予实施许可，不代表产品G3通过。此修订只影响 self 两处旧入口：统一“登录启动…”按需工具，选择方向→只读准备→摘要→逐次明确确认→Agent单次提交，提交前持久保留原ID，未知跨重启只读查验且禁止新启停。其他功能、主屏布局、Boot和已批包/原生安全边界不变。

| 验收 ID | 获批旅程及下一片应观察的结果 |
| --- | --- |
| SG01 入口 | 从主菜单或监控入口打开同一个工具→不写入，不用乐观勾选伪装登记成功；关闭/取消回原入口焦点。 |
| SG02 准备/确认 | 选择启用或停用→只读准备并展示真实前态、当前主体/root及当前服务端包固定GUI口径；拒绝或取消不提交，不提供任意目标。明确确认才构造授权请求，过期/漂移由服务端重新拒绝。 |
| SG03 发送/保留 | 确认后、发送前原子保存原operationId→同root共享、一次发送、双击不重提。存取错误仅封锁self，不自动补ID/权限或照发。可证未发送与写出后的不确定分开处理，不把reserved当admitted。 |
| SG04 未知恢复 | 已发送结果未知、关闭或崩溃→重开保留原ID，只查询/复制/关闭；查不到、Running、Unknown、缺正文或查询失败不证明未执行。查询、刷新和当前值匹配均不解除已锁未知，不强制解锁、不换ID、不自动重发/回滚/UAC。 |
| SG05 状态 | 正常回执后独立复核登记→区分本包登记匹配/未登记/无法核对；历史opaque Data与期望值不应用到当前UI，也不把登记匹配称为登录启动或程序运行验证。 |
| SG06 分项验收 | 同SG-F1跑真实WPF像素/鼠标/键盘/取消/关闭/焦点/桌面DPI；真实Pipe/Journal/私有GUID适配器及跨重启pending持久化单独证实，Debug/Release回归。无真实Run/Boot、目标启动或发行宿主写测试。 |

**实际完成**：只读复核旧写与回执缝，R74冻结69源0漂移；按授权真实生成并查看[两关键状态r1](<docs/visual/sa-20261104/self-gui-g2-candidate-r1.png>)。PNG1536×1024、1,325,560bytes、SHA256 `A052962E86229EC503646D312351E252880C5A7D1DBD3B74173264318CFD2819`，verify/完整解码/项目副本字节一致已过。[输入和授权](<docs/visual/sa-20261104/self-gui-g2-r1.prompt.md>)只有功能/风格/合成数据，没有截图参考、位置或分区模板；[身份校验](<docs/visual/sa-20261104/self-gui-g2-r1.image.json>)保留原件与副本关系。已批G2精确补充、状态覆盖、桌面/键盘及最小实施范围见[原设计合同](<docs/design-system.md#2026-11-04--self-登录启动-g2-候选-r1r75>)，不另建任务/完成账本。

**审查取舍**：不采用只读映射中的“复用样式即可G2免审”推论；已补回本关。不采用Core自动将未确认请求升为Confirmed=true、仅凭Completed或“错误码不是outcome_unknown”放闸的建议。R74前后发送阶段缺陷已修且有冻结回归，R75没有证据推翻该结论，不把审查日志的中间推论当作残留缺陷。未来共享提交只延续实际发送边界与严格ACK，不将磁盘pending或receipt历史升级为原生权限。

**收尾审计**：[R75审计](<docs/visual/sa-20261104/r75-design-audit.json>)确认R74冻结69源0漂移、8个既有本片本地引用有效、候选hash一致、diff-check exit0。没有启动runtime测试或创建其进程/私有键/服务/临时根，图像原件与项目副本是保留的设计交付物，不是测试残留；不把本轮无夹具冒称全系统无残留。

**未运行/未完成**：没有改GUI/Core/CLI业务源码，没有启动新GUI、Agent、Console测试或目标，没有运行新build/test矩阵；本轮不记产品VIS/FLOW/DATA/ENG通过。只有图像文件验证和源码/文档审计适用。现已取得G2与明确实施授权，下一片从共享提交/pending契约开始落实SG01–SG06，不再重批同一G1/G2；本轮设计关卡通过不代表产物G3或整体目标完成。

## 2026-11-04 — self CLI 严格准备、逐次确认与原 ID 协作（R74）

本次沿用 G1-self-write-r1 / G1-self-package-admission-r1，补齐 Console CLI 旅程。CLI 复用已有文件、`--yes`、JSON、超时和 UUIDv7 约定；本次没有图形界面或布局改动，G2/VIS 对此 Console 文本切片 N/A，**不代表 GUI 新确认/未知状态获免审或已迁移**。测试只使用 GUID 私有原生夹具，不操作真实 Run/Boot，也不启动固定 GUI 目标。

| 类别 | 本次结果与边界 |
| --- | --- |
| 旅程 / 契约 | [CLI 路由](<src/SystemAutorun.Cli/CliArguments.cs>)新增 `startup self prepare enable\|disable` 与 `startup self set --file JSON\|- --yes [--operation-id UUIDv7]`。准备仅允许只读传输选项，拒绝确认、文件、revision、operationId；返回未确认数据。set 只接保存后的 `JSON.data`，使用既有严格读取器，拒绝未知、重复、大小写别名、缺失或错误类型字段；文件内确认不能代替本次 `--yes`，本地不挑选 exe/key/SID 或变更服务端目标。 |
| 共享准备读取 | 新增 [Core typed client](<src/SystemAutorun.Core/Ipc/AgentSelfStartupClient.cs>)，只负责准备及严格读取。它检查协议/root、无 mutation ID/revision/replay、精确期望方向、Confirmed=false 和完整请求形状；错误回复不显示 Data。准备不是持久计划、CAS、拥有权或原生授权，执行端仍复核全部前提。 |
| ID / 阶段修复 | [CLI](<src/SystemAutorun.Cli/CliApplication.cs>)只在严格输入通过后生成/规范化 ID，并在连接/交换前向 stderr 打印及 Flush **reserved ID，不代表已准入**；JSON stdout 仍为单个信封。只发送一次，不自动准备/刷新、启动 daemon/UAC 或重试。保留 AgentClient 的实际 attemptedWrite 边界：连接前原始错误保持 connection_failed/timeout/permission/argument 分类，无已准入输出 ID；写出后的 AgentOutcomeUnknown 原样保留，收到无效 ACK 才转原 ID Unknown。未知时查原回执，不换新 ID 或输入盲重放。 |
| 结果边界 | set 校验 ACK 协议/root/operationId/状态/对象/大小，不将 Windows internal SelfJournalEvidence 提升为新公共 DTO。成功 Data 仅作 **opaque 历史驱动证据**，不按 protocolCompleted 等字段推导权限、当前原生状态、可重试性或登录启动证明；失败内部 Data 隐去，原 Journal 回执仍可独立查询。全局 receipt v1、指纹和失败分类白名单不变。 |
| FLOW / DATA | 新增 [66 项 CLI 测试](<tests/SystemAutorun.IntegrationTests/SelfStartupCliTests.cs>)。六种转换均运行独立 Console exe → 真实 Named Pipe → 同一个 R73 Journal → GUID 私有原生适配器，证明准备不写、确认后单回执、文件字节不变、原 ID 缓存重放不再进入目标、operation show 读取同回执；另测 stdin/自动与显式 ID、严格输入/回复、错误 Data 隐去、ID 提前可见和单次发送。真实不存在 Pipe 的 deadline 证明未发送不冒称 Unknown；**合成 post-write 异常只验适配契约，不冒充真实系统断线测试，也未验真实发行宿主正向旅程**。 |
| 审查 / 失败保留 | 只读安全审查发现 P2 前后发送阶段混淆及 P3 拒绝文件仍给回执查询 ID，现已分别修复并拆测试。首轮聚焦 189 项中 2 个输出文案断言取错子串，修正精确文字且保留不启动/非证明断言，日志/TRX 不删除；后续聚焦194/194，再增加两种连接前错误测试后冻结。**本段最终口径取代今日日志中的 @09:43 中间源码快照，不把旧审查残留当现状**。 |
| ENG | Debug/Release **34 步全 exit 0**，两构建均 0 warning/error。每配置 Core616、CLI168（含新增66）、Agent30、service43、Journal50、crash8、key-create23、native21、session32、raw33、admission16、target66、host16、history63、registration62、GUI53 全过且无跳过；GUI53 仅既有回归，不是新 GUI VIS/FLOW 验收。 |
| 源码 / 清理 | R73 的67项先核对无漂移，仅声明修改 CLI 两文件及测试 Host 的 private→internal 可见性，另64项保持；新增 typed 准备 client / CLI 测试后冻结 **69 项，终检0漂移**。保留全部无关未提交工作。相关进程、临时候选、私有 HKCU/HKLM 键、fixture service、dump 均0；diff-check exit0。本轮无需额外删除构建助手产物。 |

证据：[审查处置](<docs/visual/sa-20261104/r74-self-cli/review-disposition.json>)、[矩阵](<docs/visual/sa-20261104/r74-self-cli/steps.json>)、[测试摘要（含首轮失败）](<docs/visual/sa-20261104/r74-self-cli/test-summary.json>)、[源码差异核对](<docs/visual/sa-20261104/r74-self-cli/existing-source-audit.json>)、[清理终检](<docs/visual/sa-20261104/r74-self-cli/cleanup-after.json>)。

**未完成范围**：旧 GUI self 入口仍直接写入，尚未迁移到 Agent/逐次确认/未知回执门禁；下一切片先核对相关 G1/G2 与实际代码，再处理两个入口及真实 GUI 旅程。发行宿主正向/慢盘、Boot 写边界、完整启动项业务 CLI parity 与整体 VIS/FLOW/DATA/ENG 仍未完成，不宣称生产就绪或总目标达成。

## 2026-11-04 — self Agent 单一 Journal 接线与发行包校验计时（R73）

沿用 G1-self-write-r1 / G1-self-package-admission-r1，只接后台 RPC，**尚未接 CLI 语法、typed client 或 GUI，旧 GUI 写路径未迁移**；无 GUI/视觉改动，G2/VIS N/A。测试均为 GUID 私有注册表，不运行发行宿主、不触碰真实 Run/Boot。

| 类别 | 本次结果与边界 |
| --- | --- |
| 单一持久所有者 | [Journal](<src/SystemAutorun.Core/Ipc/OperationJournal.cs>)只暴露不可变宿主路径元数据，不是权限；[服务](<src/SystemAutorun.Windows/Startup/SelfStartupRegistrationService.cs>)新增只收同一 Journal 的构造，不允许独立 root/journal 配对。[Agent](<src/SystemAutorun.Agent/AgentCommandDispatcher.cs>)一次绑定共享实例，拒绝错 root 或错服务实例。`startup-self-set` 在通用包装**之前**走自身 context-aware 管线，只有一次准入/发布；`IsMutation` 表示信封 ID 分类，不表示必套通用包装。原按路径构造保留兼容，后续 GUI 不可用它另造持久所有者。 |
| 请求与准备 | `startup-self-prepare` 只收 name=`enable`/`disable`，不得携带 payload/revision/operationId，返回既有 schema1、Confirmed=false。set 保留严格确认、原始前态、原 operationId 与缓存别名保护。服务端先验证 transport root/version/timeout，再仅清掉 transport 字段；Pipe 的原校验不变，不从请求挑选包或原生目标。 |
| 昂贵预检 | 每个 Agent/root 的唯一 dispatcher 增加不排队 single-flight、完成后 250ms 单调冷却、关联 30 秒预检预算，工作真正结束后才放闸。真实 Pipe 阻塞用例证明并发预检立即 configuration_busy，ping 仍通、无目录/回执写入。**没有新增断线检测，不承诺任意多 dispatcher/多进程全局限流**；预算限制断线后的剩余工作。 |
| FLOW / DATA | 新增 [30 项隔离 Agent 测试](<tests/SystemAutorun.IntegrationTests/SelfStartupAgentTests.cs>)：真实 Named Pipe 六种 native/no-op 转换、未确认准备不写存储、最终回执/缓存/冲突、同实例 Running 与重复 operation_in_progress、每 opId 单回执、头部/缓存别名先拒、未知目标字段拒绝、不替用户确认、错根/错实例构造拒绝，以及默认生产服务在 testhost 中无原生观测/准入即拒绝。成功与调用数断言共同防双 Journal，单看回执数量不足。**不证明真实发行宿主正向操作**。 |
| 实测成本 | [只读证明组件计时](<docs/visual/sa-20261104/r73-self-agent/package-proof-budget.json>)使用既有 R16 包：876 资产 / 430311884 字节；Open+seal 1611.71ms，四遍预检校验约 **2406.77ms**，十六遍约 **6782.23ms**。显式合成 seam 始终 IsCurrentPackage=false，无 registry read、actor lease、Journal、原生调用或宿主启动，manifest 前后摘要相同。**仅当前不受控/预热缓存下组件成本，不是 Prepare/Execute 全旅程、冷盘或合作锁时限保证**；保留全部字节校验、调用者预算与 Unknown 恢复规则，不强制所有调用使用 600s。 |
| ENG / 失败保留 | 首次新增夹具因缺 autorun.yaml 失败 8/143；仅补私有根的空配置，不削弱客户端 root 校验，日志/TRX 保留。后续聚焦 **144/144**。冻结后的 Debug/Release **32 步**全部 exit 0、两构建 0 warning/error；每配置 Core616、Agent30、service43、Journal50、crash8、key-create23、native21、session32、raw33、admission16、target66、host16、history63、registration62、GUI53 均过、无跳过。旧 GUI 回归不等于新增 UI 验收。 |
| 源码 / 清理 | R72 的 62 项全部先核对不变，补入既有协议/Dispatcher 后形成 64 项 before 基线；仅声明修改六文件、各留 before-code 与差异，其余 58 项不变。新增 Agent 测试与计时源/工程后冻结 **67 项**，终检 0 漂移；无关未提交内容保留。相关进程、临时候选、HKCU/HKLM 私有夹具键、fixture service、dump 均 0；校验路径/重解析点后移除自有计时 bin/obj **27188983 字节**，保留源码/报告；diff-check exit 0。 |

证据：[审查与处置](<docs/visual/sa-20261104/r73-self-agent/review-disposition.json>)、[矩阵](<docs/visual/sa-20261104/r73-self-agent/steps.json>)、[测试摘要（含首轮失败）](<docs/visual/sa-20261104/r73-self-agent/test-summary.json>)、[终检](<docs/visual/sa-20261104/r73-self-agent/cleanup-after.json>)、[计时输出清理](<docs/visual/sa-20261104/r73-self-agent/proof-build-cleanup.json>)。下一步接 CLI 的严格文件/确认/原 ID 协作链路，再迁移 GUI；Boot 边界、真实发行正向旅程与整体 G3 仍未完成。

## 2026-11-04 — self 当前包生产后端与只读准备（R72，尚未接 GUI/CLI/Agent）

继续 G1-self-write-r1。用户对 `self-package-admission-r1` 明确选择 **“信任当前运行包并逐次确认”**，本次据此记录 G1-self-package-admission-r1：仅当前服务端运行的发行 App/Agent 所属包内固定 GUI，核对既有清单及列出资产完整性；**不是发布者认证，不新增签名、证书或信任库，不承诺抵御本账户恶意篡改或未列出依赖**。原确认、主体/root、冲突拒绝、跨 root 协作、Journal、原生前后核验和未知不重放边界不变。测试仍禁止真实 Run/Boot。

新增 [Windows self 服务](<src/SystemAutorun.Windows/Startup/SelfStartupRegistrationService.cs>)及[43 项服务测试](<tests/SystemAutorun.IntegrationTests/SelfStartupRegistrationServiceTests.cs>)，扩展既有[协调器](<src/SystemAutorun.Windows/Startup/SelfStartupJournal.cs>)连接固定生产适配器；**没有 RPC/CLI/GUI 路由或旧 GUI self 路径迁移**。无用户可见交互/视觉改动，G2/VIS N/A；不是生产旅程全项完成。

| 类别 | 本次边界与证据 |
| --- | --- |
| 当前包与 root | 生产构造一次绑定宿主解析的 ProjectPaths、ambient App/Agent 目标、默认主体合作租约和固定原生 reader/writer，无请求目标路径、SID、进程、包或控制目录覆盖。宿主后续须单例绑定当前 root，不能从请求重建。只有 ambient `OpenCurrent` 可赋 `IsCurrentPackage`；显式合成环境 seam 即使 context 字段相同也不能打标。它证明来源，不证明发布者或独立写权限。 |
| Prepare / 纯读 | 返回既有 schema1 SelfStartupMutationRequest，`Confirmed=false`，仅服务端 contextId + 全字节原生指纹；用假设确认只检查 eligibility，不准入、不写元数据/回执、不取 RootWriteLease 或 actor 准入锁。前后验证目标、主体及 root；即便 mutation root/actor 锁被占仍可纯读，但只代表时点观察，不是 CAS、授权或持久计划。准备后仍须明确确认、重新验证；公开准备错误收敛到 target/context/conflict/registry 四类，原生细节不扩展公开码表。 |
| 持久准入与执行 | Execute 接受固定 `startup-self-set` 信封、严格冻结 schema1，经同一 Journal 准入；原 GUID fixture 与生产构造不可由请求切换。相同 operationId 绑定同一确认意图，改 Confirmed/Enabled 等 payload 复用会冲突，已拒绝意图正常缓存回放；没有自动重试或自动换 ID。保留 Running 保守 fence、最终回执及持有 owner 至发布/回读完成，不新增 native stage 持久日志；崩溃后仍 Unknown。 |
| F1 审查修复 | 删除零使用的预组合 planning 工厂。生产原生入口现在必须传入 **Core 铸造且尚活跃、root 匹配的 OperationExecutionContext**，读取其冻结信封/明确确认并核对 session 的同一计划及预期状态，在 dispatch/key-created 回调周围再次校验；另须 ambient 包来源与完整 native/session 校验。context 仅证明 Journal 活跃准入，不独立授权原生写入；缺失/已结束/错 root/错命令/未确认/错 context/错意图均在任何原生调用前拒绝。合成 package session 即便有合法 live Journal context，也在打开真实注册表前拒绝。 |
| 原生范围 | 固定当前主体 64 位 Run 的 SystemAutorun 项；无路径/键名/hive 选择、启动目标、UAC 或 Boot 写入。沿 R69 的单层 Create 机制：Run 子键缺失时可在已存在的 CurrentVersion 下创建字面 `Run` 子键并 SetValue，不递归创建祖先；回执分列 CreateKey/SetValue，部分创建后出错 Unknown、不回滚。**测试只在私有 GUID 子键验证机制**。 |
| F2 / 后续路由陷阱 | Agent 的通用 IsMutation 分支已自带 Journal，不能简单套入本服务造成同 root/ID 的内外 Journal 互撞、未执行即 Unknown；CLI mutation ID 也须共用分类。后续接线必须选择**唯一权威 Journal 路径**、在通用包装前分派或等价重构并补测试，不以永久 App-only 限制规避“GUI/CLI 共用业务”目标。当前仍未接线。 |
| F3 / 成本未验 | [只读清单测量](<docs/visual/sa-20261104/r72-self-production-backend/package-sizing.json>)为 876 资产 / 430311884 字节；**不是延迟或吞吐测试**。Open 两遍资产检查，Prepare 又前后复核，执行在 actor 锁内还有多次完整哈希；没有静默降为仅 identity 检查。Prepare/Execute 整个管线均 Task.Run，避免占 GUI Dispatcher，但**不承诺生产性能或合作锁时限**。实际发行包纯读计时及保留安全边界的优化，仍是开放用户路由前的工作。 |
| FLOW / DATA | 43 项覆盖只读无目录/字节变化、准入锁不获取、预取消、root 错配、非文本/外来值拒绝、预览不自确认、六种固定 GUID 真 native/无写转换、缓存不重开输入、头部别名拒绝、准备后注册/包/context 变化、原生入口缺少 live 意图、合成来源无生产权限和公开 testhost 拒绝。回调断言在 workflow 真完成后显式重抛，不能被 Journal 转 Unknown 掩盖。公开错误返回 faulted Task，不同步抛出。**不证明真实发行宿主正向旅程或旧 GUI 迁移**。 |
| ENG | 首轮 267/267，扩展时一次编译因 `Record.Exception` 表达式赋值选到已废弃 Func<Task> 重载失败；改为 block-bodied Action，保留失败日志、不跳测。后续聚焦 **276/276**。冻结源码后的 Debug/Release 30 步矩阵全部 exit 0，两构建均 0 warning/error；每配置 Core 616、service 43、Journal 50、真实终止 8、key-create 23、native 21、session 32、raw 33、admission 16、target 66、host 16、history-journal 63、registration 62、GUI 回归 53 全过，无失败/跳过；GUI 回归不是新增 UI 的 VIS 验收。 |
| 源码与清理 | R70 的 60 项基线中仅声明修改 Journal/NativeMutation/PlanningSession/TargetLease 四文件，各保留 before-code 哈希匹配副本与完整差异；其余 56 项不变。新增服务/测试后冻结 **62 项**，矩阵及最终复核均 0 差异，保留所有无关未提交工作。相关进程、SystemAutorun 临时候选、HKCU/HKLM 私有夹具键、fixture service、dump 均 0；仅移除已核实无进程、无重解析且为空的夹具父目录；`git diff --check` exit 0。 |

证据：[审查及处置/明确剩余项](<docs/visual/sa-20261104/r72-self-production-backend/review-disposition.json>)、[冻结源码](<docs/visual/sa-20261104/r72-self-production-backend/sources.json>)、[30 步矩阵](<docs/visual/sa-20261104/r72-self-production-backend/steps.json>)、[逐 TRX 结果](<docs/visual/sa-20261104/r72-self-production-backend/test-summary.json>)、[基线差异](<docs/visual/sa-20261104/r72-self-production-backend/existing-source-audit.json>)、[清理终态](<docs/visual/sa-20261104/r72-self-production-backend/cleanup-after.json>)。

**剩余**：实际发行包纯读性能/正向宿主准备、单一 Journal 的 Agent/CLI 接线、当前 root 单例绑定、G1 确认旅程及旧 GUI self 迁移、Boot 写细化、总体产品 G3。未测试或操作真实 Run/Boot；未宣称包签名、完整依赖闭包、非合作写入 CAS/ABA、独立原生所有权/阶段恢复。

## 2026-11-04 — self Journal 组合与真实终止（R70，仅私有 GUID，非生产 self 写入口）

继续 G1-self-write-r1，新增[内部协调器](<src/SystemAutorun.Windows/Startup/SelfStartupJournal.cs>)、[50 项 Journal 组合测试](<tests/SystemAutorun.IntegrationTests/SelfStartupJournalTests.cs>)及[8 个真实进程终止用例](<tests/SystemAutorun.IntegrationTests/SelfStartupJournalCrashTests.cs>)。**只使用固定 GUID 私有注册表和不运行的合成包**；没有生产 Run entrypoint、包授权、GUI/CLI/RPC 命令或旧 GUI self 写路径迁移，没有修改共享回执 v1、全局 Journal 指纹/无写白名单。无交互或视觉变化，G2/VIS 为 N/A。

| 类别 | 边界与可复核证据 |
| --- | --- |
| Journal / 请求 | 协调器一次绑定 GUID、ProjectPaths 及可信 target/admission 工厂，并据绑定路径构造既有 OperationJournal，不能分别传入不同 journal/root 或逐请求指定目标。回调仅消费冻结克隆的 context.Request、严格 R63 payload，自己构造 PlanningSession 并固定同一 GUID raw reader，校验 live context 和目标物理 root；不接受外传 plan/session。全局指纹不绑定传输头且规范化 command，因此固定命令大小写、协议、root/timeout、name/revision **在调用 Journal 前检查，缓存重放也不跳过**；无效信封不准入、不建回执。payload 保持既有指纹约束；只有请求 Id 可在合法同 ID 重放时更新。 |
| 持久边界 / 分类 | Journal 在回调前持久发布 Running，释放 RootWriteLease 后才进入包 pin / 主体合作租约 / native scope。首次 driver mutation dispatch 前失败按 not_executed，任一 Create/Set/Delete dispatch 后任何错误均 outcome_unknown，**不按异常类型或错误码推断无写**；相同 IO、取消及 invalid_argument 在前/后阶段均有反证。最终 Response.Data 有界投影 driver protocol-completed、dispatch、每调用 status/disposition、created/held/fresh 状态、原因与 native error；不带原始命令、包路径、SID 或异常消息。ProtocolCompleted 只描述原生协议，不等同 Journal 发布/回读完成；state Matches 不是所有权。 |
| 生命周期 / 回读 | owner 在回调外持有，直到终态 Journal 发布及完整回读比较后才 finally Dispose；提交时 native scope / Monitor 已退出，不持 root 等待 actor。确定性用例先持实际 RootWriteLease，放行 callback，再等待捕获的 context.IsActive=false（真实 End），确认 task 未完成、磁盘仍 Running、包不能独占打开且 actor 租约仍 busy；释放 root 后完成。receipt_verified **在 GetAsync 和 owner/fingerprint/state/响应 DeepEquals 比较之后**，再次验证 pin/租约仍持有。回读使用 GetAsync 属变更流程内核对，不冒称纯只读诊断。 |
| DATA / 故障 | [Debug Journal](<docs/visual/sa-20261103/r70-self-native-journal/debug-journal.txt>) / [Release Journal](<docs/visual/sa-20261103/r70-self-native-journal/release-journal.txt>)各 **50/50**：覆盖现存/缺键 Create、Delete、两类 noop、持久 Running 先于 dispatch、合法同 ID 重放不重开任何输入、不同指纹冲突、冻结原 JsonDocument、两种物理 root 错配、无效新信封和同 ID 头部变更、七阶段 IO/取消、部分建空键不补偿、原生完成后回调报错、回执发布共享拒绝、结果超限省略。新增真实回读反证：改 Response.Data、owner、fingerprint、state，临时删除经验证的精确夹具回执或用独占文件拒绝读取，让 GetAsync/比较器实际执行并要求 AgentOutcomeUnknownException；finally 恢复精确原字节，复核 Completed、原生结果未撤销且只重放缓存。不是只在读取前抛模拟异常。 |
| FLOW / 真实终止 | [Debug crash](<docs/visual/sa-20261103/r70-self-native-journal/debug-crash.txt>) / [Release crash](<docs/visual/sa-20261103/r70-self-native-journal/release-crash.txt>)各 **8/8**：[专用 test host](<tests/SystemAutorun.IntegrationTests/SelfStartupJournalCrashHost.cs>)在 accepted、before_native、key_created、native_returned、native_verified、callback_returning、receipt_readback、receipt_verified 被真实终止；只控制精确验证 PID/映像路径的 SAFixture_R46_GUID.exe，非通用 dotnet/pwsh，也不调用生产 Agent fixture 命令。每个终止点用两个独立新进程重放：target/admission 工厂和回调均 **0**，磁盘回执/原生状态字节不变。发布前磁盘 Running 保持原样、重放 Unknown，即使值已完全匹配或内存协议已验证也不接管；终态已发布的两个点重放缓存成功。原生 create 后中断保留空键，不盲补值或回滚；仅测试结束回收自有夹具。 |
| ENG / 审查 | [初版 38/38](<docs/visual/sa-20261103/r70-self-native-journal/focused-first.txt>)、[扩充 89/89](<docs/visual/sa-20261103/r70-self-native-journal/focused-second.txt>)；封装调整时[一次编译失败 CS0246](<docs/visual/sa-20261103/r70-self-native-journal/focused-third.txt>)为 nested PlanningSession 缺 alias，补正确别名后[最终聚焦 102/102](<docs/visual/sa-20261103/r70-self-native-journal/focused-fourth.txt>)，未放宽检查或跳过用例。[只读审查处置](<docs/visual/sa-20261103/r70-self-native-journal/review-disposition.json>)逐条记录重放头部绕过、持有期反证与比较器负例的修复；审查者看中间版本，不冒称最终安全审计。最终[60 项冻结源](<docs/visual/sa-20261103/r70-self-native-journal/sources.json>)的[28 步矩阵](<docs/visual/sa-20261103/r70-self-native-journal/steps.json>)全部 exit0，Debug/Release 构建各 **0 警告/0 错误**；每配置 Core **616/616**、新 Journal **50/50**、新 crash **8/8**、建键 **23/23**、既存值 **21/21**、会话 **32/32**、raw **33/33**、合作租约 **16/16**、target **66/66**、旧 history host **16/16**、旧 history Journal **63/63**、Boot/注册 **62/62**、七类 GUI **53/53**，见[TRX 汇总](<docs/visual/sa-20261103/r70-self-native-journal/test-summary.json>)；无失败或跳过。不是全量 integration、生产 self 写旅程或 VIS/原生焦点证明。 |
| 改动 / 清理 | 既有源仅[test-host 分派两行及两个测试 helper 可见性](<docs/visual/sa-20261103/r70-self-native-journal/existing-source-audit.json>)，原正文 SHA 匹配 R69；**其余 54 项既有源未改**，新增协调器及三份测试源。[最终清理](<docs/visual/sa-20261103/r70-self-native-journal/cleanup-after.json>)确认 **60 项冻结源差异 0、相关进程 0、临时候选 0、私有注册键 0、夹具服务 0、dump 0、diff check 0**。只移除精确路径且已验证普通/空的测试公共父目录，不清生产协调目录、用户数据或其他未提交工作。 |

**仍未完成：** 既有 Running 只是保守的持久准入栅栏，**不是逐次原生调用事件日志**；具体阶段在最终回执前仍只在内存，不能从匹配值或崩溃现场推断某调用/键归属、自动续写或补偿。未新增防同用户篡改凭据，回执回读只检查一致性，不授予写权限；顺序检查及合作锁仍非 key identity / 原子 CAS / ABA / 非合作写屏障。受信生产包准入、真实 Run writer、GUI/CLI 共用入口及旧路径迁移仍待完成；Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 继续未完成，整体目标保持进行。

## 2026-11-03 — self 固定单层建键与两阶段变更（R69，仅私有 GUID 缝，生产入口未接通）

本片继续 G1-self-write-r1，在[值机制](<src/SystemAutorun.Windows/Startup/SelfStartupNativeMutation.cs>)中接入[固定子键创建](<src/SystemAutorun.Windows/Startup/SelfStartupNativeKeyCreation.cs>)及[23 项专门用例](<tests/SystemAutorun.IntegrationTests/SelfStartupNativeKeyCreationTests.cs>)。**唯一适配器仍固定到非空 GUID 私有测试命名空间**；没有真实 Run writer entrypoint、递归建祖先、Journal、公开 schema、GUI/CLI/RPC 接线、发行包授权或目标启动，旧 GUI self 路径未迁移。无交互/视觉变化，G2/VIS 为 N/A。

| 类别 | 实装边界与可复核证据 |
| --- | --- |
| 单层建键 | 缺 Run 且为 Create 时，只打开既存固定 Parent，权限为 QUERY + CREATE_SUB_KEY + 64-view，**不含 SET**；默认 raw 读取权限仍 QUERY + 64-view，私有 helper 仅接受三种固定权限组合。通知写阶段后重读会话并验证 hive、primary SID、held/fresh parent 原生名；NtCreateKey 的名字固定单组件 Run，非易失/继承安全、Class/Title 为零、OBJ_DONT_REPARSE + CASE_INSENSITIVE，没有路径拼接输入、link/backup/restore/ACL 修复。遵循[官方 Create 约定](<https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwcreatekey>)，用户态调用 NtCreateKey。祖先缺失或 CREATE deny 于首次变更前拒绝，不创建父级。 |
| 两阶段 / 不接管 | 同一 attempt 只允许一次既存 Set/Delete，或 **CreateKey → SetValue**；创建返回原始 status0 且 disposition=1（CREATED_NEW_KEY）才可继续。disposition=2（OPENED_EXISTING）即使为空或为相同 canonical 值也拒绝接管；不触发第二调用，不回滚、不删外来键。先保存受保护的 CreatedKeyAfter，再执行有界 afterKeyCreate，再按同一 owner attempt 的真实 create0/1 证据核对中间态、完整值指纹和持握键名，最后写入已 pin 的固定 GUI 命令。其他会话的 create 证据不能推进本会话。 |
| 结果 / 取证 | 每次变更 API 前记录种类与 dispatch fence，分别保留原始 NTSTATUS/disposition；最后一个 NativeStatus=0 可能只表示空键已创建，不能报告启用成功。新增 ProtocolCompleted 与原始 Failure：仅完整保护流程正常返回才完成；HeldAfter/FreshAfter.Matches 只是实际状态，**不是写入成功或所有权证明**。外来 canonical 键竞态的 held/fresh 均可真实匹配，但仍 Failure=self_registry_context_changed、ProtocolCompleted=false、无 Set 调用。原生/中途错误优先抛出，同时独立保存可取得的后态；失败会话仅在同一个 live native scope、同 owner 且已跨 dispatch 时可只读取证，不恢复可写性，不跳过 actor/target/lease/token 验证，作用域外拒绝。 |
| 生命周期修复 | Monitor 可重入，不能仅凭 Dispose 同锁声称同线程回调安全。现在拒绝嵌套 native scope；同线程 Dispose 只请求延迟回收并使继续执行失败，直到原生句柄及外层 scope 退出才实际释放 pin / 合作租约，另一线程仍等待锁。有界回调前已有 create 中间快照；回调/内核调用不能强行取消或丢弃线程，不能承诺立即 Dispose。 |
| FLOW / DATA | 最终 [Debug 建键](<docs/visual/sa-20261103/r69-self-native-key-create/debug-key-create.txt>) / [Release 建键](<docs/visual/sa-20261103/r69-self-native-key-create/release-key-create.txt>)各 **23/23**，另[既存值 Debug](<docs/visual/sa-20261103/r69-self-native-key-create/debug-native.txt>) / [Release](<docs/visual/sa-20261103/r69-self-native-key-create/release-native.txt>)各 **21/21**。覆盖精确两次调用和单次阶段通知、保留 parent 其他值/兄弟键、祖先不创建、前/中途 IO 与取消、空键部分成功不补偿、空/foreign/canonical 三种 OPENED_EXISTING 拒绝、同名 parent / 新键替换、值漂移后仍有 fresh 观测、跨会话证据拒绝、parent CREATE deny 与只读 noop、DACL 二进制恢复/SDDL 精确复核、同线程前/中途 Dispose、嵌套 scope、另一线程 Dispose 与中间快照。实际删除 parent 使 NtCreateKey 返回映射 **1018**；实际删除新键使第二次 NtSetValueKey 返回 **1018**，保留第一条 create0/1 且不重建。最新竞态 REG_LINK 的既存/缺失自有目标均实际返回 **4395**，不改值或创建目标；用精确创建句柄 NtDeleteKey 先移除链接，再验证物理根后清理。所有变更限自有 GUID，包为不运行的合成资产；业务流程不补偿与最终测试夹具回收明确分开。 |
| ENG / 审查 | [初版聚焦 38/38](<docs/visual/sa-20261103/r69-self-native-key-create/focused-first.txt>)，审查修复及扩充后[聚焦 44/44](<docs/visual/sa-20261103/r69-self-native-key-create/focused-second.txt>)，无失败或跳过。[审查处置](<docs/visual/sa-20261103/r69-self-native-key-create/review-disposition.json>)记录逐条修正及匹配状态不冒充所有权；子代理只读中间版本，不冒称最终全面审计。最终 [56 项冻结源](<docs/visual/sa-20261103/r69-self-native-key-create/sources.json>)对应 [22 步矩阵](<docs/visual/sa-20261103/r69-self-native-key-create/steps.json>)：Debug/Release 构建各 **0 警告/0 错误**；Core 各 **616/616**、建键各 **23/23**、既存值各 **21/21**、会话各 **32/32**、raw 各 **33/33**、合作租约各 **16/16**、target 各 **66/66**、旧历史 worker 各 **16/16**、Boot/注册各 **62/62**、七类 GUI 各 **53/53**，见 [TRX 汇总](<docs/visual/sa-20261103/r69-self-native-key-create/test-summary.json>)。不是全量 integration、真实发行包 self 写入旅程或 VIS/原生焦点证明。 |
| 改动 / 清理 | 五项既有源为私有 open 权限、会话中间/诊断/生命周期控制、driver/attempt 序列、旧缺键用例预期及既有 link 互操作测试 helper 的一行可见性；[原正文备份与完整 diff](<docs/visual/sa-20261103/r69-self-native-key-create/existing-source-audit.json>)的 before SHA 均匹配 R68，**其余 49 项既有源未变**。[最终清理](<docs/visual/sa-20261103/r69-self-native-key-create/cleanup-after.json>)确认 **56 项冻结源差异 0、相关进程 0、临时候选 0、私有注册键 0、夹具服务 0、dump 0、diff check 0**。仅移除已验证精确路径、非 reparse 且空的公共夹具父目录，未清生产协调目录、用户数据或其他未提交工作。 |

**仍未完成：** 这是私有缝的单层建键/改值机制，不是已接通的生产 self 执行器。受信发行包准入、持久阶段 Journal / 崩溃核对、GUI/CLI 共用入口及旧写路径迁移待完成；缺失祖先仍拒绝。命名/顺序核验与合作锁不是 key identity、原子 CAS、ABA/非合作写屏障，内存 attempt 不能解释崩溃后状态，匹配后态不证明调用所有权。Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 保持未完成，整体目标继续。

## 2026-11-03 — self 现存键原生改值 / 删值机制（R68，仅私有 GUID 缝，尚无生产写入口）

本片沿用 G1-self-write-r1，新增[内部原生值机制](<src/SystemAutorun.Windows/Startup/SelfStartupNativeMutation.cs>)及[隔离测试](<tests/SystemAutorun.IntegrationTests/SelfStartupNativeMutationTests.cs>)。**唯一可调用适配器固定到非空 GUID 私有测试键，未提供真实 Run 的生产 entrypoint**；没有原生建键、递归建父级、Journal、公开 schema、RPC/CLI/GUI 接线或包信任授权，旧 GUI self 写路径仍未迁移。无用户交互或视觉变化，G2/VIS 为 N/A。

| 类别 | 实现与实际证据 |
| --- | --- |
| 值机制 / 最小权限 | 在 R67 会话内复核前态，按实际 primary SID 打开并核名 hive 与固定值所在键；仅需改值/删值时追加 SET_VALUE，无 CREATE、OPEN_LINK、backup/restore、ACL 修复或枚举权。持有同一原生键句柄比较完整原始指纹与核名，写入只取已 pin 的 Context.Command，以 REG_SZ、完整 UTF-16 和单个末尾 NUL 调用 NtSetValueKey；删除只用固定值名调用 NtDeleteValueKey，不删键或其他值。遵循 [Set 参数约定](<https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwsetvaluekey>)及 [Delete 权限/用户态 Nt 命名](<https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwdeletevaluekey>)。VerifyEnabled / VerifyAbsent 不申请 SET、不通知写阶段、不调用原生变更 API。 |
| 缺键边界 | R63 可按值态规划 Create，但计划不是写许可或能力完备证明。本片在单一 driver 内对缺键 Create **于通知/变更调用前拒绝** self_registry_unavailable；缺键 VerifyAbsent 可只读核实。测试确认键与祖先均未创建。安全创建父/键句柄仍未实现，不通过改规划内核或假称建键完成掩盖限制。 |
| 生命周期 / 阶段 | 会话新增同步独占 scope，将 driver、复核和 Dispose 串行；失败会话不可复用，保护保留至 owner Dispose，不让另线程中途释放目标/合作租约。可信同步回调须有界并捕获 owner token；回调或内核调用不能强制取消，Dispose 会等待，不能承诺立即退出。beforeNative 通知后再次核对会话、主体、持握键名及完整值；**MutationCallStarted 仅在变更 API dispatch 前置 true**，不指打开/查询 API，也不由通知本身置 true。单次 attempt 一次性，不承诺 callback 自身没做副作用。 |
| 错误与后态 | 每次变更 dispatch 后保存可取得的原始 NTSTATUS，分别尝试持握键后态与 fresh 会话后态；持握键失效不跳过 fresh 读取。HeldAfter 只有核名/主体/token 后置检查通过才发布；原生错误优先保留，写后读取/通知错误也不被匹配状态吞掉。Attempt 中的 HeldAfter / FreshAfter / PostcheckError 供**同步调用返回/抛出后**查验，非持久回执或所有权证明。相同 IO 异常/相同值漂移 Reason 在前后阶段可有不同 MutationCallStarted，禁止整体加入“确定未写”白名单；不回滚、不重放。 |
| FLOW / DATA | 最终[Debug](<docs/visual/sa-20261103/r68-self-native-value/debug-native.txt>) / [Release](<docs/visual/sa-20261103/r68-self-native-value/release-native.txt>)各 **21/21**：四种转移、其他值/子键保留、精确原始后态、attempt 不复用、空 GUID、缺键无创建、同一 IO / token / 值漂移在变更前后、删除重建等值命名空间拒绝旧句柄、SET deny 下 readonly noop、实际 DACL 二进制恢复与 SDDL 精确复核、owner Dispose 等待及同一初始计划双线程只能一次 dispatch。GUID-only race 缝在最后复核后删除自有键，使 **NtSetValueKey 与 NtDeleteValueKey 均实际返回映射 Win32 1018**；attempt 标记 true、原生 status 非零、held 读取不可用，但仍取得 fresh 缺键观测并保留原错误，没有假成功或恢复键。包为合成不可加载资产，不运行目标；不调用真实 Run/Boot 默认入口或切换账户。 |
| 审查 / 改动控制 | [安全审查与处置](<docs/visual/sa-20261103/r68-self-native-value/review-disposition.json>)记录回调有界约束、原生错误也采样后态的修正，以及缺键能力限制；先期子代理阅读不冒称最终 driver 全面审计。仅三项既有源变化：raw open 私有 helper 的可选 SET 位（默认读取行为不变）、会话同步 scope、组合测试夹具 internal 可见性。[正文还原核对](<docs/visual/sa-20261103/r68-self-native-value/existing-source-audit.json>)逐项逆转这些精确编辑后 SHA 与 R67 一致，其余 **49 项既有源未变**。 |
| ENG | [初次编译](<docs/visual/sa-20261103/r68-self-native-value/focused-first.txt>)被 xUnit2031 阻止，未运行测试；按[当前规则](<https://xunit.net/xunit.analyzers/rules/xUnit2031>)把两处 Where + Single 改为等价 predicate Single，不削弱断言，[聚焦复跑 21/21](<docs/visual/sa-20261103/r68-self-native-value/focused-second.txt>)。最终[54 项冻结源](<docs/visual/sa-20261103/r68-self-native-value/sources.json>)对应[20 步矩阵](<docs/visual/sa-20261103/r68-self-native-value/steps.json>)包含之后的 held 快照发布顺序微调：Debug/Release 构建各 **0 警告/0 错误**；Core 各 **616/616**、native 各 **21/21**、会话各 **32/32**、raw 各 **33/33**、合作租约各 **16/16**、目标各 **66/66**、旧历史 worker 各 **16/16**、Boot/注册各 **62/62**、七类 GUI 各 **53/53**，无跳过，见 [TRX 汇总](<docs/visual/sa-20261103/r68-self-native-value/test-summary.json>)。不是全量 integration、已发布包 self 写入旅程或 VIS/原生焦点证据。 |
| 清理 | [最终核对](<docs/visual/sa-20261103/r68-self-native-value/cleanup-after.json>)确认 **54 项冻结源差异 0、相关进程 0、临时候选 0、私有注册键 0、夹具服务 0、dump 0、diff check 0**。只删除精确路径、非 reparse 且空的公共测试父目录；所有变更/ACL/race 均在自有 GUID 根内回收。未删除生产控制目录或用户数据，保留已有未提交工作。 |

**仍未完成：** 仅现存键值机制，不是生产 self 执行器；安全建键、受信包准入、持久阶段记录/崩溃核对与 GUI/CLI 统一入口尚待实现。合作锁与顺序核对不是 key identity、原子 CAS、ABA/非合作写屏障，内存 attempt 也不能解释进程崩溃后的实际结果，匹配后态不证明调用所有权。Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 继续未完成，整体目标保持进行。

## 2026-11-03 — self 共享前后态检查会话（R67，内部 P0，原生执行器仍未接通）

本片继续 G1-self-write-r1，新增[内部检查会话](<src/SystemAutorun.Windows/Startup/SelfStartupPlanningSession.cs>)，把 R63 规划、R64 目标 pin、R65 跨 root 合作租约和 R66 完整原始观测接到同一所有者生命周期。没有新增原生 writer、Journal、RPC/CLI/GUI 路由或公开 schema，没有迁移 GUI 旧写路径；无用户交互或视觉变化，G2/VIS 为 N/A。此会话不是状态查询 API：取得合作租约可能创建保护控制目录与持久锁文件，即便最终计划无需原生写入。

| 类别 | 实现与实际证据 |
| --- | --- |
| 取得与三方主体 | 请求先验证 schema、确认、目标上下文和完整期望状态，非法/未确认/前置取消不调用任何工厂。先打开并核对目标与实际 primary actor，再 await 合作租约；等待后重新验证两租约，再读取当前原始态，并在前后复核实际主体及 target/coordinator/observation 的序数 SID 相等。不沿用等待前的登记态，不从请求采纳 exe、SID、hive、key 或 package 路径；默认入口仍以服务端 ProjectPaths 和当前进程事实派生。 |
| 前后态与失效 | 首次观测经 R63 规划；执行前检查重读同一受保护上下文，比较计划字段和值指纹，并拒绝键存在性变化，不能用相同 absent 值掩盖键消失。后置检查返回实际 Current 与 Reason：区分值不匹配和键上下文变化，Delete 不能因整个键消失而假验证；Create 可使原先缺失的键存在。Matches 只是现场后条件，不证明本会话实际调用了任何 native writer，更不是 Journal 成功。任一检查失败后会话不可复用，不修复、回滚或重放；保护保留至所有者 Dispose，供未来执行器先记录不确定性。 |
| 释放 / 并发 | busy、取消、任意工厂/读取故障与规划拒绝均释放已返回的目标和协调器租约，不依赖狭窄异常白名单。已取得会话的检查和 Dispose 在私有同步闸内串行，闸内无 await，避免多个 Verify 共用目标流位置；不同线程可使用合作文件句柄。Dispose 幂等，处置后从会话边界抛 ObjectDisposedException；目标、协调器、读取异常仍是阶段中立，不加入全局“确定未写”分类。 |
| 隔离 FLOW / DATA | [组合测试](<tests/SystemAutorun.IntegrationTests/SelfStartupPlanningSessionTests.cs>)最终 Debug/Release 各 **32/32**，见 [Debug](<docs/visual/sa-20261103/r67-self-planning-session/debug-session.txt>) / [Release](<docs/visual/sa-20261103/r67-self-planning-session/release-session.txt>)。覆盖四种转移及缺键创建/确认缺失、零工厂校验、无效包/假目标主体、忙等/取消释放、三方主体/坏元数据/错误上下文/过期前态/外来登记、等待后的目标及 scope 漂移、原始读取故障、键/值漂移后不可修复复用、后条件不匹配/删键、调用后读取故障/取消不回滚、四线程复核及两个不同 root 的真实合作竞争。使用三套既有真实隔离夹具：包为合成不可加载资产，文件 pin、保护目录/锁和 GUID 原生键操作为实际 Windows API；正向私有值改写由测试夹具执行，不是产品执行器。未运行真实 Run/Boot 或发布包默认入口，未执行目标或切换实际账户。 |
| 审查 / 最小改动 | [审查处置](<docs/visual/sa-20261103/r67-self-planning-session/review-disposition.json>)记录只读设计审查、夹具复用探索及主线最终实现核对；不把先期审查当最终代码审计。协调器仅新增一行只读 actor 事实，三套测试夹具仅由 private 改为 internal，不复制其原生实现；[四项正文还原核对](<docs/visual/sa-20261103/r67-self-planning-session/existing-source-audit.json>)逐项逆转唯一声明行并要求 SHA 与 R66 完全相同，其他 **46 项既有源未变**。审查中“真实读取的 false 通常是上下文过期”不直接采纳：Create 未由测试应用时，实际 absent 对 expected present 即真实不匹配，已有明确用例。 |
| ENG | [首轮编译失败](<docs/visual/sa-20261103/r67-self-planning-session/focused-first.txt>)仅是新测试缺 System.IO 及级联重载诊断，没有运行测试；补显式 using 后[聚焦 32/32](<docs/visual/sa-20261103/r67-self-planning-session/focused-second.txt>)，不降低断言。最终[52 项冻结源](<docs/visual/sa-20261103/r67-self-planning-session/sources.json>)对应[18 步双配置矩阵](<docs/visual/sa-20261103/r67-self-planning-session/steps.json>)：构建各 **0 警告/0 错误**；Core 各 **616/616**、会话各 **32/32**、原始读取各 **33/33**、合作租约各 **16/16**、目标各 **66/66**、旧历史 worker 各 **16/16**、Boot/注册各 **62/62**、七类 GUI 各 **53/53**，均无跳过，见 [TRX 汇总](<docs/visual/sa-20261103/r67-self-planning-session/test-summary.json>)。不是全量 integration、完整 self 写入旅程或视觉/原生焦点验收。 |
| 清理 | [最终核对](<docs/visual/sa-20261103/r67-self-planning-session/cleanup-after.json>)确认 **52 项冻结源差异 0、相关进程 0、临时候选 0、私有注册键 0、夹具服务 0、dump 0、diff check 0**。保护目录均在 GUID 临时根内随夹具回收，公共测试父目录仅在精确路径、非 reparse 且空时删除；没有删除生产协调目录、真实 Run/Boot 或其他用户数据，保留先前未提交工作。 |

**仍未完成：** 会话只组合检查与持锁，不授予原生写权限，不证明发布者信任/完整可执行依赖闭包，不是 key identity、原子 CAS 或 ABA/非合作写屏障。实际原生 writer、受信包准入、Journal 阶段敏感记录/结果核验和 GUI/CLI 统一接线仍待实现；不能把新异常类型整体视为未写。Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 继续未完成，整体目标保持进行。

## 2026-11-03 — self 固定项完整原始登记观测（R66，内部 P0，尚非执行器）

本片沿用 G1-self-write-r1，新增[固定项原始读取器](<src/SystemAutorun.Windows/Startup/SelfStartupNativeRead.cs>)与[私有注册键测试](<tests/SystemAutorun.IntegrationTests/SelfStartupNativeReadTests.cs>)，没有修改既有源码。没有新增原生写入口、CLI/GUI/Agent 路由、公开 schema 或 Journal 白名单；R15 只读文本状态保持原状，不用其裁掉 NUL 后的内容构造写入前态。本片没有用户交互或视觉变化，G2/VIS 为 N/A。

| 类别 | 实现与实际证据 |
| --- | --- |
| 固定原生定位 | [生产入口](<src/SystemAutorun.Windows/Startup/SelfStartupNativeRead.cs#L13-L17>)只取当前实际 primary SID、固定 64 位 Run 路径及固定值名，不取 caller hive/key/name/SID、环境、exe 或缓存 HKCU。先打开并核对实际 actor hive，再以其句柄打开固定子键；请求仅 KEY_QUERY_VALUE 与 64 位视图，使用 OBJ_DONT_REPARSE，未申请 SET/CREATE/备份恢复权。hive 不存在/不可读、模拟主体及原生错误均拒绝，不冒称 absent。测试缝只接受非空 GUID，派生固定私有前缀，不接受任意路径或 SID。 |
| 完整指纹 / 有界 ABI | [原字节读取](<src/SystemAutorun.Windows/Startup/SelfStartupNativeRead.cs#L68-L91>)使用 [NtQueryValueKey 的 partial information](<https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwqueryvaluekey>)，12 字节头加契约派生的 **16,386 字节**数据预算，固定总缓冲 **16,398 字节**，逐项检查返回长度/头/数据长度。任意原生 uint 类型与实际字节送入 R63 指纹，不解码、扩展、修补或截断 NUL，不按外部长度增长分配或循环重试。只有明确 value-not-found 才是值缺失；键缺失另以 KeyPresent 保留，拒绝/未读不会产出伪造的空指纹。 |
| 名称 / 主体 / 漂移 | [观测流程](<src/SystemAutorun.Windows/Startup/SelfStartupNativeRead.cs#L28-L65>)持有初始键，读取后复核主体/hive/键名，再打开当前命名空间核对键存在性与完整指纹，最后复读原持有键及两边名称。名称通过 [NtQueryKey](<https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwquerykey>) 有界读取，按注册表大小写语义与服务端固定 native path 比对，不把名字或最后写入时间叫对象 ID。删键 1018、链接 4395、改名与值/类型/存在性漂移均实测拒绝；等值 ABA、非合作写与后续变化没有原子保证，也没有隐式获得 R65 合作锁。 |
| 隔离 DATA / FLOW | 最终 Debug/Release 原始观测各 **33/33**，见 [Debug](<docs/visual/sa-20261103/r66-self-raw-registry/debug-raw.txt>) / [Release](<docs/visual/sa-20261103/r66-self-raw-registry/release-raw.txt>)。覆盖七种原生类型、坏 UTF-16/奇数长度/NUL、不存在的键与值分离、0–3/上界前一字节/上界/超界、同 SID 模拟拒绝、前置/中途取消、精确 ACL 拒绝及恢复、八类键/值漂移、真实父级和末级 REG_LINK。五组原字节接入 R63 内存规划：仅 REG_SZ 与恰好一个终止 NUL 被认作相同目标；缺/多 NUL、EXPAND_SZ、Binary 冲突。所有改写、ACL 与链接目标均在当前主体的独占 GUID 私有键内，不执行任何目标、不读写真实 Run/Boot、不切换实际账户或卸载 hive；生产默认入口没有现场执行。 |
| 真实失败与根因 | 首次[22/28](<docs/visual/sa-20261103/r66-self-raw-registry/focused-first.txt>)揭示 RegQueryValueExW 会改变三种异常字符串的返回缓冲，不能充当实际原字节；另有两例链接清理失败和一例新 DACL 的 AI 规范化差异。读取改用 NT 原字节；ACL 先建立等效稳定 setter 基线再备份，仍逐字恢复核对；链接用原创建句柄 NtDeleteKey 删除后再清根，[第二轮 28/28](<docs/visual/sa-20261103/r66-self-raw-registry/focused-second.txt>)。扩展后[32/33](<docs/visual/sa-20261103/r66-self-raw-registry/focused-third.txt>)揭示 RegSetValueExW 把未终止 REG_SZ 的夹具从 142 字节补成 144，读取器正确反映存储值；夹具改用 NtSetValueKey 真正种入未终止原数据，没有降低指纹或规划断言。中间失败保留，最终按 33 例重跑两配置。 |
| 审查与工程 | [审查处置](<docs/visual/sa-20261103/r66-self-raw-registry/review-disposition.json>)区分旧版本静态审查、主线最终 ABI 复核与真实测试，不把早期“无不安全接受”当原字节正确性证明。夹具现在核对实际 primary SID 的 software/root 物理名称、拒绝既存叶并在删除后复验不存在；链接/删键原因断言由宽泛集合收紧为实测 4395/1018。[50 项冻结源](<docs/visual/sa-20261103/r66-self-raw-registry/sources.json>)对应[16 步矩阵](<docs/visual/sa-20261103/r66-self-raw-registry/steps.json>)：Debug/Release 构建各 **0 警告、0 错误**，Core 各 **616/616**、原始读取各 **33/33**、R65 合作租约各 **16/16**、R64 目标各 **66/66**、旧历史 worker 各 **16/16**、Boot/注册各 **62/62**、七类 GUI 各 **53/53**，无跳过。[TRX 汇总](<docs/visual/sa-20261103/r66-self-raw-registry/test-summary.json>)保留三个聚焦轮次与最终矩阵；不是全量 integration 或完整 self 写后/视觉验收。 |
| 清理 / 保留原工作 | 首轮遗留的两个 GUID 根先[逐项检查](<docs/visual/sa-20261103/r66-self-raw-registry/first-cleanup-inspection.json>)，再以[限定脚本](<docs/visual/sa-20261103/r66-self-raw-registry/cleanup-first-links.ps1>)核对 native 名称和根内目标，OPEN_LINK 取得实际链接句柄后删除，最后仅清空的已验证父/root；[清理回读](<docs/visual/sa-20261103/r66-self-raw-registry/first-cleanup-after.json>)保留。最终[清理与源码核对](<docs/visual/sa-20261103/r66-self-raw-registry/cleanup-after.json>)为 **48 项既有源变化 0、50 项冻结源差异 0、相关进程 0、临时候选 0、私有注册键 0、夹具服务 0、dump 0、diff check 0**；没有清理其他用户数据或修改生产 ACL。 |

**仍未完成：** 这个 observer 不授予写权限，也不把自身异常加入“确定未写”分类。R63 规划、R64 目标、R65 合作协调和 R66 原始观测仍待组成共享受保护执行器；包信任边界、实际原生写入与阶段敏感记录/写后核验、GUI/CLI 统一迁移尚未关闭。Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 继续未完成，整体目标保持进行。

## 2026-11-03 — self 按主体/固定项跨 root 合作租约（R65，内部 P0，端到端仍未完成）

本片沿用 G1-self-write-r1，新增[内部合作租约](<src/SystemAutorun.Windows/Startup/SelfStartupAdmissionLease.cs>)与[隔离测试](<tests/SystemAutorun.IntegrationTests/SelfStartupAdmissionTests.cs>)，仅在[既有测试入口](<tests/SystemAutorun.IntegrationTests/StartupHistoryCrashHost.cs>)加入两行 worker 分派。没有新增 GUI/CLI/Agent 写路由、真实 Run/Boot 写入或 Journal 分类；现有 GUI 旧写路径仍未迁移。本片没有用户交互或视觉变化，G2/VIS 为 N/A，不用测试截图冒充视觉或原生交互验收。

| 类别 | 实现与实际证据 |
| --- | --- |
| 固定合作作用域 | [生产入口](<src/SystemAutorun.Windows/Startup/SelfStartupAdmissionLease.cs#L33-L34>)只取实际主令牌 SID 与 OS LocalAppData known-folder；[原生解析](<src/SystemAutorun.Windows/Startup/SelfStartupAdmissionLease.cs#L81-L95>)使用 SHGetKnownFolderPath、DONT_VERIFY、非模拟主体前后复核，没有环境变量、caller SID、exe、hive、key、root 或当前目录回退。固定 actor 哈希目录和固定 HKCU Run SystemAutorun 锁叶不随 root、包或 await 线程变化。内部显式 callback 仅为临时存储测试缝，不是请求字段。 |
| 创建与租约 | [创建/争用](<src/SystemAutorun.Windows/Startup/SelfStartupAdmissionLease.cs#L43-L72>)复用原子 owner/protected actor+SYSTEM+Admin ACL 创建助手及 R64 的普通本地目录祖先 pin，拒绝重解析、未知身份/大小写与不规范别名；先核对 base/container ACL，最多创建两个固定层级。锁为零长、单链接、受保护普通文件，OPEN_ALWAYS、share=0；不截断、不修 ACL、不存 PID/期望态、不 DeleteOnClose。只对 32/33 做有界可取消等待，默认 15 秒；关闭句柄释放，不删除长期协调文件。 |
| 复验与限制 | [复验](<src/SystemAutorun.Windows/Startup/SelfStartupAdmissionLease.cs#L109-L140>)重新核对主体、known-folder 映射、目录身份/大小写与同句柄 ACL，并检查锁的元数据及新命名空间身份。新打开只申请属性/安全查询权，不申请会与 share=0 冲突的数据读取权；该区别经实测，不采纳“所有二次打开均失败”的笼统推断。[审查处置](<docs/visual/sa-20261103/r65-self-admission/review-disposition.json>)保留边界：这只是相同 OS known-folder 命名空间内的合作序列化，不证明同 SID 的不同 profile 映射仍唯一，不是注册表 CAS/ABA、防同 SID/admin 篡改、写授权或包信任；不可用/含额外写 ACE 的容器保守拒绝，不修用户 profile。 |
| 真实隔离 DATA / FLOW | 最终[16/16 聚焦](<docs/visual/sa-20261103/r65-self-admission/focused-fifth.txt>)覆盖跨线程释放/同对象重取、空锁留存、持有期目录拒绝改名、争用超时、取消、参数预算、假 SID/映射漂移、base/actor 目录/锁 ACL 拒绝且不修复、非零数据不截断、硬链接/重解析拒绝。[两个物理 root 的独占 apphost](<tests/SystemAutorun.IntegrationTests/SelfStartupAdmissionTests.cs#L134-L156>)以实际独立进程争用同一固定槽；核对映像/PID后强退 holder，另一 root 可取得同一路径。所有写测试使用实际当前主体及自有私有临时目录，不使用通用 pwsh 映像，不写生产控制存储或真实注册项。生产 known-folder 仅在隔离 worker 中只读核对不受 LOCALAPPDATA 环境覆盖；未实际切换 Windows 账户或证明跨 profile 唯一。 |
| 中间失败保留 | 两次编译失败修正缺失 using 与 nullable 断言；首轮执行的[6/15](<docs/visual/sa-20261103/r65-self-admission/focused-third.txt>)正确拒绝了继承额外写权限的假 base，改为私有夹具并补测 base ACL，没有放宽产品门槛。[14/16](<docs/visual/sa-20261103/r65-self-admission/focused-fourth.txt>)的两例是临时 ACL 回写将新建 P 规范化为 PAI，夹具先建立等效稳定基线再备份，仍显式恢复二进制 DACL并逐字核对 SDDL；之后为最终 16/16。 |
| ENG / 挂起根因 | [r2 命令与时序](<docs/visual/sa-20261103/r65-self-admission-r2/steps.json>)中 Debug/Release 构建各 **0 警告、0 错误**，Core 各 **616/616**、合作租约各 **16/16**、R64 目标各 **66/66**、旧历史 worker 各 **16/16**、Boot/注册筛选各 **62/62**。首轮 GUI 长等被显式中断，r2 在已完成 17 项后由 90 秒无 dump 超时中止，均不记通过。[根因复现](<docs/visual/sa-20261103/r65-self-admission-r2/environment-null-proof.json>)证明新增 PowerShell 脚本的 .NET null 参数被转为空串；[已有手动分支](<tests/SystemAutorun.IntegrationTests/StartupGuiWindowTests.cs#L132-L151>)以 probe is not null 判定，误入 15 分钟等待，不是已证明的 Dispatcher 竞态。[独立 r3 修复脚本](<docs/visual/sa-20261103/r65-self-admission-r3/run-gui-recovery.ps1>)只移除具名进程变量并断言原生 null/实际不存在，不改 C# 或断言；同一冻结源及编译字节的 [Debug GUI](<docs/visual/sa-20261103/r65-self-admission-r3/debug-gui.txt>) / [Release GUI](<docs/visual/sa-20261103/r65-self-admission-r3/release-gui.txt>)均 **53/53**。[合并 TRX 汇总](<docs/visual/sa-20261103/r65-self-admission-r3/test-summary.json>)保留失败项，不把 failed suite 的 17 个 completed case 算整组通过。[根因与审查线索处置](<docs/visual/sa-20261103/r65-self-admission-r3/gui-hang-resolution.json>)未采纳无现场支持的线程/集合修补。 |
| 清理 / 原工作保留 | [47 项冻结源](<docs/visual/sa-20261103/r65-self-admission-r3/sources.json>)与[6 项 Debug/Release 编译资产](<docs/visual/sa-20261103/r65-self-admission-r3/assemblies.json>)最终差异均为 0。45 项既有基线中只有测试入口改变；[剥除两行 hook 的字节核对](<docs/visual/sa-20261103/r65-self-admission/worker-hook-audit.json>)与此前完整文件摘要一致。两次中断只回收各自已验证进程/临时根；误探针生成的三张 PNG 与 ready 文件位于测试 bin，时间、内容及哈希归属均核对后删除，历史证据不动。[最终清理](<docs/visual/sa-20261103/r65-self-admission-r3/cleanup-after.json>)为相关进程 0、临时候选 0、源码/编译差异 0、diff check 0；无 dump，未以跳过用例、改 UI 或删除断言掩盖失败。 |

**边界仍未关闭：** R63 规划、R64 目标核验与本合作租约尚未组成完整受信执行器。包信任、独占原生前读/写后回读、阶段敏感操作记录及 GUI/CLI 统一迁移仍待实现；此租约不授予原生写入权限，也不改变未知结果不可盲重放的约束。Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 继续保持未完成，整体目标继续进行。

## 2026-11-03 — self 发行资产与主体/root 绑定（R64，内部 P0，写入接线未完成）

本片沿用下节 G1-self-write-r1，新增[清单读取器](<src/SystemAutorun.Windows/Startup/SelfStartupPackageManifest.cs>)、[只读目标租约](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs>)及[隔离测试](<tests/SystemAutorun.IntegrationTests/SelfStartupTargetTests.cs>)。没有新增 CLI/Agent 路由、原生写入口、GUI 接线或公开请求字段；纯后端事实采集无交互/视觉变化，G2/VIS 为 N/A。现有 GUI 的旧 self 写调用仍未迁移，不能据此认为旧写路径已获得本保护。

| 类别 | 实现与实际证据 |
| --- | --- |
| 服务端固定目标 | [生产入口](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs#L16-L19>)只取当前进程、AppContext 及实际 Windows 组件的布局事实，固定派生包根下的 GUI；只接受 App 或 runtime Agent 的既定布局，不采用任意 exe、CLI/Helper 宿主、开发目录探测或环境变量 Agent 路径作为替代。内部显式环境缝仅供合成包测试，不是请求接口。宿主及组件的磁盘路径必须在列出的已核对资产中；这不是运行中映像字节与磁盘逐字节相同的证明。 |
| 主体与物理 root | [主体/目录绑定](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs#L40-L63>)复用现有主令牌 SID 门槛，并在核验前后复读主体。root 的“当前服务端上下文”由已解析的 ProjectPaths 提供，不接受客户端 rootAssociation 充当授权。目录全祖先拒绝重解析并持有无 Delete 共享的句柄，记录真实卷/文件 ID 与大小写语义，物理拼写按 Ordinal 核对；词法相等不是对象身份。未知大小写语义、无法取得稳定 ID、不规范别名及不可读本地卷均拒绝，不降级成 absent。未实测 FAT/exFAT 或其他卷的兼容性。 |
| 有界资产核对 | [schema-1 读取器](<src/SystemAutorun.Windows/Startup/SelfStartupPackageManifest.cs#L9-L69>)拒绝重复/未知字段、危险路径、重复别名及不合法元数据，复用既有 DOS/ADS/设备名词法规则。预算为清单 2 MiB、4096 项、单项 512 MiB、合计 1 GiB；要求与发布脚本一致的 33 个固定资产。[文件打开与复验](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs#L180-L249>)使用持有父目录的 NT 相对只读打开，拒绝重解析、非普通文件及多链接，按原始长度/SHA256 核对清单；四个固定 EXE 另核对 x64 PE32+、GUI/Console 子系统及 bundled-runtime 元数据。清单/运行时 JSON 使用与持有指纹一致的同一读取字节，不混用另一次未经比对的解析结果。 |
| 上下文指纹 | [服务端摘要](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs#L89-L99>)绑定主 SID、物理 root/包根的路径与对象/大小写事实、清单身份/原始摘要、固定命令，以及按 Ordinal 排序的资产路径/对象/长度/摘要。App 与 Agent 对同一包/root/主体给出相同 ID；角色与观测时间不进入摘要。ID 不是秘密能力、配置版本或原生 CAS；本层不证明配置内容未变，也不授予 Windows 写入权限。 |
| 拒绝与安全复核 | [只读审查处置](<docs/visual/sa-20261103/r64-self-target/review-disposition.json>)经主线源码复核，补齐[统一目标核验异常](<src/SystemAutorun.Windows/Startup/SelfStartupTargetLease.cs#L255-L259>)，避免原始 PE/JSON/本地化 SSH 错误直接充当对外拒绝界面；取消与已释放 owner 误用保持原语义。复验沿用各文件原始预算，并分别检查当前命名空间和持有对象的元数据/字节。不因句柄共享限制删除必要复验，也不声称能对抗所有非合作改写。未修改 Journal 白名单；未来原生调用后遇到此异常，仍必须按实际执行阶段判断未知结果，不能全局解释为“确定未写”。 |
| 隔离 DATA / FLOW | 最终[聚焦 66/66](<docs/visual/sa-20261103/r64-self-target/focused-final.txt>)覆盖 App/Agent 摘要一致、固定宿主/组件、原始哈希与长度、清单输入/预算、PE 与运行时形状、主体漂移、取消、缺失/占用、目录改名、真实 NTFS 重解析/硬链接拒绝、Unicode/空格 root、拒绝后释放及额外数据不锁定。全部位于独占临时根，使用不可运行的合成 PE/占位 DLL 与内部环境缝；没有启动发行 GUI/Agent、实际切换 Windows 账户、写真实 Run/Boot 或宣称真实发行包成功准入。生产 ambient 入口只实测测试宿主拒绝。首轮[47/49](<docs/visual/sa-20261103/r64-self-target/focused-first.txt>)的两例是断言漏认 InvalidDataException，产品已拒绝危险对象；修正断言后为[49/49](<docs/visual/sa-20261103/r64-self-target/focused-second.txt>)，补强为[55/55](<docs/visual/sa-20261103/r64-self-target/focused-third.txt>)，再完成审查修订后的最终 66 例，不覆盖失败记录。 |
| ENG | [41 项冻结源](<docs/visual/sa-20261103/r64-self-target/sources.json>)对应[10 步命令/时序](<docs/visual/sa-20261103/r64-self-target/steps.json>)。Debug/Release 构建各 **0 警告、0 错误**；Core 各 **616/616**、目标核验各 **66/66**、Boot/注册筛选各 **62/62**、七类 GUI 筛选各 **53/53**，无跳过。[Debug 构建](<docs/visual/sa-20261103/r64-self-target/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r64-self-target/debug-core.txt>) / [目标](<docs/visual/sa-20261103/r64-self-target/debug-target.txt>) / [注册](<docs/visual/sa-20261103/r64-self-target/debug-registration.txt>) / [GUI](<docs/visual/sa-20261103/r64-self-target/debug-gui.txt>)；[Release 构建](<docs/visual/sa-20261103/r64-self-target/release-build.txt>) / [Core](<docs/visual/sa-20261103/r64-self-target/release-core.txt>) / [目标](<docs/visual/sa-20261103/r64-self-target/release-target.txt>) / [注册](<docs/visual/sa-20261103/r64-self-target/release-registration.txt>) / [GUI](<docs/visual/sa-20261103/r64-self-target/release-gui.txt>)。[TRX 汇总](<docs/visual/sa-20261103/r64-self-target/test-summary.json>)保留中间失败。不是全量 integration、原生写入或视觉验收。 |
| 清理 / 原工作保留 | [初查](<docs/visual/sa-20261103/r64-self-target/cleanup-inspection.json>)仅余空测试父目录，核对实际绝对路径、非 reparse、空目录和无相关进程后只做非递归删除。[清理后复核](<docs/visual/sa-20261103/r64-self-target/cleanup-after.json>)为冻结源差异 0、与本轮开始的 37 项既有源码/配置比较差异 0、相关进程 0、临时候选 0。本片仅新增上述三项源码及证据，不覆盖既有未提交工作。 |

**这不是完整包信任或写入准入完成：** 同目录可编辑 manifest 只证明所列资产字节一致，不证明发布者、签名、ACL 信任或完整可执行依赖闭包。本层不枚举包树；未列出的文件/目录（包括额外代码）既不被证明不存在，也不被授信或锁定。一次 Open 会读取资产约两遍，后续每次 Verify 再读一遍，不能直接接到 GUI 高频状态刷新。后续仍须补全受信包准入边界、跨 root 合作协调、独占原生读取/写后回读及阶段敏感的独立操作记录，再统一迁移 GUI/CLI；本只读租约不是固定 Run 项的写租约。

**仍未完成：** self 端到端共享保护与接线、Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功、日志 G3 与 SA01–SA10 均保持未完成，整体目标继续进行。

## 2026-11-03 — self 共享请求与状态转移内核（R63，仅 P0，self 端到端未完成）

本片沿用下节已批的 G1-self-write-r1，只新增[共享模型/规划内核](<src/SystemAutorun.Core/Models/SelfStartupMutation.cs>)及[隔离测试](<tests/SystemAutorun.Core.Tests/SelfStartupMutationTests.cs>)。没有改现有 CLI/Agent 路由、只读 schema、GUI 接线或旧 self 写入；没有开放新原生写入口。纯内存规划无用户交互/视觉变化，G2/VIS 为 N/A，不重开已批视觉。

| 类别 | 实现与实际证据 |
| --- | --- |
| 请求契约 | [schema-1 请求与专用读取器](<src/SystemAutorun.Core/Models/SelfStartupMutation.cs#L21-L44>)要求显式提供 enabled、confirmed、targetContextId、expectedRegistration 与版本；缺失布尔不默认为 disable。使用固定 camelCase、required 成员及 [.NET 10 拒绝重复属性](<https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries#option-to-disallow-duplicate-json-properties>)，未知字段、大小写别名及嵌套重复属性均拒绝；输入上限沿用现有请求预算。payload 不接受 executable/hive/key/rootAssociation/verified/configurationRevision/operationId，操作 ID 留给未来信封层。未来入口必须调用这个专用读取器，不能直接套用宽松的全局 JSON 选项。 |
| 完整原生前态 | [独立原生指纹](<src/SystemAutorun.Core/Models/SelfStartupMutation.cs#L9-L19>)包含 registered、原生 uint 类型、原始字节长度及 SHA256，原始数据最多 16,386 字节，不裁切。缺失必须是规范空记录；坏元数据或不可读不当作 absent。不能复用 R15 已解码并裁掉尾部 NUL 的状态作为写入前态。规划目标仅匹配 REG_SZ、UTF-16LE 与恰好一个终止 NUL 的完整指纹；大小写变动、额外/缺失 NUL、其他 root/包、额外参数、REG_EXPAND_SZ 及其他类型均拒绝接管。哈希比较不冒充全局原生 CAS。 |
| 状态规划 / 回读 | [四种计划](<src/SystemAutorun.Core/Models/SelfStartupMutation.cs#L46-L109>)为 Create、Delete、VerifyEnabled、VerifyAbsent，后两种不要求原生写，但仍需真实核验后才能发布成功。缺确认、上下文变化、前态变化、非法或外来登记均无计划并返回确定拒绝码。targetContextId 不是秘密能力；两个服务端参数必须来自后续受信执行器，不能由请求或词法关联充当授权。包、实际主体 SID、物理 root 的核验/绑定尚未实现。本层没有 lease、journal、注册表/任务操作、进程控制、成功回执或回滚；写后不匹配仅表示未确认，未来执行器须按实际阶段处理未知结果，不盲重放。 |
| 隔离验证 | 最终[聚焦回归 86/86](<docs/visual/sa-20261103/r63-self-policy/focused-final.txt>)通过，覆盖四态、30 个外来值场景、确认/上下文/完整前态、坏状态、UTF-16、原始长度/终止符、输入不变性、JSON 缺失/重复/别名/未知字段及回读失配。全部使用合成字节、虚构路径和上下文，没有实际 Windows 主体、发行包或注册写入证明。[较早 77 例记录](<docs/visual/sa-20261103/r63-self-policy/focused-debug.txt>)是补严格读取器前的中间版本，不冒充最终冻结版。 |
| ENG | [29 项冻结源](<docs/visual/sa-20261103/r63-self-policy/sources.json>)对应[命令/时序](<docs/visual/sa-20261103/r63-self-policy/steps.json>)。Debug/Release 构建各 **0 警告、0 错误**；Core 各 **616/616**、Boot/注册状态筛选各 **62/62**、七类 GUI 筛选各 **53/53**，无跳过。[Debug 构建](<docs/visual/sa-20261103/r63-self-policy/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r63-self-policy/debug-core.txt>) / [注册](<docs/visual/sa-20261103/r63-self-policy/debug-registration.txt>) / [GUI](<docs/visual/sa-20261103/r63-self-policy/debug-gui.txt>)；[Release 构建](<docs/visual/sa-20261103/r63-self-policy/release-build.txt>) / [Core](<docs/visual/sa-20261103/r63-self-policy/release-core.txt>) / [注册](<docs/visual/sa-20261103/r63-self-policy/release-registration.txt>) / [GUI](<docs/visual/sa-20261103/r63-self-policy/release-gui.txt>)。[TRX 汇总](<docs/visual/sa-20261103/r63-self-policy/test-summary.json>)可核对。这不是全量 integration、原生写入或 CLI parity 完成证据。 |
| 清理 / 原工作保留 | [初查](<docs/visual/sa-20261103/r63-self-policy/cleanup-inspection.json>)仅余空测试父目录；核对实际绝对路径、非 reparse、空目录、无相关进程后只做非递归删除。[最终复核](<docs/visual/sa-20261103/r63-self-policy/cleanup-after.json>)为冻结源差异 0、所核对的 27 项既有源码变化 0、仅新增模型与测试两项、相关进程 0、临时候选 0。 |

**后续执行器门槛未被这个内核代替：** [现有发行包 manifest](<docs/deploy.md#L29-L54>)仅提供字节完整性，不是发布者签名或信任决策，不能把文件名/存在性当作已核验包。[现有 startup 租约](<src/SystemAutorun.Windows/Startup/StartupMutationLease.cs#L6-L28>)明确只是同物理 root 互斥，不是固定 HKCU Run 值的跨 root 租约。下一片先补固定发行布局的运行时核验、实际主体/物理 root 绑定及按主体/固定项的合作协调，再接独占键的原生读取/写后核验、操作记录和 GUI/CLI。[Journal 的阶段敏感未知结果分类](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L268-L275>)也必须按新命令核对，不能将写后故障加成全局“确定未写”白名单。

**仍未完成：** self 的完整共享保护及 CLI/GUI 接线、Boot 写契约、GUI 恢复控制、原生键盘/焦点/高 DPI、远端真实成功路径、日志 G3 与 SA01–SA10 均未关闭，整体目标继续进行。

## 2026-11-03 — Boot 卸载失败判定修复与 CLI 写入前置核对（R62，整体未完成）

本片没有开放 CLI 原生注册写命令。核对现有实现后，先按修复路径纠正既有 Boot 卸载的假成功判定；固定任务名称、权限、安装目标和操作范围不变，没有新增界面或视觉状态，G2/VIS 对此后端切片为 N/A。原批准范围继续有效。

**根因与最小修复：** 旧 `Uninstall` 将 GetFolder/DeleteTask 的所有 COM 故障转成 false，而[Helper 调用](<src/SystemAutorun.Elevated/Program.cs#L25-L40>)忽略该布尔值，可能对权限拒绝或调度器故障返回成功；末尾的旧 GetStatus 还会把读取故障当作不存在。[修复后的共享函数](<src/SystemAutorun.Windows/Scheduling/BootTaskScheduler.cs#L116-L167>)只将 HRESULT 2/3 的 COM 或 CLR 映射异常认作缺失。删除后必须先经固定 GetTask 确认缺失，才能返回 true；任务仍在、返回 null 或回读失败均不报成功。只有任务缺失已经得到确认之后，空文件夹清理才可 best-effort。未修改其他旧 GetStatus/安装路径的宽泛异常处理，不宣称这些路径一并修好。[DeleteTask](<https://learn.microsoft.com/en-us/windows/win32/taskschd/taskfolder-deletetask>) 本身无返回值，必须区分调用结束与后置状态。

| 类别 | 实际证据与限制 |
| --- | --- |
| 根因回归 | 在内部注入缝保留旧错误处理逻辑，先[复现 27 例中 12 例失败](<docs/visual/sa-20261103/r62-boot-uninstall/before-fix.txt#L127>)；之后补 6 个 CLR 映射边界并修复，聚焦回归[33/33 通过](<docs/visual/sa-20261103/r62-boot-uninstall/focused-after.txt>)。[新增共 22 例](<tests/SystemAutorun.IntegrationTests/BootTaskSchedulerTests.cs#L79-L163>)覆盖 lookup/delete/verify 的权限与服务故障、缺失、删除未生效、null 返回、验证与可选清理的时序；不吞掉关键阶段故障、不跳过失败例。 |
| ENG | [24 项冻结源](<docs/visual/sa-20261103/r62-boot-uninstall/sources.json>)对应[执行命令/时序](<docs/visual/sa-20261103/r62-boot-uninstall/steps.json>)。Debug/Release 构建各 **0 警告、0 错误**；Core 各 **530/530**、Boot 与注册读取/真实 pipe CLI 状态筛选各 **62/62**、七类 GUI 筛选各 **53/53**，无跳过。[Debug 构建](<docs/visual/sa-20261103/r62-boot-uninstall/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r62-boot-uninstall/debug-core.txt>) / [注册](<docs/visual/sa-20261103/r62-boot-uninstall/debug-registration.txt>) / [GUI](<docs/visual/sa-20261103/r62-boot-uninstall/debug-gui.txt>)；[Release 构建](<docs/visual/sa-20261103/r62-boot-uninstall/release-build.txt>) / [Core](<docs/visual/sa-20261103/r62-boot-uninstall/release-core.txt>) / [注册](<docs/visual/sa-20261103/r62-boot-uninstall/release-registration.txt>) / [GUI](<docs/visual/sa-20261103/r62-boot-uninstall/release-gui.txt>)。不是全量 integration 或全部 CLI parity。[TRX 汇总](<docs/visual/sa-20261103/r62-boot-uninstall/test-summary.json>)保留修复前失败结果。 |
| FLOW / DATA | 新的卸载测试全部使用公开托管 Scheduler doubles，经与生产相同的卸载函数执行固定 folder/task 调用，不创建任何真实计划任务、触发器或 SYSTEM 工作负载。注册状态测试继续覆盖独占键与独占 pipe 配置。未写真实 Run、真实 Boot、生产配置或远端，未执行会触及生产任务的 Helper；Helper 实际退出码、原生卸载及提权 GUI 错误旅程仍未实测。删除后的回读故障只表示无法确认，不表示任务肯定没被删除；不承诺对外部非协作写者的原子保证。 |
| 清理 / 原工作保留 | [初查](<docs/visual/sa-20261103/r62-boot-uninstall/cleanup-inspection.json>)仅余空的共享测试父目录；复核绝对路径、空目录、非 reparse、无相关进程后仅非递归移除。[最终复核](<docs/visual/sa-20261103/r62-boot-uninstall/cleanup-after.json>)为冻结源差异 0、24 项所核对源码中，与本轮开始相比仅共享 Scheduler 与其测试两项变化、相关进程 0、临时候选 0。既有未提交工作保留。 |

**CLI 写入尚不能按“缺一条接线”处理：** [R15](<docs/architecture.md#L1065-L1086>)只批准只读观测，明确 rootAssociation 不是授权；[后续门槛](<docs/architecture.md#L1299-L1300>)及[原生写入约束](<docs/architecture.md#L1364-L1368>)仍有效。主线已核对 [GUI self 写入](<src/SystemAutorun.Windows/Startup/UserStartupRegistration.cs#L21-L41>)无前态比较，可覆盖其他 root 的固定 Run 值；现有观测没有写入主体/目标授权、跨 root 协调和未知结果处理。安全子代理的只读审查仅作线索，本结论以这些源码和契约为依据，不把“命令族已列入 G1”当作四个原生写命令的实现许可。

**受影响 G1 补充已批准（G1-self-write-r1，2026-11-03）：** 用户在 `self-write-g1-boundary` 选择“按此安全分片推进”，批准先补 self enable/disable，只接受当前 Windows 主体、当前 root 与已核验发行包的固定 GUI 入口，不接受任意 exe/hive/key。enable 只允许缺失或完全相同的目标，disable 只允许完全相同的目标或已缺失；外来、其他 root、未知和非文本登记一律拒绝，不接管、不迁移。GUI/CLI 共用写入保护；显式确认、完整原生前态重读、按主体与固定注册项的跨 root 合作协调、独立操作记录及写后核验必须齐备，configurationRevision 不充当原生 CAS。结果不确定即停止并供查验，不盲重放或自动回滚；CLI 不自动 UAC。Boot 写入另行细化已提权终端与受保护部署边界，不照搬旧 Helper。现有 G1/G2 其余内容不重批；该批准不是实现或验收完成，也不是操作真实 Run/Boot 的测试许可。下一切片先落共享契约与可隔离验证的 self 写入保护，再接 CLI/GUI；尚未开放新写命令。收到批准后[重新核对](<docs/visual/sa-20261103/r62-boot-uninstall/post-approval-check.json>)，24 项冻结源差异 0、相关进程 0、临时候选 0。

**仍未完成：** 四个 CLI 自启写命令、GUI 恢复控制、原生键盘/焦点/高 DPI 与远端真实成功路径仍未完成；日志 G3、SA01–SA10 和整体目标保持未完成。

## 2026-11-03 — 日志正常模态返回的隔离回归（R61，日志 G3 未完成）

本片只补[已批日志旅程 LG01/04/05/07](<docs/architecture.md#2026-11-03--gui-日志旅程接续与-g2-候选r58>)的程序化覆盖，**没有修改产品源码或视觉**。布局、权限、来源、导出及批准范围保持不变；不重开相同 G1/G2，也不把新增测试当作原生交互验收。

[四个新场景](<tests/SystemAutorun.IntegrationTests/LogViewerGuiTests.cs#L254-L350>)覆盖“服务/全局入口 × 返回/合成 Escape”。全局场景核对真实 MenuItem 的绑定与空参数，不打开原生 popup；服务场景核对服务参数。两者通过真实 MainWindow 接线进入 `ShowDialog`，分别核对首次来源身份与不自动追踪，再选同一个业务来源、启动追踪、从独占文件追加事件并经真实 pipe 读到新增内容。

| 类别 | 实际证据与限制 |
| --- | --- |
| FLOW / 资源 | [聚焦回归 4/4](<docs/visual/sa-20261103/r61-log-journey/focused-debug.txt>)及最终矩阵均通过。关闭经过真实返回 handler 或[合成 KeyEventArgs 路由](<https://learn.microsoft.com/en-us/dotnet/api/system.windows.input.keyeventargs.-ctor?view=windowsdesktop-10.0>)，不是物理按键。[全局 Escape 轨迹](<docs/visual/sa-20261103/r61-log-journey/matrix-captures-debug/global-escape.txt>)确认先正常返回并完成子窗资源等待、保留主屏选择与搜索，最后才回收 owned main/STA；没有用提前关 Dispatcher 解开模态帧。四例均断言仅一次 Closed、FollowTask/ShutdownTask 已正常结束、busy/follow/export 关闭、主屏仍显示且 Dispatcher 尚未 shutdown。 |
| DATA | 实际业务日志/Named Pipe 读取均在独占 ServerFixture 内，关窗后 Agent 可 Ping、未触发停止、配置 revision 不变。主屏行身份与 unknown 健康信息是明确的合成 UI 数据，不是实际工作负载或 uptime 证明。没有触及生产配置、真实服务、远端或注册状态。 |
| ENG | [18 项冻结源](<docs/visual/sa-20261103/r61-log-journey/sources.json>)与[执行命令/时序](<docs/visual/sa-20261103/r61-log-journey/steps.json>)对应 Debug/Release 构建各 **0 警告、0 错误**，Core 各 **530/530**、七类筛选 GUI 各 **53/53**、日志 CLI 各 **20/20**，无跳过。[Debug 构建](<docs/visual/sa-20261103/r61-log-journey/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r61-log-journey/debug-core.txt>) / [GUI](<docs/visual/sa-20261103/r61-log-journey/debug-gui.txt>) / [日志 CLI](<docs/visual/sa-20261103/r61-log-journey/debug-cli-logs.txt>)；[Release 构建](<docs/visual/sa-20261103/r61-log-journey/release-build.txt>) / [Core](<docs/visual/sa-20261103/r61-log-journey/release-core.txt>) / [GUI](<docs/visual/sa-20261103/r61-log-journey/release-gui.txt>) / [日志 CLI](<docs/visual/sa-20261103/r61-log-journey/release-cli-logs.txt>)。这不是全量 integration 或全业务 parity 测试。 |
| VIS / 局部渲染 | 已逐张查看最终四张 1320×820、96 DPI WPF 渲染：[全局追踪](<docs/visual/sa-20261103/r61-log-journey/matrix-captures-debug/global-back-following.png>) / [返回主屏](<docs/visual/sa-20261103/r61-log-journey/matrix-captures-debug/global-back-returned.png>)；[服务追踪](<docs/visual/sa-20261103/r61-log-journey/matrix-captures-debug/service-back-following.png>) / [返回主屏](<docs/visual/sa-20261103/r61-log-journey/matrix-captures-debug/service-back-returned.png>)。来源选择、真实新增事件、主屏服务及搜索可见；未知数据没有填入假读数。这里只核对当前旅程，无新产品视觉变化；不是桌面屏幕、原生焦点或高 DPI 证据。[PNG/hash 清单](<docs/visual/sa-20261103/r61-log-journey/captures.json>)已保留。 |
| 清理 / 原工作保留 | [初查](<docs/visual/sa-20261103/r61-log-journey/cleanup-inspection.json>)的唯一临时候选是空的共享测试父目录。核对真实绝对路径、非 reparse、空目录与无相关进程后，仅非递归移除；初次差异守卫误把相对源路径与绝对路径相比，规范化后确认只有本片测试文件变化，并未放宽源内容检查。[最终复核](<docs/visual/sa-20261103/r61-log-journey/cleanup-after.json>)为冻结源差异 0、与 R60 相比仅该测试文件变化、相关进程 0、临时候选 0。 |

**仍未验/未通过：** 原生菜单点击、Tab/Shift+Tab/Escape、返回焦点、非零滚动保持、快速反转与 125%/150% DPI 仍未验。R60 强制销毁模态 owner 的框架返回限制仍保留，不能由正常关闭通过推翻；S3 布局待核与 SA01–SA10 也未关闭。**日志 G3 和整体目标仍未完成。**

**业务覆盖勘测：** 主线抽查确认，[已批 CLI 自启目标](<docs/architecture.md#cli-与共享数据契约g1-r1-已批准>)尚有 self enable/disable、boot install/uninstall 缺口；[当前完整路由尾部](<src/SystemAutorun.Cli/CliArguments.cs#L50-L56>)只有两个 status，而 [GUI](<src/SystemAutorun.App/App.xaml.cs#L149-L195>)具备相应写能力。App 中未找到 recovery-state/recovery-resume 接线，既有[恢复 UX 未完成记录](<README.md#L147-L154>)仍成立。后续优先核对 CLI 注册目标身份、主体与非交互提权边界，再按既有 G1 补齐；不能直接照搬 GUI 弹窗/提权调用。远端真实成功路径仍按[既定限制](<README.md#L169-L182>)待验，本片未连接远端。

## 2026-11-03 — 日志窗口 LG05 关闭资源修复（R60，日志 G3 未完成）

本片沿用[已批日志 G2-r2、精确补充与最小范围](<docs/design-system.md#已批准的-r2-精确补充>)，只恢复“关闭取消窗口自己的读取、不停止 Agent”的 LG05 行为。布局、来源契约、权限、导出格式与主屏接线均未改变，不重开相同审批。

### 修复与复现边界

[日志窗口](<src/SystemAutorun.App/Dialogs/LogViewerWindow.xaml.cs#L25-L50>)现在共用一次性关闭任务，并在 `Closed` 兜底取消资源。owner 销毁会跳过子窗 `Closing`；已被销毁的窗口不再排队重入 `Close`。[读模型](<src/SystemAutorun.App/ViewModels/LogViewerViewModel.cs#L278-L284>)在实际等待追踪与读取任务结束后清除 busy 状态。

[初次失败](<docs/visual/sa-20261103/r60-log-lifetime/before-fix.txt>)、[带事件诊断的失败](<docs/visual/sa-20261103/r60-log-lifetime/before-fix-trace/lifetime-modal.txt>)及[仅有资源修复时的 4/5](<docs/visual/sa-20261103/r60-log-lifetime/focused-after.txt>)均保留。后者的[时间轨迹](<docs/visual/sa-20261103/r60-log-lifetime/focused-captures/lifetime-modal.txt>)显示：资源已在 owner 关闭后数十毫秒内结束，但模态 `ShowDialog` 到测试 STA 退出时才返回；这不是资源修复已解决返回旅程。

这与 [WPF #6757 的解释](<https://github.com/dotnet/wpf/issues/6757#issuecomment-1176879381>)一致：owner 的 `WM_DESTROY` 绕过子窗 `WM_CLOSE`，未正常结束模态消息帧。最终[资源回归](<tests/SystemAutorun.IntegrationTests/LogViewerGuiTests.cs#L388-L461>)保留两种 owner 销毁路径，在全部资源断言之后才回收测试自己的 STA；失败仍转抛，**不把该回收记作模态自行返回**。产品没有加入消息帧反射、原生焦点操作或其他框架绕过逻辑。

| 类别 | 实际证据与限制 |
| --- | --- |
| ENG | [18 项冻结源](<docs/visual/sa-20261103/r60-log-lifetime/sources.json>)及[实际命令/时序](<docs/visual/sa-20261103/r60-log-lifetime/steps.json>)对应 Debug/Release 构建各 **0 警告、0 错误**，Core 各 **530/530**、七类筛选 GUI 各 **49/49**、LogControlPlane 各 **20/20**，均无跳过。[Debug 构建](<docs/visual/sa-20261103/r60-log-lifetime/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r60-log-lifetime/debug-core.txt>) / [GUI](<docs/visual/sa-20261103/r60-log-lifetime/debug-gui.txt>) / [日志 CLI](<docs/visual/sa-20261103/r60-log-lifetime/debug-cli-logs.txt>)；[Release 构建](<docs/visual/sa-20261103/r60-log-lifetime/release-build.txt>) / [Core](<docs/visual/sa-20261103/r60-log-lifetime/release-core.txt>) / [GUI](<docs/visual/sa-20261103/r60-log-lifetime/release-gui.txt>) / [日志 CLI](<docs/visual/sa-20261103/r60-log-lifetime/release-cli-logs.txt>)。这不是全量 integration 或完整 GUI/CLI parity 结论。 |
| FLOW / 资源 | [5 个关闭场景](<docs/visual/sa-20261103/r60-log-lifetime/focused-after-r2.txt>)通过：离线初始化中直接关闭、owner 销毁、直接关闭随即 owner 销毁，以及追踪中的 modeless/modal owner 销毁。断言包含一次 `Closed`、初始化/追踪任务已结束、追踪与导出已停用、处置后刷新不再读。最终[模态轨迹](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/lifetime-modal.txt>)明确记录先完成资源断言，再执行测试 STA teardown。没有验证物理键盘或原生焦点。 |
| DATA | 独占真实 pipe/文件 fixture 在关窗后仍可 Ping，Agent 停止回调未触发，配置 revision 不变。离线场景配置原文与目录项不变，也没有启动 Agent；回收实际 fixture 后才结束测试。 |
| VIS / 局部渲染 | 已逐张查看最终 5 张真实 WPF RenderTargetBitmap：错误展开的[最小窗口](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/logs-1120x700.png>)、[常用窗口](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/logs-1320x820.png>)、[宽窗口](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/logs-1620x651.png>)，以及[模态追踪](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/following-modal.png>)、[非模态追踪](<docs/visual/sa-20261103/r60-log-lifetime/captures-debug/following-modeless.png>)。筛选、原文、图标、分页控件均可见，当前选中底色/文字和异常语义色保留，未见本片新增裁切或操作外框。[PNG 尺寸由原图核对，hash 清单已归档](<docs/visual/sa-20261103/r60-log-lifetime/captures.json>)。这些是 96 DPI 渲染，不是桌面屏幕、原生输入、窗口 chrome 或 125%/150% DPI 证据。 |
| 清理 / 保留已有工作 | [初查](<docs/visual/sa-20261103/r60-log-lifetime/cleanup-inspection.json>)只发现空的共享测试父目录；按[构造路径](<tests/SystemAutorun.Core.Tests/TempDirectory.cs#L5-L11>)核对绝对路径、非 reparse、空目录及无相关进程后，仅非递归移除该父目录。[最终复核](<docs/visual/sa-20261103/r60-log-lifetime/cleanup-after.json>)为 18 源差异 0、前轮 S3 的 13 源差异 0、相关进程 0、临时候选 0。原未提交工作、批准图片及失败证据均保留。 |

**已观察未通过：** 强制销毁模态 owner 后，WPF 消息帧自行退出的独立复现仍失败；正常模态下 owner 本来不可点击，本片没有把程序化极端关闭当作物理点击旅程。

**仍未验：** 全局日志菜单到返回、原生 Tab/Shift+Tab/Escape 与焦点恢复、快速反转及 125%/150% DPI；既有 S3 布局待核与 SA01–SA10 也不因此关闭。审查中的 owner/focus 守卫建议没有建立新的已复现主屏缺陷，故未据风格对齐改写已有接线。**日志 G3 与整个目标仍未完成。**

## 2026-11-03 — 共享状态 S3 实装与分项复验（R59，G3 未完成）

继续[已批准 G2-state-r2 与精确补充](<docs/design-system.md#新版-kfl-规则复核r58共享状态组件-g2>)及
[既定切片](<docs/design-system.md#已批状态范围的实施切片与规则归属>)，不重开相同审批。
**本片源码已迁移，UI 尚未完整验收。** 没有改写 Windows 启动项、连接真实远程主机、启动工作负载或接管前台。

| 类别 / 规则 | 实际证据与限制 |
| --- | --- |
| 实装 — REU / INT-2/3/3a | [Startup](<src/SystemAutorun.App/Dialogs/StartupManagerWindow.xaml>)移除两种 Marker，名称/图标复用行交互色；搜索原边框跟随输入 brush。[Caption role](<src/SystemAutorun.App/Services/ColorStateAssist.cs#L27-L33>)只驱动既有标题，不染正文。[Monitoring](<src/SystemAutorun.App/Dialogs/MonitoringWindow.xaml>)移除 Rail/Tab 状态条；主机状态点和元数据保留语义色；splitter 保留 6 DIP、原生键盘调整、可访问名，只让既有“事件详情”标题反馈自身焦点。正文仍使用现有 1 DIP 边框颜色反馈。不增加可聚焦父层或新导航。 |
| ENG — 最新冻结矩阵 | [13 项冻结源](<docs/visual/sa-20261103/r59-state-s3-r2/sources.json>)及[执行命令/时序](<docs/visual/sa-20261103/r59-state-s3-r2/steps.json>)对应 Debug/Release 构建各 **0 警告、0 错误**，Core 各 **530/530**、七类筛选 GUI 各 **45/45**，无跳过。[Debug build](<docs/visual/sa-20261103/r59-state-s3-r2/debug-build.txt>) / [Core](<docs/visual/sa-20261103/r59-state-s3-r2/debug-core.txt>) / [GUI](<docs/visual/sa-20261103/r59-state-s3-r2/debug-gui.txt>)；[Release build](<docs/visual/sa-20261103/r59-state-s3-r2/release-build.txt>) / [Core](<docs/visual/sa-20261103/r59-state-s3-r2/release-core.txt>) / [GUI](<docs/visual/sa-20261103/r59-state-s3-r2/release-gui.txt>)。过滤类为 ColorStateAssist、LogViewerGui、RemoteHostsWindow、GuiConfigurationControl、GuiRemoteHostControl、StartupGuiWindow、MonitoringWindow；不是全量 integration。[构建后源核对](<docs/visual/sa-20261103/r59-state-s3-r2/source-audit.json>)无变化。 |
| 修复后复验 — REU-4 / INT-3a | [首轮 16/17](<docs/visual/sa-20261103/r59-state-s3-r1/focused.txt>)发现 Rail 的布尔 Tag 只改变文字、未改变底色；同源 Metric 也用了字符串 True trigger。两配方当前均无页面消费者，不冒称已见产品缺陷。已复用 Dashboard 的强类型 Boolean trigger，并让选中底色优先于 hover；保留底色/强类型/优先级断言，不删测试。上述双配置 45/45 已包含新回归与既有 Dashboard 底色断言。 |
| VIS — 100% DPI 局部实装 | 已逐张查看最终 **10 张真实 WPF RenderTargetBitmap**，不是效果图或桌面屏幕取帧。[Startup 最小](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/startup-selected-1120x700.png>) / [常用](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/startup-selected-1320x820.png>) / [宽屏](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/startup-selected-1620x651.png>)及[元数据详情](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/startup-details-1620x651.png>)；[主机最小](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-overview-1120x700.png>) / [常用](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-overview-1320x820.png>) / [宽屏](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-overview-1620x651.png>)；[异常最小](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-exceptions-1120x700.png>) / [常用](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-exceptions-1320x820.png>) / [宽屏](<docs/visual/sa-20261103/r59-state-s3-r2/captures-debug/monitoring-exceptions-1620x651.png>)。旧条已消失，选中名称/底色、异常红色和既有分隔可见；不以离屏图证明原生聚焦终点、输入或窗口 chrome。 |
| VIS 发现 — ALN-14/17 / TYP-3 / VER-3 | 旧 Monitoring 概览连接信息末行被固定头部裁切，异常表的时间末尾也有裁切；详情仍可读完整的既有时间字符串和消息。未借“只改状态”重排旧界面，记录为后续布局待核；不宣布整页 ALN/TYP 或 VIS 通过。 |
| FLOW / DATA 的测试边界 | [Startup 测试](<tests/SystemAutorun.IntegrationTests/StartupGuiWindowTests.cs>)用已批合成样例及 owned pipe；[Monitoring 测试](<tests/SystemAutorun.IntegrationTests/MonitoringWindowTests.cs>)用独占 fixture、未初始化的主机读模型及明确标注的合成异常，切换选择期间阻断刷新，不读生产日志、不探测远程。覆盖选择/取消、语义色保留、固定几何、Caption binding 与正文 binding/颜色、卸载恢复；[Caption 双 motion 测试](<tests/SystemAutorun.IntegrationTests/ColorStateAssistTests.cs>)使用 Tag 选择分支检验独占 brush；动画/反转分支以该 STA 的 Windows ClientAreaAnimation 开启为条件，本轮未另记该分支观测，不把两个 motion 参数都通过当作强制动画证据。即时路径有断言；**这些不是原生焦点旅程**。 |
| 审查复核 | 不接受“聚焦必须更亮”的误报：已批无边框色对正是 #F4F4F5→#8B8B94，不能擅自倒置。Metric 的旧 Foreground 是取消 role 后的回退，不是必须删除的死代码。Splitter 的标题反馈与正文的边框反馈各有所有者；静态上下文图标不因此重映射。Collapsed 控件仍属窗口，窗口 Unloaded/Dispose 才解绑；未发现可据此确认的泄漏。真实聚焦、focused-selected、返回/反转的测试缺口仍如实保留。 |
| 清理与源审计 | [PNG 格式/尺寸/hash 与初查](<docs/visual/sa-20261103/r59-state-s3-r2/audit-final.json>)确认 10 张 PNG、13 源匹配、相关进程 0，宽检发现一个空的共享测试父目录；按[真实 TempDirectory 构造路径](<tests/SystemAutorun.Core.Tests/TempDirectory.cs#L5-L11>)、非 reparse/空目录/无相关进程核验后只做非递归移除。[最终复核](<docs/visual/sa-20261103/r59-state-s3-r2/cleanup-after.json>)为源差异 0、相关进程 0、SystemAutorun 临时候选 0。保留原未提交工作、批准图片与证据。 |

**仍未验：** 原生 Tab/Shift+Tab、列表键盘选择及 focused-selected、搜索/详情实际焦点色、splitter 键盘调整与焦点归属可发现性、返回/快速反转、原生 reduced-motion、125%/150% DPI、旧 popup 圆角及全套日志旅程。沿 R58 已知前台限制，不绕过、不盲重试或改桌面/会话设置。S4 与既有布局待核继续开放；**日志 G3、完整 GUI/CLI parity 和 SA01–SA10 总体验收未完成。**

## 2026-11-03 — GUI 日志 r2 实装与分项验收（R58，G3 未完成）

本节保留 R58 阶段快照；其中 Startup/Monitoring 待迁移与 40 项矩阵是当时事实，当前 S3 接续及剩余门禁见上节。

继续 [G1-r1 日志旅程与现有接线事实](<docs/architecture.md#2026-11-03--gui-日志旅程接续与-g2-候选r58>)，
不重复整套交互审批。已实际生成并查看 [日志候选 r1](<docs/visual/sa-20261103/logs-g2-candidate-r1.png>)，
仅合成数据/纯文字输入；PNG 1672×941，1,493,841 bytes，Pillow verify/完整解码与源/副本
SHA256 一致。**图中“全部来源”、新一级导航与重复信息不直接进入实现**；
[逐项审阅](<docs/design-system.md#2026-11-03--日志-g2-候选-r1r58>)后用户明确选择“重画 r2”。
[r2 原图](<docs/visual/sa-20261103/logs-g2-candidate-r2.png>)也已生成并实际查看：PNG **1536×1024**、
**1,385,184 bytes**，Pillow verify/完整解码、源/副本 SHA256 一致（E8251E24…9850AB5）。
无“全部来源”或大导航，事件原文就地展开；图中生成的全页码/末页跳转、重复身份等
按[精确补充与实施范围](<docs/design-system.md#2026-11-03--日志-g2-候选-r2r58>)集中审阅，
不能当既有后端能力。用户随后明确选择 **“批准 r2 并实施（推荐）”**，G2-logs-r2 与
所列实施范围 **已批准**。现已实施，但下列证据不等于完整日志 UI 验收。

| 类别 / 验收项 | 实际结果与边界 |
| --- | --- |
| 实装范围 | [窗口/绑定](<src/SystemAutorun.App/Dialogs/LogViewerWindow.xaml>)、[有界窗口模型](<src/SystemAutorun.App/ViewModels/LogViewerViewModel.cs>)及主屏/菜单入口已接入。原 status 详情、异常聚合与打开日志目录保留；不新增 provider、配置写入、提权、自动启动 Agent 或 WSL。 |
| DATA / STA 绑定旅程 — LG01–LG06 | [窗口测试](<tests/SystemAutorun.IntegrationTests/LogViewerGuiTests.cs>)接 owned AgentServer/真实 pipe、文件读取与 CLI；事件及服务身份为明示合成 LG-F1，NeverRunExecutor 不启动工作负载。覆盖来源/级别/时间、正向分页、显式追踪/暂停、轮转丢失、未知时间/损失 UTF-8、当前页导出格式与拒覆盖、真实窗口关闭及离线连接取消。STA 路由事件/控件选择不冒充 OS 键鼠输入，也不是生产服务数据。 |
| 修复与复验 | 原窗口关闭重入已改为 dispatcher 上单次异步收尾；来源刷新不清空被选中的服务项，新增 SelectionBoxItem 与可见文字断言；摘要去重复 severity 前缀。初矩阵中新增离线测试根缺 autorun.yaml，连接在开始前即被拒，已补正常根而非删除 IsBusy 断言；其后漏写 UTF8Encoding 命名空间的编译错误也已修复。旧焦点记录是中间版本，不替代末轮结果。 |
| ENG — 冻结矩阵 | [9 份 R58 源码](<docs/visual/sa-20261103/r58-sources-sealed.txt>)对应 Debug/Release 各一轮构建 **0 警告/错误**，Core 各 **530/530**、筛选 integration 各 **60/60**，无跳过。日志：[Debug build](<docs/visual/sa-20261103/r58-final-debug-build.txt>) / [Core](<docs/visual/sa-20261103/r58-final-debug-core.txt>) / [integration](<docs/visual/sa-20261103/r58-final-debug-integration.txt>)；[Release build](<docs/visual/sa-20261103/r58-final-release-build.txt>) / [Core](<docs/visual/sa-20261103/r58-final-release-core.txt>) / [integration](<docs/visual/sa-20261103/r58-final-release-integration.txt>)。筛选为 LogViewerGuiTests、LogControlPlaneTests、DashboardReadModelTests、DashboardMonitoringTests、StartupGuiWindowTests；不是全量 integration 或其他系统验收。 |
| VIS — 桌面渲染 | 已实际查看冻结版本真实 WPF RenderTargetBitmap：[1120×700](<docs/visual/sa-20261103/r58-probe-final/logs-1120x700.png>)、[1320×820](<docs/visual/sa-20261103/r58-probe-final/logs-1320x820.png>)、[1620×651](<docs/visual/sa-20261103/r58-probe-final/logs-1620x651.png>)。服务“媒体库”可见，多行原文展开，底部读取起点/页大小可见；仅 **100% DPI**，程序选择/渲染不是完整原生键鼠或 125%/150% DPI 证据。 |
| FLOW — 原生局部 | R2 native probe 已观察事件展开，原生保存对话框取消后无文件，再成功导出并回读[当前页 envelope](<docs/visual/sa-20261103/r58-probe-native-r2/native-page.ndjson>)四条合成事件。级别键盘尝试没有选中目标，**不记筛选通过**；服务→日志→返回、键盘焦点恢复和跟随/暂停仍缺充分原生证据。R2 证据早于末轮摘要修订，不冒充冻结版本全项实测。 |
| 新 KFL — INT-2/3/3a / COL-1/3 | 已同步并重载 [4e87937](https://github.com/AngleNaris/KFL/commit/4e87937108a09eeab98e120ec1e773d7ec5656e4)。用户先选择焦点/选中均按新默认，随后明确批准[共享 G2-state-r2 与精确补充](<docs/design-system.md#新版-kfl-规则复核r58共享状态组件-g2>)并授权实施。r1 蓝色/圆角/红焦点不采用；r2 额外外框、日历、焦点加粗不落地。已接入[颜色状态机制](<src/SystemAutorun.App/Services/ColorStateAssist.cs>)、[Dashboard](<src/SystemAutorun.App/Themes/Dashboard.xaml>)与[共享组件](<src/SystemAutorun.App/Themes/Components.xaml>)；Startup/Monitoring 的旧标记仍未迁移，不能称全局通过。 |
| 状态 S1 / 局部原生 | [证据](<docs/visual/sa-20261103/r58-state-s1/evidence.json>)包含 owned 合成日志窗口的实际 Tab、行选择与图标像素；不是完整服务旅程。曾发现原文 Material 下划线，已进入 S2 共享修正，旧图不作为新版本全项通过。 |
| 状态 S2 / ENG | [最新矩阵](<docs/visual/sa-20261103/r58-state-s2-engineering-matrix-r2.txt>)对应[9 项冻结源](<docs/visual/sa-20261103/r58-state-s2-sources-r2.json>)，Debug/Release 构建各 **0 警告/错误**，Core 各 **530/530**，ColorStateAssist、LogViewerGui、RemoteHostsWindow、GuiConfigurationControl、GuiRemoteHostControl、StartupGuiWindow 六类过滤集各 **40/40**、0 跳过；不是全量 integration。新增 routed observer 测试最初误用会实际改焦点的 TextBox，改为不可聚焦 Control 隔离观察，保留恢复 brush/卸载解绑断言；[修正后的聚焦 18/18](<docs/visual/sa-20261103/r58-state-common-tests-r4.txt>)亦通过。 |
| 状态 S2 / 审查复核 | [复核结论](<docs/visual/sa-20261103/r58-state-review-resolution.json>)不照单接受“已复现”标签：真实 WPF 测试确认 ComboBox 校验红边会在 motion-setting 更新后被普通边色覆盖，现由 HasError trigger 将[库原生校验色](https://github.com/MaterialDesignInXAML/MaterialDesignInXamlToolkit/blob/v5.3.2/src/MaterialDesignThemes.Wpf/Themes/MaterialDesignTheme.ComboBox.xaml#L754-L758)传为 resting brush，TextBox 保持其原库表现；[9/9 回归](<docs/visual/sa-20261103/r58-state-review-tests-r2.txt>)通过。DataGridRow 与 Selector 的选中/取消事件 runtime `Same` 且无键盘焦点的动态切换均通过，故拒绝“事件不同导致丢选中”假设，不加多余事件钩子。静态不可聚焦表头恢复 TextSecondary，可聚焦表头才用交互角色；MenuItem 键盘假设和校验/聚焦并发仍需原生证据。 |
| 状态 S2 / 原生发现与修正 | [r1 像素/时序](<docs/visual/sa-20261103/r58-state-s2-native-r1/evidence.json>)：100% DPI owned 禁用合成远程表单，无实际连接；原生 Tab→ComboBox、Space 切换 CheckBox、主动作边框、Alt+Down/Escape 有局部观察。但 NameTextBox 在 systemMotion=true 时三帧均直接为 #F4F4F5、无动画，**MOT/INT-3 不通过**。已补[共享后置 routed focus 观察](<src/SystemAutorun.App/Services/ColorStateAssist.cs#L75-L112>)与卸载解绑，不用静态截图掩盖失败。 |
| 状态 S2 / 最新 VIS/FLOW 未验 | 修后 [r2](<docs/visual/sa-20261103/r58-state-s2-native-r2/evidence-cleanup.json>)虽观察到 owned animation，但前台已转移，未取得有效终点截图；无关应用截图已排除并删除。用户另准短时前台复验，[r3](<docs/visual/sa-20261103/r58-state-s2-native-r3/evidence-cleanup.json>)遇 CopyFromScreen 句柄无效与 Windows 前台锁，**Tab 未发送**，未绕过。两个 test exit 0 仅是原夹具业务断言通过，不能把未执行的原生路径算通过。 |
| 状态 S2 / r4 新增局部证据 | 用户确认桌面恢复后再试，[r4](<docs/visual/sa-20261103/r58-state-s2-native-r4/evidence-cleanup.json>)实际查看[真实窗口静态图](<docs/visual/sa-20261103/r58-state-s2-native-r4/inactive-window-static.png>)，但这是 inactive PrintWindow，不是聚焦终点像素。UIA 程序聚焦的[原始时序](<docs/visual/sa-20261103/r58-state-s2-native-r4/owned-probe/native-focus.ndjson>)确认 NameTextBox FocusWithin/Focused 为 true：0ms #303036 → 81ms #F0F0F1 → 213ms #F4F4F5、动画 true→false，几何 386×32 / 0,0,0,1 不变；证明非瞬跳及到达目标，不以调度晚采样证明精确 120ms。物理 Tab 再被前台锁拒绝，未发送；正常 activate 返回 sent 后窗口仍 inactive。[只读桌面诊断](<docs/visual/sa-20261103/r58-state-native-desktop-diagnostic.json>)为 foreground HWND=0，[会话诊断](<docs/visual/sa-20261103/r58-state-native-session-diagnostic.json>)为 worker session 1 Active、active console 12，只记录事实，不断言用户未解锁；未切换会话/桌面、修改安全设置或降低前台保护。窗口关闭、29240 退出、归档 hash 核实、owned probe/fixture/截图暂存清理，最终临时候选 0。 |
| 状态范围 / 仍缺证据 | [切片归属](<docs/design-system.md#已批状态范围的实施切片与规则归属>)沿原批准：S2 最新原生终点/返回及 reduced-motion、S3 Startup/Monitoring/splitter、旧 popup 圆角 RAD-4/5、三档桌面与 125%/150% DPI 均未完成；保留已批 G1/G2，不重复审批、不提前声称全局 KFL 或 G3 通过。 |
| 状态探针清理 | r1 归档原始时序/截图并逐文件 hash 核对，22632 已退出；[r2 清理](<docs/visual/sa-20261103/r58-state-s2-native-r2/evidence-cleanup.json>)与[r3 清理](<docs/visual/sa-20261103/r58-state-s2-native-r3/evidence-cleanup.json>)核实 19592/24108 退出、fixture finally 回收及精确 probe 删除。没有为失败截图归档用户其他应用内容。[工具产物清理](<docs/visual/sa-20261103/r58-state-review-artifact-cleanup.json>)核对归档 hash 后删 5 张本轮原始截图及只读库源暂存；[末审](<docs/visual/sa-20261103/r58-state-s2-final-audit.json>)确认 9 源匹配、相关进程 0。唯一共享空测试父目录按实际测试构造路径与空目录/非 reparse 校验[单独移除](<docs/visual/sa-20261103/r58-state-s2-final-cleanup.json>)，最终临时候选 0；未清理无关数据。 |
| 清理与保留 | [清理记录](<docs/visual/sa-20261103/r58-cleanup-final.json>)核实四个 probe 进程退出、归档文件 hash 一致后仅删其精确临时目录；fixture 根已由 finally 回收。KFL 下载暂存目录已移除，上游 archive 与旧技能备份保留。[源码/进程审计](<docs/visual/sa-20261103/r58-final-audit.json>)核对 R57 11 份及 R58 9 份冻结源，相关测试进程 0。其发现的一个早期残留按 Prepare 的精确合成签名、非 reparse、无相关进程与既有 workspace lease 未持有核实后[另行回收](<docs/visual/sa-20261103/r58-residue-cleanup.json>)，最终 SystemAutorun 临时候选 0；未 reset 或覆盖原未提交工作。设计图与验收图片保留。 |
| 未验 / 下一步 | 已批局部 G2 不重问；S2 程序焦点色阶已补到，物理原生输入仍受环境限制，不再盲重试或解除未验门禁；可继续已批且不依赖前台的 Startup/Monitoring 独立源迁移及 splitter 标题色反馈，移除剩余旧标记。随后补 S2 原生终点/返回、筛选/跟随/导出旅程、桌面矩阵与 125%/150% DPI。**日志 G3、整体 GUI/CLI parity 和 SA01–SA10 总体验收未完成**；上列旧冻结矩阵是前一版本的历史证据，不证明本轮改动。 |

## 2026-11-03 — 已批独立只读操作对账 SDK/IPC/CLI（R57）

[批准范围与实现边界](<docs/architecture.md#2026-11-03--只读操作对账-r1g1-operation-diagnostics-r1已批准>)：用户明确批准 `g1-operation-diagnostics-r1`，新增 `startup operation inspect <UUIDv7>`；旧命令、GUI、Helper 与 Native 三路写门禁不改。批准不代表 G3 完成。

| 类别 / 验收项 | 实际结果与边界 |
| --- | --- |
| FLOW/DATA — DO01 | [实际 Console/pipe 数据组](<tests/SystemAutorun.IntegrationTests/StartupOperationInspectionDataTests.cs>)已跑通两种来源 × prepared/native/committed/finalized，含 JSON 与人类输出：本次 Console PE→owned 进程内 AgentServer/dispatcher→真实 Journal/R54 issuer/DPAPI/Native reader。请求/response 与固定 Native 槽为 fixture，不触真实 Startup 文件夹。原操作 unknown 始终 exit 6；completed+prepared 用例明确 synthetic callback success，exit 0 仍是 candidate，非原操作成功/finalization 授权。 |
| FLOW/DATA — DO02/DO03 | SDK 与 Console 重复读后 Root/历史证据/cleaner 可识别 orphan tmp 的 bytes 不变；缺失 ID（含别的 receipt 在场）不扫描或建 Journal 目录；finalized 后无 intent 只 none，非 startup-disable 实际回执只明确 unsupported，均不猜关联 ID/重试/更新回执。 |
| DATA — DO04 | [协议/CLI 组](<tests/SystemAutorun.IntegrationTests/StartupOperationInspectionProtocolTests.cs>)覆盖坏 schema/ID/时间、6 个权限 flag、诊断 flag、重复/未知/错大小写/缺字段、16KiB 上限、running 错升 completed、非 observed 夹带 IDs、写身份/replayed/旧协议；全部拒为 protocol_mismatch。错误回复夹带的 Data 不显示；actual pipe 的 payload/revision/写 operationId 请求均在 Native 读取前拒绝。合成响应与传输组不冒充 OS 数据。 |
| FLOW/DATA — DO05 | 真实 Console `--timeout 2` 在受控 Native gate 内到期 exit 6、data=null；另一真实 pipe 调用仍 busy，actual worker 未提前完成。finally 释放后显式等待 workflow 取消/清理，再查询可用。另有实际 adapter caller 取消、合成 transport 取消/旧 Agent unknown_command；不自动启动 daemon、fallback 或重发。没有制造 OS 挂起/进程崩溃。 |
| DATA — 权限/可观察性 | 独立 schema 1，四轴不合并；queriedOperationId 独立于 mutation identity，Root/request fencing 沿原客户端。六项推进权限恒 false，非关联时 IDs=null，错误文案脱敏。Journal 是可编辑数据，不认证应用发行者；不将 completed、archive_matches 或 exit 0 作为原生授权。 |
| ENG — 首轮与夹具修正 | [初构建](<docs/visual/sa-20261102/r57-build-initial.txt>) exit 0（尚不涵盖后续 CLI/测试修订）；[首次焦点](<docs/visual/sa-20261102/r57-focus-initial.txt>)编译失败：夹具误用不存在的 AgentClient.Endpoint，改为既有 ForRoot；[首版焦点](<docs/visual/sa-20261102/r57-focus-r1.txt>) **54/54**。扩充寿命用例后[中间构建](<docs/visual/sa-20261102/r57-focus-final.txt>)因丢弃 Assert.Single 返回的 Task 被 CS4014 阻止，已显式 discard 该身份检查，不 await 仍受控阻塞的任务，也没有关闭告警。 |
| ENG — 末轮焦点 | [修正后的焦点](<docs/visual/sa-20261102/r57-focus-r2.txt>) **exit 0**：构建 **0 警告/错误**，新组 **58/58**、R56 **39/39**、旧 CLI/Native CLI **102/102**，均 **0 跳过**。 |
| ENG — 冻结矩阵 | [11 份源码冻结](<docs/visual/sa-20261102/r57-sources-sealed.txt>)后的 [Debug/Release 矩阵](<docs/visual/sa-20261102/r57-matrix-sealed.txt>) **exit 0**：两配置构建各 **0 警告/错误**，Core 各 **530/530**、筛选 integration 各 **1718/1718**，均 **0 跳过**。Windows 11 x64 **10.0.28000 / C: NTFS**；每配置各一轮，不冒称连续两轮、全量无筛选、最低系统或其他平台通过。 |
| 复核 | role-6 限定只读复核未提出可复现的新权限/证据升级路径；主线独立收紧精确字段解析并实测。不采纳报告中“历史操作已获证”的措辞，也不采纳沿用日志的 R56 裸读漏洞结论；既有 byte helper 重新准入已由 R56 主线直接核实。本次不是完整安全审计/跨账户取证。 |
| 清理/DO06 | [切片前基线](<docs/visual/sa-20261102/r57-baseline.txt>) **32 项原输入匹配、临时项 0**；[末轮审计/清理](<docs/visual/sa-20261102/r57-cleanup-final.txt>) **exit 0**：相关进程/fixture 服务 **0**，HKCU/HKLM × 32/64 位视图私有注册根各 **0**；32 项保留输入、2 项未改旧 target、11 项 sealed source 与旧保护 codec/侧库测试 hash 全匹配。production profile 侧库/Helper log 未生成；仅删除精确核实的非 reparse 空测试父目录 1 项，最终临时项 **0**。源码与文档 whitespace 检查通过，保留此前未提交工作，无 reset/整体覆盖。 |
| 未验/下一片 | G2/VIS **N/A**：仅独立 Console/SDK/IPC，无 GUI 改动。生产 startup-disable/确认/runtime 与 Native 三路写入口仍关闭；只读诊断不等于完整 unknown/restart 解决方案。后续沿既有 HS04 与 G1/G2-r2 核对 GUI/恢复旅程，整体 GUI/CLI parity、日志界面及产品总体验收仍未完成。 |

## 2026-11-03 — 现场 Journal 的纯只读快照与关联汇总（R56）

[本片范围](<docs/architecture.md#2026-11-03--现场-journal-的纯只读快照与关联汇总r56>)从现存 Journal 固定叶读取，再接 R55；**仍是内部 DATA 入口，没有注册生产 SDK/IPC/Helper/GUI，没有改动现有 GetAsync 或开放三路 Native 写入口**。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 现场读取 | [新 reader](<src/SystemAutorun.Windows/Startup/StartupHistoryJournalEvidenceReader.cs>)规范化指定 UUIDv7，只读当前 actor 固定目录的 metadata/receipt，root→现存 mutation/workspace→两份 Journal image pin 覆盖 R55 调用窗口及外层 seal。无写租约、GetAsync/RunAsync、目录/文件/锁创建、scan、cleanup 或回执更新。 |
| DATA — 读契约 | 两 image 上限各 **256KiB**；现 writer 根字段必须完整、唯一、精确大小写且无多项，拒错 schema/identity/fingerprint/state/command/owner。它是现格式的保守读取子集，不升级格式、不认证可编辑 Root/应用发行者，不把 retention floor 当执行授权。当前仅 startup-disable 诊断。 |
| DATA — 状态分轴 | JournalState、PersistedReceiptState、ObservedReceiptState 与 R55 Evidence 独立。无 live `_active`，persisted running 保守标 unknown，**实际活跃 callback 亦如此，不证明崩溃/停止**；completed/unknown 仅盘上标签。六项执行/回执更新/重放/finalize 标志恒 false。 |
| DATA — 缺失与封尾 | 无选定 receipt 只 missing，不扫描别的 receipt；无 intent（含 finalize 后）只内层 none/unobserved，不扫侧库猜 recordId或授权新 ID 重试。内层 side/native pin 保留原观察窗口，外层仅固定 Journal snapshot 并最终精确 bytes/actor 复核，不声称全局 atomic snapshot。失败不发布算出的 Evidence。缺失分支显式安全复读整条本地 no-reparse 路径；中途新出现 receipt 不被采用。 |
| DATA — 真/假边界 | [39 项新用例](<tests/SystemAutorun.IntegrationTests/StartupHistoryJournalEvidenceTests.cs>)使用真实 Journal callback/R49/R54 actual owner、私有 ACL/CurrentUser DPAPI；请求、response、同卷 Native 目录为 fixture，不触真实 Startup 文件夹。真实活跃 callback 用 finally 释放闸并 drain；synthetic completed + prepared 仍 candidate/original_matches 而非 finalization。两种真实 cleaner 可识别 orphan tmp 的 bytes 在重复查询后不变。 |
| 复核边界 | 通用限定复核覆盖初版三个小文件，未发现可复现错误/关键伪通过。安全专项未读底层 helper 就推测缺失复读为裸读；主线抽读 [实际 helper](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryTransaction.cs#L231-L236>)确认本就先 OpenOptional re-pin，**不采纳已存在网络漏洞的结论**。末轮加本地 late parent/leaf reparse + 独占 target 与晚出现 receipt 回归，没有 UNC/SMB 外连测试，不改旧 helper/R55。 |
| ENG — 焦点 | [首轮](<docs/visual/sa-20261102/r56-focused-initial.txt>) **exit 0**：构建 **0 警告/错误**，**34/34**、**0 跳过**。[封尾焦点](<docs/visual/sa-20261102/r56-focus-final.txt>) **exit 0**：构建仍 **0 警告/错误**，新组 **37/37**、历史 **697/697**，均 **0 跳过**。 |
| ENG — 初矩阵与终态回归 | [初冻结](<docs/visual/sa-20261102/r56-sources.txt>)后[初矩阵](<docs/visual/sa-20261102/r56-matrix-final.txt>) **exit 0**：Debug/Release **0 警告/错误**，Core 各 **530/530**、筛选 integration 各 **1658/1658**，**0 跳过**。随后主线对照既有 SDK 发现新 reader 的 header 子集未检 terminal skeleton；[修复前回归](<docs/visual/sa-20261102/r56-terminal-before.txt>)构建通过、**2 失败/14 通过**，真实复现 completed 无终态结果与 running 带终态结果错误输出 Journal observed。已改为复用未修改的 [AgentOperationClient.ReadReceipt](<src/SystemAutorun.Core/Ipc/AgentOperationClient.cs#L22-L69>)，不是跳过/吞错；初矩阵不代表此修订。 |
| ENG — 末轮矩阵 | [末轮冻结](<docs/visual/sa-20261102/r56-sources-sealed.txt>)记录新 reader/用例及共享 validator；[末轮矩阵](<docs/visual/sa-20261102/r56-matrix-sealed.txt>) **exit 0**：Debug/Release 构建均 **0 警告/错误**；末轮新组 **39/39**、历史 **699/699**，Core 各 **530/530**、筛选 integration 各 **1660/1660**，均 **0 跳过**。本片两版均每配置各一轮筛选集，不冒称同版本连续两轮。仅 Windows 11 x64 **10.0.28000 / C: NTFS**，不是全量无筛选/最低系统/跨平台/生产旅程验收。 |
| 清理与兼容 | [原始基线](<docs/visual/sa-20261102/r56-baseline.txt>)临时项 **0**；[末轮审计/清理](<docs/visual/sa-20261102/r56-cleanup-final.txt>) **exit 0**：相关进程/fixture 服务 **0**，HKCU/HKLM × 32/64 位视图私有注册根各 **0**，32 项原输入 hash 全匹配、3 项 sealed source 全匹配、旧保护 codec/侧库测试 hash 仍匹配 R54；production profile 侧库/Helper log 未生成。仅精确核实的非 reparse 共享空测试父目录残留 1 项被删除，最终临时项 **0**；源码无尾空白，文档 whitespace 检查通过，保留已有未提交工作。矩阵/清理后仅更正三行注释，明确既有 byte reader 本来会重新准入；[最终源码审计](<docs/visual/sa-20261102/r56-final-source-audit.txt>)反替换后 SHA 精确匹配 sealed 源，另两源未变，**无执行代码变化**，未为注释重跑全矩阵。 |
| 未验/下一片 | G2/VIS/FLOW **N/A**：本片无 UI/交互改动。下一片沿 HS04 提出独立只读诊断 SDK/IPC/CLI 契约并核对受影响 G1，进入用户可执行旅程；不向现有 strict inspection DTO 或历史 response.Data 偷塞 evidence，不改现有 operation show 语义。如须新交互/视觉则先补受影响 G1/G2。生产确认/runtime、Native 三路、真实崩溃/重启/跨账户/掉电/其他卷/最低系统与整目标均未完成，不宣称 exactly-once。 |

## 2026-11-03 — 给定回执快照的只读关联证据汇总（R55）

[本片范围](<docs/architecture.md#2026-11-03--给定回执快照的只读关联证据汇总r55>)把实际 R54 发行来源接入内部只读 DATA 汇总，但输入仍是明确给定的 receipt snapshot，**不是现场 Journal 读接口、完整 unknown/restart 流程或生产 SDK/IPC 入口**。生产原生三路继续关闭。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 只读输入 | [新查询](<src/SystemAutorun.Windows/Startup/StartupHistoryOperationEvidenceReader.cs>)验证 snapshot schema/opid/fingerprint/command/name/state，只把 running/completed/unknown 标成 ReceiptSnapshotState。它不调用现有 GetAsync（该查询会取写锁并清 tmp），不创建协调文件、目录、stage 或清任何 Journal orphan。快照/DPAPI 都不认证调用方或应用发行者。 |
| DATA — 同组关联 | readonly root→mutation→workspace→root images→侧库固定 index/current/Link/candidate-parent，未改 R53 Reader 在同组 bytes 被钉住时独立关联/seal；重读 metadata 比较防旧结果拼接，并核对 exact candidate 与 record/Txn。无 Link 直接 unlinked，不采用中途新出现的 Link，不调用 KnownFolder。 |
| DATA — 物理证据 | 仅关联成功后才从同组已认证 current proof 解出 payload，配合已钉 config 交给旧 readonly native reconciler。物理槽与路径/主体 scope 保留至外层封尾，native 最后复核先于最终 root/side/mapping/actor seal；晚期 ACL/mapping/Native unverified 均清 associated IDs。不是全局 atomic CAS、历史授权或 Native 成功回执。 |
| DATA — 不推进 | 所有 NativeWriteReady/ExecutionRequested/TransactionFinalized/MayFinalize/ReceiptUpdated/ReplayRequested 恒 false；committed + archive_matches 不升级 unknown，不清 intent、不重放/补旧证。缺 intent（含 finalize 后 unknown）只返回 none/unobserved，不猜/扫描 recordId；此有界查找不是完整恢复。 |
| DATA — 真/假边界 | [新组](<tests/SystemAutorun.IntegrationTests/StartupHistoryOperationEvidenceTests.cs>)真实 Journal callback→R49/R54 actual owner→私有 ACL/CurrentUser DPAPI；请求、unknown response 和同卷 Native 目录为明示 fixture，不触真实 Startup 文件夹。测试给定 snapshot 获取在查询前，查询过程中 receipt 文件可被独占且 Journal orphan tmp 保留；新建 reader/Journal 对象只是逻辑重开，不冒充真实进程崩溃/OS 重启。 |
| ENG — 焦点 | [首轮](<docs/visual/sa-20261102/r55-focused-initial.txt>) **exit 0**：构建 **0 警告/错误**，初始 **40/40**、**0 跳过**。[末轮](<docs/visual/sa-20261102/r55-focus-final.txt>) **exit 0**：构建仍 **0 警告/错误**、新组 **43/43** 与历史 **660/660** 均通过、**0 跳过**。[两项冻结源](<docs/visual/sa-20261102/r55-sources.txt>)用于后续矩阵。 |
| ENG — 完整矩阵与复核 | [冻结矩阵](<docs/visual/sa-20261102/r55-matrix-final.txt>) **exit 0**：Debug/Release 均 **0 警告/错误**，Core 各 **530/530**，记录所列 integration 筛选集各连续两轮 **1621/1621**，四轮均 **0 跳过**。仅 Windows 11 x64 **10.0.28000 / C: NTFS**，不是全量无筛选/跨平台/生产旅程验收。限定只读实现复核覆盖新 reader 全文及选定夹具段，未发现可复现错误或关键伪通过；设计咨询只核对原三份 reader 的最低契约，二者均不替代真实验收。 |
| 清理与兼容 | [原始基线](<docs/visual/sa-20261102/r55-baseline.txt>)临时项 **0**；[末轮审计](<docs/visual/sa-20261102/r55-cleanup-audit.txt>)相关进程、fixture 服务、私有注册根均 **0**，27 项原配置/Journal/receipt/context/协议/门禁/旧格式/readers/R54 发行 hash 全部未变、2 项冻结源全部匹配；canonical profile 侧库与 Helper log 未生成，whitespace 检查通过。仅共享空测试父目录残留 1 项：[最终清理](<docs/visual/sa-20261102/r55-cleanup-final.txt>)初次路径校验命令因 Split-Path 参数组合不兼容在删除前退出，改用已观察的 DirectoryInfo.Parent.FullName 重新核实绝对路径、非 reparse/空目录后仅删该父目录，最终临时项 **0**、**exit 0**。[扩围复核](<docs/visual/sa-20261102/r55-cleanup-scope-final.txt>)另验证 HKCU/HKLM 私有注册根及全部 fixture 进程/服务别名为 **0**，旧保护 codec/侧库测试 hash 仍匹配 R54。保留已有未提交工作。 |
| 未验/下一片 | G2/VIS/FLOW **N/A**：没有 UI/交互改动。仍需独立纯只读现场 Journal snapshot 与缺 intent 时的明确 lookup 边界，再接批准的生产确认/runtime/诊断入口；仍未验真实崩溃/重启/跨账户/掉电/其他卷/最低系统/全状态回滚/生产完整旅程，不宣称产品整体完成或 exactly-once。 |

## 2026-11-02 — Live context 与实际 owner 同源关联发行（R54）

[本片范围](<docs/architecture.md#2026-11-02--live-context-与实际-owner-同源关联发行r54>)接上内部真实 Journal callback、R49 采集、同一实际 owner 的 Link 发行/回读；验收跨至 11 月 3 日。生产原生三路仍关闭，没有新 SDK/IPC/Helper/GUI 接入，也不是用户确认或 unknown/restart 对账。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 同源工厂 | [唯一新 ctx 入口](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryOperation.cs>)只从 frozen request 严格解析 startup-disable/name/ExpectedRevision/selection 并调用 R49，拒绝其他命令、缺字段、未知/重复/错大小写/null/已禁用/注册来源/错 metadataDigest。未新增 caller ctx+raw candidate/record/IDs 的入口。 |
| DATA — 构造边界 | 末轮把 owner 构造器的裸 ctx 参数收紧为私有构造的 opaque draft ticket；ticket 只能通过 live ctx→Strict selection→R49 采集，Core 拿锁前后/候选发布前及 owner 构造器都比对同 manager、exact candidate/record 与 active root。readonly ctx 只从该 ticket 复制；ticket 不是认证或 Native 权限。 |
| DATA — 实际 TxnId | owner 自己的 TransactionId 同时进入原 intent 与新 protected Link，候选摘要取钉住的原 candidate 密文字节。测试实际解码 intent/Link，比较 ctx opId/fp、owner TxnId、exact candidate/recordId 和 proof digest，不按名称/时间推断。raw owner 无 ctx ticket 不补发，重复/Native 已执行而仍 candidate 的阶段也不再发行。 |
| DATA — 发布与回读 | [新发行阶段](<src/SystemAutorun.Windows/Startup/StartupHistoryOperationPublication.cs>)要求 owner linkIssuing、same session、精确 intent、未尝试 Native/ledger/commit、session 未使用且 exact candidate。[Stage 专用方法](<src/SystemAutorun.Windows/Startup/StartupHistoryWriteLease.cs>)只发固定 Link、nonreplace；旧 Publish 白名单不变，拒绝任何已有目标包括同义包，不采用、不覆盖、不重试。真实 R53 Reader/完整 seal 及 active ctx/intent 复核通过才返 owner；后续边界持续验证 Link。 |
| DATA — 真/假边界 | [新组](<tests/SystemAutorun.IntegrationTests/StartupHistoryOperationOwnerTests.cs>)真实 Journal callback、R49 server-observed draft、实际 owner、私有侧库/ACL/CurrentUser DPAPI；两来源各覆盖只准备与内部 rename→ledger→committed→finalize。**请求、callback response、Native 目录为夹具**；不把 synthetic callback 的 Completed/Replay 当生产协议或 Native 成功证明，也不操作真实 Startup 文件夹。 |
| DATA — 异常与寿命 | 取消覆盖 draft/intent/link-flushed/published/verified；回读字节与 late ACL 变化、Link 缺失/损坏、ended/foreign ctx、callback owner/准备工作逃逸均拒新动作并释放租约。仅清未发布 stage，保留真实 candidate/intent/Link，不修复半发布或回滚。不宣称逐条 after-admission 竞态全部穷尽或撤销已派发 Native。 |
| 测试修订与复核 | [首轮](<docs/visual/sa-20261102/r54-focused-initial.txt>)构建通过、组内 6 失败；[二轮](<docs/visual/sa-20261102/r54-focused-r2.txt>)剩 4 失败。根因均为测试：DELETE 预留期间普通读取不共享、Windows 首次 DACL 写增加 AUTO_INHERITED、正常 IntentPublication 后还有 TemporaryCleanup。改为 owner Dispose 后比 exact bytes、ACL 注入前显式原 descriptor 归一化且 finally 原二进制还原、精确核对两种真实 adapter calls。主线另发现 Journal 转 unknown 可吞 callback 断言，所有新组执行加 drain 后异常转抛，并有 sentinel 测试。独立限定只读复核未发现生产缺陷；主线仍主动收紧 ctor ticket，末轮专项复核未发现该错绑/转抛改动的可复现问题，不把复核替代真实验收。 |
| ENG — 末轮焦点 | [修订阶段](<docs/visual/sa-20261102/r54-focus-final.txt>) **exit 0**，旧 32 组及历史 **614/614**。ticket 加固后[封闭焦点](<docs/visual/sa-20261102/r54-focus-sealed.txt>)构建 **0 警告/错误**，新组 **35/35** 与历史 **617/617** 均通过、**0 跳过**、**exit 0**。[冻结源](<docs/visual/sa-20261102/r54-sources.txt>)含 5 项已测源。 |
| ENG — 完整矩阵 | [冻结后矩阵](<docs/visual/sa-20261102/r54-matrix-final.txt>) **exit 0**：Debug/Release 均 **0 警告/错误**，Core 各 **530/530**，记录所列 integration 筛选集各连续两轮 **1578/1578**，共四轮均 **0 跳过**。环境仅 Windows 11 x64 **10.0.28000 / C: NTFS**；不是全量无筛选/跨平台/真实产品旅程验收。 |
| 未验范围 | G2/VIS/FLOW **N/A**：无 UI/交互改动。仍未做生产 SDK/IPC 写链、确认/runtime、Helper apphost/runas/UAC、只读 unknown/restart 对账、跨账户/真正重启/掉电、全状态回滚、restore/rebuild、其他卷/过滤驱动/最低系统与完整批准旅程；不声称 exactly-once 或产品整体完成。 |
| 清理与完整性 | [切片前基线](<docs/visual/sa-20261102/r54-baseline.txt>)临时项 **0**；[末轮审计](<docs/visual/sa-20261102/r54-cleanup-audit.txt>)相关进程、私有注册根、fixture 服务均 **0**，原配置/gate/Journal/context/协议/旧格式/reader/codec/R49 采集 hash 未变，5 项冻结源 hash 匹配。仅原 owner 与 write lease 为本片预期改动；R53 reader/read lease/codec/测试均不改。canonical profile 侧库及 Helper log 均未生成。[最终清理](<docs/visual/sa-20261102/r54-cleanup-final.txt>)只删除已核实非 reparse 且空的共享测试父目录，最终临时项 **0**、**exit 0**；[扩围复核](<docs/visual/sa-20261102/r54-cleanup-scope-final.txt>)另覆盖 SAFixture 映像、临时根可执行文件、注册/服务别名及全部 brand 临时前缀，四类计数仍为 **0**。已有未提交工作保留。 |
| 下一片 | 接生产确认/runtime 前，先用真实关联来源完成只读 unknown/restart 证据汇总和明确失配/缺失边界；不得由 Link 或 candidate 自动推进回执、重放 Native 或补旧证。整体目标继续。 |

## 2026-11-02 — 关联侧库真实只读路径（R53）

[本片范围](<docs/architecture.md#2026-11-02--关联侧库真实只读路径r53>)将 R52 比较接到真实私有目录/文件/ACL/DPAPI，不是实际 owner 发行，不是 operation 扫描或生产对账。生产原生三路继续关闭，旧记录不补证。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 只读入口 | [新 Reader](<src/SystemAutorun.Windows/Startup/StartupHistoryOperationStoreReader.cs>)只接受当前 record context 与预期 operationId/fingerprint/transactionId，使用固定 `<recordId>.operation-v1.dpapi`。既有 index 认证并选择 current proof，不从 Link 自报 latest，不按名称/时间猜配。 |
| DATA — 读写准入分离 | [读 lease](<src/SystemAutorun.Windows/Startup/StartupHistoryReadLease.cs>)仅并列独立 Link 叶白名单，IsProofName、旧 Reader、[Stage.Publish](<src/SystemAutorun.Windows/Startup/StartupHistoryWriteLease.cs>)与 owner 不改；新名可读不授予旧 publisher 写权。Reader 不调用会创建目录/锁的 write lease，不创建缺失材料、不修 ACL 或重建 index。 |
| DATA — seal | 同目录读 lease 钉真实 Root/私有 ACL、index/current proof/Link 及 committed 的 candidate proof，R52 完整关联匹配之后仍只暂存结果。逐件 VerifyCurrent、目录 seal、活 mapping/actor 全通过才返回关联 ID；没有 payload/ID 观察器。late ACL/mapping/cancel 不能把早到匹配泄露出去。 |
| DATA — 缺失边界 | 仅 Link 固定叶的 FILE_NOT_FOUND(2) 暂存 unlinked，仍 seal index/current/目录及 mapping/actor，且不要求 legacy committed 补 candidate；缺失没有可钉句柄，不是原子不存在证明。现存空包、坏域/过大/硬链接/reparse、缺 parent、坏 index/current、path-not-found(3) 或上下文漂移都不是关联成功，失败无 ID；旧历史可读性不随 Link 状态降级。 |
| DATA — 已执行用例 | [新增测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryOperationStoreTests.cs>)真实私有外部文件/ACL/CurrentUser DPAPI，实测全部叶/目录持有时写删换被拒，取消释放，late ACL/mapping 拒绝且无 ID，中途替换 parent/Link/current、Root 同路径换身份、硬链接/reparse 不读目标、三元组/域/大小/缺失与占用拒绝、旧历史无 Link 仍可读及旧 Stage 拒新叶。**发行/current/index 为夹具合成**，不计实际 owner 发行、原生结果、账本终结或多文件 atomic。 |
| 只读设计与代码复核 | 限定设计咨询确认独立 Reader 有验收价值，要求仅 read lease、保持写白名单闭集、缺 Link 也要 seal 当前目录、旧历史旁路不降级；已落实。独立只读复核指定 378 行，未运行构建/测试，未发现可复现代码缺陷或关键覆盖薄弱项；主线另行运行真实测试，不把复核意见替代验收。 |
| ENG — 焦点 | [首轮](<docs/visual/sa-20261102/r53-focused-initial.txt>)与[冻结后焦点](<docs/visual/sa-20261102/r53-focus-final.txt>) **exit 0**，构建 **0 警告/错误**，新组 **52/52**、全部历史 **582/582**，均 **0 跳过**；[冻结源](<docs/visual/sa-20261102/r53-sources.txt>)对应新 Reader、读 lease 与测试。 |
| ENG — 完整矩阵 | [最终 Debug/Release 矩阵](<docs/visual/sa-20261102/r53-matrix-final.txt>) **exit 0**：两配置构建各 **0 警告/错误**，Core 各 **530/530**，已审 integration filter 各连续两遍 **1543/1543**，全部 **0 跳过**。矩阵期间源码冻结，未用测试/复核替代未做的生产旅程。 |
| 平台与未验范围 | [本次矩阵](<docs/visual/sa-20261102/r53-matrix-final.txt>)实测 Windows **10.0.28000**、本地 **C: NTFS**。G2/VIS/FLOW **N/A**：没有 UI/旅程变化。仍未接 live ctx→actual owner 的发行/发布，也未开放生产 SDK/IPC 写链、Helper apphost/runas/UAC、unknown/restart 回执对账、真正跨账户/重启/掉电、抗全状态回滚、restore/rebuild、其他卷/过滤驱动/最低系统或完整批准旅程；本片不宣称原生权限或 exactly-once。 |
| 清理与完整性 | [切片前基线](<docs/visual/sa-20261102/r53-baseline.txt>)临时项 **0**；[最终清理审计](<docs/visual/sa-20261102/r53-cleanup.txt>) **exit 0**，相关进程/私有注册根/夹具服务/最终临时项均 **0**，仅删除已核实空且非 reparse 的共享测试父目录。canonical 生产历史目录与 profile Helper log 仍不存在。19 项配置/gate/Journal/context/协议/旧格式/Reader/Writer/owner/codec hash 未变，3 项已测源 hash 与冻结源一致；已有未提交工作保留。 |
| 下一片 | 由同一实际 owner 的 TxnId 与 R51 live context 同源发行/发布新 Link，使用本片 Reader 做真实回读；保留旧无 Link 记录只读、不补证。随后才可接生产确认/runtime 与只读 unknown/restart 对账。整体目标未完成。 |

## 2026-11-02 — 独立操作关联格式与只读比较（R52）

[本片范围](<docs/architecture.md#2026-11-02--独立操作关联格式与只读比较r52>)仅为关联格式、CurrentUser DPAPI codec 与给定证据比较，不是持久发行/真实侧库 Reader，不完成生产三 ID 关联或 unknown/restart 对账；生产原生三路继续关闭。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 独立格式 | [OperationLink schema1](<src/SystemAutorun.Windows/Startup/StartupHistoryOperationFormat.cs>)保存规范 UUIDv7 operationId、既有 fingerprint、规范 transactionId、exact v1 candidate 与其受保护密文摘要。recordId/Root/Actor/Source 等只取 candidate，不重复可漂移参数；candidate 限 candidate/Sequence1。这是结构约束，不证明真实发行；不新增 live ctx→rawcandidate 工厂。 |
| DATA — 兼容与保护 | 原历史 DTO/校验/序列化、index/intent 格式与现有 reader/owner 不变，schema2 仍拒绝。[独立保护域](<src/SystemAutorun.Windows/Startup/StartupHistoryOperationProtection.cs>)有新 magic/CurrentUser entropy，复用原 DPAPI 原语只增加互斥域选项，旧默认域/上限不变。严格 JSON 拒未知/重复/缺失/错大小写/null/非法版本；plain 64 KiB、cipher 128 KiB，有取消、主 token 复核与暂存清零，不声称完全擦除 CLR/OS 内存或应用归属。 |
| DATA — 给定证据关联 | 包装器冻结三份 bytes，先验 current proof 与调用方独立认证 latest，再验 candidate 密文摘要及 exact candidate；current 只能是同 candidate 或同 Binding/CandidateAt、Sequence+1 的 committed。只有相符才返回 associated_candidate/associated_committed 与关联 ID，缺 link 为 unlinked，错配/损坏 unverified 无 ID；不改变回执，不自动恢复/重建，不返回原生权限。 |
| DATA — 已执行用例 | [新增测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryOperationTests.cs>)以真实 CurrentUser DPAPI/临时文件身份，配合**合成 current 与 fixture protected index**，覆盖旧 v1 literal JSON golden、旧 codec 往返及 schema2 拒绝、新旧域隔离、严格格式/大小/取消/impersonation、三 ID/fp 与 physical context 变化、同槽不同 candidate、重保护 parent 密文替换、committed-parent 连续性与 latest 缺失/错配。实测 native writable opens、adapter calls 与 root 前后字节，不以固定权限 bool 自测代替。 |
| 设计与代码复核 | 限定只读设计咨询建议独立包而非扩展通用 payload，防止旧 owner 另选 TxnId 错绑。独立代码复核确认主保护/比较边界，指出上下文测试早退只覆盖外层；修订为同 actor 的变更 proof 通过外层再进 Link 比较，foreign actor 分开做前置拒绝与纯比较。补精确 Reason、新包 literal golden、非法 phase/disposition 拒绝。复读原 v1 Validate 确认它已限定 phase 与 disabled，复核所称“restored 会通过”的生产缺陷不成立，未加冗余生产分支。 |
| ENG — 首轮与修订焦点 | [首轮](<docs/visual/sa-20261102/r52-focused-initial.txt>)构建 **0 警告/错误**，新增 **52/52**；[首版历史焦点](<docs/visual/sa-20261102/r52-focus-final.txt>) **527/527**。补强后[修订冻结源](<docs/visual/sa-20261102/r52-sources-r2.txt>)对应的[最终焦点](<docs/visual/sa-20261102/r52-focus-r2.txt>) **exit 0**，构建 **0 警告/错误**，新组 **55/55**、全部历史 **530/530**，均 **0 跳过**；三份生产源与首版相同，仅测试补强。 |
| ENG — 完整回归 | [最终 Debug/Release 矩阵](<docs/visual/sa-20261102/r52-matrix-final.txt>) **exit 0**：两配置构建均 **0 警告/错误**，Core 各 **530/530**，已审 integration filter 各连续两遍 **1491/1491**，全部 **0 跳过**。矩阵开始后未改源码；两份新增生产文件、共用保护原语与新测试均与修订冻结源一致。 |
| 平台与未验范围 | [本次矩阵环境](<docs/visual/sa-20261102/r52-matrix-final.txt>)为 Windows **10.0.28000**、本地 **C: NTFS**。G2/VIS/FLOW **N/A**：无 UI/交互变化。无真实 sidecar 命名注册/文件 IO/operation 扫描/Reader seal/生产发行。未来同一 lease 必须钉 index/latest proof/candidate proof/link 并 seal 后才发布，不能用现有 observer 的早到回调冒充成功；当前 byte snapshot 只稳定传输。尚未验证用户确认、真实生产 SDK/IPC 原生链、Helper apphost/runas/UAC、跨账户/真正重启/掉电、restore/rebuild、其他卷/过滤驱动/最低系统或完整批准旅程。 |
| 清理与完整性 | [切片前基线](<docs/visual/sa-20261102/r52-baseline.txt>)临时项 **0**；[最终清理审计](<docs/visual/sa-20261102/r52-cleanup.txt>) **exit 0**，相关进程/私有注册根/夹具服务/最终临时项均 **0**，仅删除已核实空且非 reparse 的共享测试父目录。canonical 生产历史目录与 profile Helper log 仍不存在。17 项原配置/gate/Journal/context/协议/旧格式/reader/owner/旧格式测试 hash 未变，4 项已测源 hash 与冻结源一致；已有未提交工作保留。 |
| 下一片 | 从 R51 live context 及同一个实际 owner 的 TxnId 发行/发布新关联，并接入同 lease 的真实 reader 核对；旧记录不补证。此后再做受保护确认/runtime 与 unknown/restart 对账，不能将这次比较当成 exactly-once 或 Native capability。整体目标未完成。 |

## 2026-11-02 — 回调期执行来源冻结（R51）

[本片范围](<docs/architecture.md#2026-11-02--回调期执行来源冻结r51>)是纯 Core 的 live 执行快照前置，不是三 ID 的持久受保护关联。生产原生三路仍关闭，历史 v1/intent 编码、回执 schema 与 IPC/CLI/GUI 均不变。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 快照 | [Journal 新入口](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L46-L86>)先规范化 operationId，首个 await 前深 clone payload；同一 snapshot 用于既有指纹及回调。[新上下文](<src/SystemAutorun.Core/Ipc/OperationExecutionContext.cs>)复用 pending 的 AcceptedAt、本实例 ID，并在同一次 pending Root lease 内捕获 physical namespace。旧 RunAsync 不获得新增闭包冻结保障；新旧 API 共用原准入与回执，不建第二份日志。 |
| DATA — 生命周期 | Root 释放后才发上下文；回调 finally 使独立 active 位失效，早于结果分类、序列化/回执。RequireActiveFor 是 namespace/生命周期时点检查，不是目录对象身份、native CAS、主体认证或确认。原始 clone 在失效后仍可作历史数据读取，不再是 live 执行来源。JsonIgnore 避免默认把 Request 带进回执，但不是阻止调用方显式泄露的沙箱。 |
| DATA — Core 验收 | [Core 新测试](<tests/SystemAutorun.Core.Tests/OperationExecutionContextTests.cs>)用真实临时 root/文件锁与 JSON 文档：持 Root 阻塞准入后先 Dispose 原文档，callback 可用 snapshot、Root 探针可取得锁、fingerprint/AcceptedAt 与实际 pending 一致；动态序列化探针观察 active 已为 false。另覆盖错误/取消/null 返回、同 ID 并发、跨实例 unknown、旧新 API 双向回放、指纹规范化与四项字段变化、容量/时间/参数拒绝、回执 publication fault 与默认序列化不带请求。 |
| DATA — 原生组合 | [两临时来源的真实 COM .lnk 组合](<tests/SystemAutorun.IntegrationTests/StartupHistoryExecutionContextTests.cs>)只从 context.Request 取选择与 revision，实际等待 factory→typed owner→archive/ledger→committed→finalize→Dispose；原请求在 callback 中已 Dispose，链仍完成。再验终结后抛错得到 unknown、同 ID 不重做、root/侧库/checkpoint 不变。该测试不接生产 dispatcher，不生成持久 operation link。 |
| 复核与修正 | 限定设计咨询建议先做快照/生命周期、OperationLink 另片版本化。独立只读代码复核确认主要边界；主线复读 R50 helper 的实际 flags 投影，排除“DTO 仍自设 false”的旧信息假阳性。首次持锁用例补协作 deadline 后仍 drain，不采用 WaitAsync 后弃置 workflow；null 响应不能证明无写，保留旧 unknown 分类并补验证，不改成 not_executed 或假成功。 |
| 首轮与最终焦点 | [首轮](<docs/visual/sa-20261102/r51-focused-initial.txt>)构建 **0 警告/错误**，Core 相关 **33/33**、原生组合 **4/4**。补边界用例后，[最终焦点](<docs/visual/sa-20261102/r51-focus-final.txt>) Core 相关 **38/38**、原生组合 **4/4**、全部历史 **475/475**，均 **0 跳过**、构建 **0 警告/错误**。[冻结源](<docs/visual/sa-20261102/r51-sources.txt>)记录四个本片源及不变的 Protocol/R50 helper。 |
| ENG — 完整回归 | [Debug/Release 矩阵](<docs/visual/sa-20261102/r51-matrix-final.txt>) **exit 0**：两配置构建均 **0 警告/错误**，Core 各 **530/530**，同一 reviewed integration filter 各连续两轮 **1436/1436**，均 **0 跳过**；矩阵期间未改源码。 |
| 平台与未验范围 | [本次矩阵环境](<docs/visual/sa-20261102/r51-matrix-final.txt>)为 Windows **10.0.28000**、本地 **C: NTFS**。G2/VIS/FLOW **N/A**：纯 Core/API 及隔离测试，无界面或产品旅程变化。未验证真实生产 SDK/IPC 原生执行、业务确认、Helper apphost/runas/UAC、服务/进程真正重启、掉电、跨账户、其他卷/过滤驱动/最低系统或 restore/rebuild。上下文不撤销既有 owner，不保证 fire-and-forget 或检查后的所有竞态。 |
| 清理与完整性 | [本片前基线](<docs/visual/sa-20261102/r51-baseline.txt>)与[最终审计](<docs/visual/sa-20261102/r51-cleanup.txt>)：相关进程、私有注册根、夹具服务与最终临时项均 **0**；仅删核对绝对路径、非 reparse 且为空的共享测试父目录。三份生产配置、R47 三入口、Protocol、历史 v1/intent codec、工厂/typed owner/Capture 及 R50 helper hash 不变；六个冻结源一致，生产历史目录及 profile Helper 日志仍不存在。已有未提交工作保留，未 commit/reset。 |
| 下一片 | 对受保护 OperationLink 独立做版本化、完整请求关联与只读匹配；旧 v1 保持不补证。再接入受保护的确认与 runtime 调度，并完成 unknown/restart 对账，仍不得按名称/时间猜记录。HS02/HS04 与整体目标均未完成。 |

## 2026-11-02 — Journal 与历史 owner 的隔离组合验证（R50）

[本片范围](<docs/architecture.md#2026-11-02--journal-与历史-owner-的隔离组合验证r50>)仅测试已有协议的组合；**生产代码、dispatcher/CLI/GUI 路由、回执 schema 和原生写权限均不变。** 通用 Journal callback 不等于已注册的 startup-disable 或用户确认。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 组合 | [新增隔离测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryJournalTests.cs>)对两个临时固定来源、真实 COM .lnk、owned root/私有侧库直接组合 Journal 与 fresh factory→typed owner→archive/ledger→committed→finalize。回调入口及 owner Dispose 后各取 Root 探针；完整实际 workflow 完成后才清夹具，不以 caller 超时替代 drain。所有快照布尔从真实各层返回值构成，不给测试 DTO 自设常量冒充生产状态。 |
| DATA — 不重放 | 覆盖成功结果/变参 ID 冲突、同 ID 并发、新 Journal 实例观察 unknown、结果省略不补执行。native 前后、ledger、commit、finalize、owner Dispose 后故障及 native 后取消均有隔离用例；回放比较 callback、真实 native_renamed checkpoint、root/侧库 bytes，早期无侧库场景另断言仍缺目录与无写操作。不借旧库 NativeMutation/NativeRollback 计数证明新链。 |
| DATA — 回执丢失 | 已完成原生及历史并释放 owner 后，以精确 pending 回执叶的只读共享句柄阻止替换；捕获 AgentOutcomeUnknownException，磁盘仍 Running/无 Response，新实例观察 Unknown，同 ID 不再进入 callback。省略结果则保持 Completed，回放 operation_result_unavailable；两者都不重跑原生，不等于 exactly-once。 |
| DATA — 错误边界 | 测试适配器只在 factory 阶段已知配置冲突返回 revision_conflict；callback 已进入的取消仍未知。startup-adopt 专属的 startup_entry_changed 无写白名单不向原生命令标签泛化。不将这些测试映射当作已上线的 runtime 实现。 |
| 首轮失败与修正 | [首次焦点](<docs/visual/sa-20261102/r50-focused-initial.txt>)构建 **0 警告/错误**，测试 **6 通过/18 失败**。根因是测试错误挂旧 NativeMutation 边界，新链并不走它；仅将夹具故障/计数改为真实 before_native/native_renamed checkpoint。[更正焦点](<docs/visual/sa-20261102/r50-focused-corrected.txt>) **24/24**。生产实现未改、未跳过失败。 |
| 复核与最终焦点 | 限定独立只读复核指出 DTO 常量断言、侧库/早期 unknown 快照遗漏、receipt 重放穿透时可能重开占用柄及并发等待边界。主线改从真实各层投影 flags，补 root/侧库/无目录回放断言，receipt 回放换不可达防卫委托，并加协作 deadline 后仍 drain。Root 等待原本已为 15 秒有界，不能把 WaitAsync 后丢任务当修复；未声称任意内核死锁可取消。[最终焦点](<docs/visual/sa-20261102/r50-focus-final.txt>) **24/24**、全部历史 **471/471**，构建 **0 警告/错误**、无跳过。 |
| ENG — 最终矩阵 | [最终矩阵](<docs/visual/sa-20261102/r50-matrix-final.txt>) **exit 0**：Debug/Release 构建均 **0 警告/错误**；Core 各 **505/505**，同一 reviewed filter 的集成各连续两次 **1432/1432**，均 **0 跳过**。[冻结源](<docs/visual/sa-20261102/r50-sources.txt>)记录一个新增测试与五个既有生产依赖，矩阵期间未改源代码。 |
| 平台与未验项 | [本次环境](<docs/visual/sa-20261102/r50-platform.txt>)为 Windows **10.0.28000**、本地 **C: NTFS**。G2/VIS/FLOW **N/A**：只增加隔离测试，无产品界面/旅程变化。未验证真实 IPC/SDK 原生执行、Helper apphost/runas/UAC、服务/进程真正重启、掉电、跨账户、其他卷/过滤驱动/最低系统、restore/rebuild 或批准双端全旅程。新实例不等于真实 restart，Root 可编辑日志不是恶意同 SID 沙箱或全局 native CAS。 |
| 清理与完整性 | [测试前基线](<docs/visual/sa-20261102/r50-baseline.txt>)与[最终审计](<docs/visual/sa-20261102/r50-cleanup.txt>)对比：相关进程、私有注册根、夹具服务及最终临时项均 **0**；只删核对绝对路径、非 reparse 且为空的共享测试父目录。三份生产配置、R47 三入口、Journal/Protocol 及既有生产依赖 hash 均不变；六个冻结源值一致，生产历史目录与 profile Helper 日志仍不存在。已有未提交工作保留，未 commit/reset。 |
| 下一片 | 在仍关闭原生生产写入口的条件下，先明确 operationId/TransactionId/RecordId 的受保护关联与 unknown/restart 对账，不得按名称/时间猜配已终结记录；再推进受保护请求、业务确认与 runtime 错误映射。完成新记录恢复/重建权限后才接批准双端入口。HS02/HS04 及整体目标仍未完成。 |

## 2026-11-02 — 服务器派生的活动快捷方式草稿（R49）

沿用 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--服务器派生的活动快捷方式草稿r49>)只覆盖内部事实生产与二次准备。**不新增生产双端写入口，不把 snapshot 当确认令牌，不开放恢复/重建或 restart 修复。**

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 | [工厂](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryDraft.cs>)只接现有活动 .lnk 选择与完整 workspace revision。服务器从真实 scope/held input、pin 内 COM 元数据及当前配置生成新 GUID/nonce/UTC、记录摘要与固定路径，不接 caller payload/记录/身份/归档字段。它不签 proof、不写 ledger/intent、不移动原叶；既有准入可创建协调锁文件，不冒充产品纯只读查询。返回前释放 pins，四个授权/执行布尔恒 false。 |
| DATA — 消费 | [typed 准备](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryTransaction.cs#L26-L75>)重新 Native→Root，检查完整三文件 revision、新记录条件与 fresh DELETE capture 的来源/主体/FileID/长度/link/SHA；签 candidate 前再核归档身份、卷、case、error 2 空目标和双方 Startup 布局。旧 raw 内部桥保留兼容，不据此声称公开 SDK 受保护或已经接入 Journal。 |
| DATA — 隔离结果 | [新测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryDraftTests.cs>)只用临时来源、owned root/私有侧库及真实 COM .lnk；不运行目标，不触碰生产 Startup。覆盖两来源生成与完整内部 owner 链、重复采集、旧记录不补证、三文件 revision/原叶字节与身份/来源/归档漂移、目标占位、错误选择、三个取消边界、同叶写删/改名拒绝、缺目录与另一 Startup 内归档拒绝。[首次焦点](<docs/visual/sa-20261102/r49-focused-initial.txt>) **25/25**；补充归档准入后的[最终焦点](<docs/visual/sa-20261102/r49-focus-final.txt>) **30/30**、全部历史 **447/447**，构建 **0 警告/错误**，无跳过。 |
| 复核 | 设计咨询确认扫描返回 entry 不能直接绑定稍后 capture；工厂改为同一原叶只读 pin 内重新采集。首次后台安全审查未取得可用结果，**不计通过**。续接后的限定独立代码复核未发现可确立阻断；它未展开底层共享标志，主线另亲读 [OpenRelative](<src/SystemAutorun.Windows/Startup/StartupNativeSlotProbe.cs#L129-L145>) 的默认 share=1/只读访问，核对[微软 ShareAccess 语义](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-zwcreatefile)，并以实际 pin 下写入/改名/删除均被拒绝的隔离用例佐证本机行为。不将此结论扩大为同 SID 沙箱或全平台证明。 |
| ENG — 矩阵 | [最终矩阵](<docs/visual/sa-20261102/r49-matrix-final.txt>) **exit 0**：Debug/Release 构建均 **0 警告/错误**；Core 各 **505/505**，同一 reviewed filter 的集成各连续两次 **1408/1408**，全部 **0 跳过**。[四个冻结源](<docs/visual/sa-20261102/r49-sources.txt>)记录本次构建输入；矩阵期间未改源代码。 |
| 平台与未验项 | [本次环境](<docs/visual/sa-20261102/r49-platform.txt>)为 Windows **10.0.28000**、本地 **C: NTFS**。G2/VIS/FLOW **N/A**：无界面/用户交互变化。未验证生产 Startup/Helper apphost/runas/UAC、其他卷/过滤驱动/最低系统、掉电/跨账户、runtime/Journal、restart/unknown 恢复、新记录恢复/重建或完整双端旅程。 |
| 清理 | [测试前基线](<docs/visual/sa-20261102/r49-baseline.txt>)与[最终审计](<docs/visual/sa-20261102/r49-cleanup.txt>)对比：相关进程、私有注册根、夹具服务和最终临时项均 **0**；只移除核对绝对路径、非 reparse 且为空的共享测试父目录。三份生产配置、R47 三入口 hash 均不变，四个源 hash 与冻结值一致；默认生产历史目录及 profile Helper 日志仍不存在。未提交或清退已有工作。 |
| 下一片 | 继续受保护请求与 runtime/Journal 协调、unknown/restart 对账；草稿只能作为待重新核验的服务器观察值，不是可序列化的写能力。完成恢复/重建权限后再接批准双端原生旅程；整体目标仍未完成。 |

## 2026-11-02 — 活 owner 显式终结（R48）

沿用 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--活-owner-显式终结r48>)只覆盖仍持原租约的内部 owner。**不接生产双端写入口，不实现 restart 清理或恢复/重建权限。**

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 | [owner](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryFinalization.cs>)只认自身成功 committed 锁存；同 gate 串行 Finalize/Commit/Dispose，复核原生/主体/当前配置/精确账本、私有 intent、expected proof 及完整 committed index 镜像。派发前先缓存 uncertain，异常即使向外抛也不能重试。成功只在全部核验及局部释放后发布；不改原查询/commit 的 false 常量，不回滚或补意图。 |
| DATA — 清理 | [清理租约](<src/SystemAutorun.Windows/Startup/StartupHistoryIntentCleanup.cs>)只收实际 held Root 与派生固定叶；普通单 link、非 readonly/reparse、bounded 同柄 bytes/EOF，保留 DELETE。先设置 disposition，再明确 close 删除柄，再同 parent 相对 probe；只有 error 2 算观察缺失，替代叶和不确定均不删第二次，无路径 fallback。 |
| DATA — 隔离用例 | [新增测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryFinalizationTests.cs>)覆盖两个临时来源、真实 .lnk、原空 raw、未承认 commit、完整 index 漂移/其他输入漂移、取消、前后/未预期故障、替代 intent、重复调用、并发与 Dispose；仍只用 owned root/私有侧库，不执行目标，不触碰真实 Startup。最终 focus **34/34** 通过；全部历史 focus **417/417** 通过，无跳过。 |
| 首轮失败与修正 | [首轮](<docs/visual/sa-20261102/r48-focused-initial.txt>)构建成功，focus **5 通过/23 失败**；失败发生在测试快照 File.ReadAllBytes 不共享 DELETE、与活归档 reservation 冲突，尚未到清理动作。仅修夹具 Read + Delete 共享与现有 reader 的 none 断言，不放宽生产 pin；[更正 focus](<docs/visual/sa-20261102/r48-focused-corrected.txt>) **28/28**，[生命周期 focus](<docs/visual/sa-20261102/r48-focus-final.txt>) **32/32**，构建均 **0 警告/错误**、无跳过。其后补 dispatch 未预期异常两例及成功发布前显式局部释放。 |
| 审查 | 独立设计咨询指出 POSIX 需先 close，主线查[微软语义](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-_file_disposition_information_ex)并落实。限定静态安全复核提示 dispatch 异常漏接、success/Dispose 次序和 legacy 区分；主线已补预锁存及释放次序，并亲读同步 operations、祖先 pin、私有 intent 来源/512 KiB 上界。旧库路径清理仍存在，但当前生产命令未接 legacy Disable/Restore，新方法也未接业务入口；不宣称全库只走新租约。 |
| ENG — 最终矩阵 | [最终矩阵](<docs/visual/sa-20261102/r48-matrix-final.txt>) **exit 0**：Debug/Release 构建均 **0 警告/错误**；Core 各 **505/505**，相同 reviewed filter 的集成各连续两次 **1378/1378**，均 **0 跳过**。主线 review 后亲跑；[冻结源 SHA256](<docs/visual/sa-20261102/r48-sources.txt>)与最终审计一致，没有用旧构建覆盖新代码。 |
| 审查事实对齐 | F1 把库中的旧代码误称当前生产调用：R47 已关业务路由，新终结不接线正是本片范围；共用 IntentCleanup 是故障边界，不是日志。F2 中 AggregateException 原已被捕获，实际未列出的 NotSupportedException 以前/后两例证明预锁存有效。F3 改为局部释放完成后才赋 finalized，不先发布成功再降级；[结果](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryFinalization.cs#L10-L14>)始终含计算为 false 的 NativeWriteReady/ExecutionRequested 属性。 |
| 平台 | [实际环境](<docs/visual/sa-20261102/r48-platform.txt>)为 Windows **10.0.28000**、本地 **C: NTFS**；只证明这一环境上的隔离行为，不扩大兼容性结论。 |
| VIS/FLOW 与未验项 | **N/A**：无可见界面或用户交互变化；内部显式调用不等于用户确认。未验证生产 Startup/Helper apphost/runas/UAC、最低 Windows/其他卷及过滤驱动、掉电、跨账户、restart 清理、runtime/Journal、恢复/重建与批准双端完整旅程。成功缓存只是历史操作快照，不是 native CAS/ABA、SDK 或恶意同 SID 沙箱。 |
| 清理 | [测试前基线](<docs/visual/sa-20261102/r48-baseline.txt>)与[最终审计](<docs/visual/sa-20261102/r48-cleanup.txt>)对比：相关进程、私有注册根、夹具服务和最终临时项均 **0**；只删除已核对绝对路径、非 reparse 且为空的共享夹具父目录。生产配置/R47 三入口 hash 均不变，六个已测源文件 hash 保持冻结值；默认生产历史目录与 profile Helper 日志均不存在。 |
| 下一片 | 继续受保护请求/draft 准入、runtime/Journal 与 restart/unknown 恢复协议，保持未知旧 raw 前态；完成恢复/重建权限后才接批准双端原生旅程。整体目标仍未完成。 |

## 2026-11-02 — 关闭旧 Elevated 原生写绕行（R47）

按 [G1-history-r1](<docs/architecture.md#2026-11-02--历史凭据首版兼容范围g1-history-r1已批准>)「闭环前原生写关闭」修复旧入口；[本片边界](<docs/architecture.md#2026-11-02--关闭旧-elevated-原生写绕行r47>)。
**不清意图、不授予恢复/重建/终结，不安装或运行生产 boot 任务。**

| 类别 | 实际结果与边界 |
| --- | --- |
| 调研 — 修复前绕行 | 修复前 Helper 命令可直接调 legacy StartupManager.DisableAsync/RestoreAsync，未走新 history owner；App 仅保留未被当前 UI 调用的旧内部桥。Agent 第三方 startup-disable/restore/rebuild 未实现，CLI 仍标 pending。先封这条 Helper 命令入口，而不是据 crash/reader 匹配结果清意图。 |
| 实现 — 共享 gate | [Core 策略](<src/SystemAutorun.Core/Infrastructure/ElevatedCommandPolicy.cs>)仅精确允许已有 install-boot/uninstall-boot/start-boot，不是身份授权。[Helper](<src/SystemAutorun.Elevated/Program.cs>)在参数/root/管理员/调度器/日志前拒非支持命令，退出 **64**；删旧 native 写分支与参数。[App](<src/SystemAutorun.App/App.xaml.cs>)兼容桥恒 false，通用 launcher 在 runas 前用同一策略。原三个 boot 操作保留既有管理员检查，未扩大权限。 |
| DATA — 新增 9 例 | [隔离测试](<tests/SystemAutorun.IntegrationTests/StartupElevatedClosureTests.cs>)三退役命令 × 裸参数/坏参数/旧参数，在独占临时 test-apphost 的新进程中调用本次构建 Helper DLL 的真实 managed EntryPoint。均为 **64**，不创建请求 root，原文件/目录集合及字节不变，profile elevated.log 不创建/改动。仅只读 hash，不读/输出用户日志正文。夹具拒绝有效 boot 命令。 |
| ENG — 新增 15 例 | [策略测试](<tests/SystemAutorun.Core.Tests/ElevatedCommandPolicyTests.cs>)原三命令保持支持；空/未知/退役、大小写、前后空白、NUL/CRLF 拒绝。 [首轮 focus](<docs/visual/sa-20261102/r47-focused-initial.txt>)全方案构建 **0 警告/错误**、策略 **15/15**、隔离 closure 加既有 crash **25/25**，全部无跳过。 |
| ENG — 最终矩阵 | [最终矩阵](<docs/visual/sa-20261102/r47-matrix-final.txt>)退出 **0**：Debug/Release 全方案各 **0 警告/错误**、Core 各 **505/505**，沿已审查隔离 filter 各连续两轮 **1344/1344**；Debug 历史 focus **383/383**。全部 **0 跳过**，不是未筛选全集或产品完整旅程。[本次实跑源 hash](<docs/visual/sa-20261102/r47-sources.txt>)已保留。 |
| 审查 | 安全角色完成限定范围只读静态检索；主线复核 [App gate](<src/SystemAutorun.App/App.xaml.cs#L197-L215>)、Helper、共享策略、[Agent 派发](<src/SystemAutorun.Agent/AgentCommandDispatcher.cs#L135-L151>)与 [CLI 边界](<src/SystemAutorun.Cli/CliApplication.cs#L381-L404>)。当前 src 未见生产命令到 legacy Disable/Restore 或 native rebuild 的可达路，未发现本片阻断问题；这不是外部 DLL 调用、安装包/已运行二进制 provenance、UAC 或安全认证。 |
| VIS/FLOW 与未验项 | **N/A**：只改派发/内部 launcher，不改可见窗口、菜单、布局或视图模型。未做真实 Helper apphost/runas、OS manifest/UAC、boot 安装/卸载/执行、显式终结或产品完整旅程；本片不冒充 GUI VIS/FLOW 完成。直接运行 requireAdministrator exe 时，OS 仍可能先弹 UAC，App 提前 gate 不改变这个事实。 |
| 仍有边界 | Windows 库 legacy 低层 API 保留供隔离测试，不作为受保护 SDK/沙箱。只读权限 false、unresolved、旧无凭据记录门禁与 pending root intent 均不变；未来不得把业务路由接回 legacy 方法来绕过新链。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r47-cleanup.txt>)相关进程、私有注册根、fixture 服务、最终临时项均 **0**；仅删除当场验证绝对路径/父目录、非 reparse 且为空的共享测试父目录。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r47-baseline.txt>)一致；默认历史目录及 profile elevated.log 均未创建。既有未提交工作保留，无提交、安装、发布、生产写入或权限扩大。 |
| 下一片 | 继续显式终结与受保护生产准入，保持 Actor/当前配置/同对象证据及未知旧 raw 前态边界；完成 runtime/Journal 与恢复协议后，才接批准的双端原生旅程。 |

## 2026-11-02 — 隔离进程强杀与 fresh reader（R46）

继续 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--隔离进程强杀与-fresh-readerr46>)。
本片仅增加测试宿主与跨进程验收，**不改生产行为、不清意图、不恢复、不执行原目标**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 测试专用入口 | [集成项目](<tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj>)保留原程序集身份与已有友元，只补 Console OutputType/StartupObject；[Main](<tests/SystemAutorun.IntegrationTests/StartupHistoryCrashHost.cs>)只接受固定 owned-fixture 模式、canonical 普通临时根与独占映像分支。生产 CLI/Agent、IPC、发布工程和友元列表未改。测试仍由 VSTest 从同一 DLL 发现。 |
| 实现 — 独占进程与回收 | [夹具](<tests/SystemAutorun.IntegrationTests/StartupHistoryCrashTests.cs>)每次复制独立 SAFixture_R46_GUID.exe，使用固定 root 内来源映射，子进程启动注册表来源接口拒绝；父夹具既有私有注册根照常清理。父进程核阻塞 writer 的实际映像、marker PID/root/stage，直接 Kill 自己创建的 Process，等待退出与双流排空后才回收 root；不按通用映像名/参数匹配、不杀用户进程。短命 reader 退出后核同一 Process 的 PID/预期映像与报告。 |
| DATA — 新增 16 例 | 两个 owned 来源 × intent_verified/before_native/native_renamed/ledger_published/commit_proof_flushed/commit_proof_published/commit_index_published/commit_verified 八断点。种子均为实际 COM 创建的 .lnk，目标不存在且不执行；使用真实 NTFS、当前 Windows SID 与 DPAPI。checkpoint 内阻塞时强杀，owner finally 不参与收尾，由 OS 关闭句柄。 |
| DATA — 前后分离 | 每例先启动另一查询进程确认活 owner 为 busy、known-folder 调用 0；强杀后再启动两个不同 reader 进程，验证预期原槽/归档、空/proposed 账本与 candidate/protected_committed，wire 与对象四个权限均 false。对 data root、来源/归档和侧库的完整文件集合及字节前后比对，排除仅由父夹具新建的 host 映像分支；旧 Restore 仍拒 unresolved，意图保留。 |
| DATA — 部分发布不升级 | native_renamed 后账本仍空但实际归档匹配；commit_proof_published 后孤立 committed proof 不升级 candidate；flushed stage 不补发/删除；commit_index_published 后即使 writer 尚未返回，fresh reader 仍观察 protected_committed。查询不依赖旧进程内存、回执 ACK 或自动补证。 |
| 首轮失败与修复 | [初始构建](<docs/visual/sa-20261102/r46-build-debug-initial.txt>)缺 Core.Models using，3 错误；补引用后，[首次 focus](<docs/visual/sa-20261102/r46-focused-initial.txt>)15 通过、1 失败，定位到短 reader 启动/退出与 MainModule 枚举竞态。只读子进程改为退出后核 owned handle 对应 PID/映像报告；强杀 writer 仍必须核其阻塞中的实际模块，不重试或放宽 kill 边界。最终种子另升级为真实 COM .lnk。 |
| ENG — 实跑 | [修正后 focus](<docs/visual/sa-20261102/r46-focused-corrected.txt>)构建 **0 警告/错误**、新增 crash **16/16**。[最终矩阵](<docs/visual/sa-20261102/r46-matrix-final.txt>)退出 **0**：Debug 历史 focus **383/383**；Debug/Release 全方案各 **0 警告/错误**、Core 各 **490/490**，沿 R45 已审查隔离 filter，各连续两轮 **1335/1335**。全部 **0 跳过**，不是未筛选全集或产品完整旅程。[实跑源 hash](<docs/visual/sa-20261102/r46-sources.txt>)及抽查 R45 边界源不变已记录。 |
| VIS/FLOW 与未验项 | **N/A**：无 UI/业务 CLI/RPC 变化。不声称掉电、文件系统损坏、跨账户、系统服务/Agent 重启、显式终结、恢复权限、HS02/HS04 或完整产品旅程已通过；fresh reader 是测试程序集新进程，不冒充已接线的业务恢复。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r46-cleanup.txt>)相关进程、私有注册根、fixture 服务与最终临时项均 **0**。只删除当场核对绝对路径/父目录、非 reparse 且为空的共享测试父目录；三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r46-baseline.txt>)一致，canonical 默认历史目录未创建。保留既有未提交工作，无提交、安装、发布、生产写入或权限扩大。 |
| 下一片 | 明确显式终结与生产 mutation 的受保护准入协议，保留未知旧 raw 前态；完成外层 runtime/Journal 协调与恢复权限后，才接批准双端旅程。 |

## 2026-11-02 — Pending history 只读原生对账（R45）

继续 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--pending-history-只读原生对账r45>)。
本片只补内部原生对账，**不清意图、不写历史、不恢复、不执行、不接生产或双端业务入口**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 独立轴 | [入口](<src/SystemAutorun.Windows/Startup/StartupHistoryIntentNativeReader.cs>)保留 R44 metadata 与独立 Native check。旧 NativeState 恒 unobserved，新 check 仅报告 unobserved/original_matches/archive_matches/unverified；写入、执行、终结四个权限始终为 false。物理快照不随 protected phase 自动升级为业务完成。 |
| 实现 — 当前可信输入 | 先通过 R44 pending/镜像/独立历史准入，再持 Mutation→Root 既有只读协调柄与 bounded 配置快照。[纯 settings 投影](<src/SystemAutorun.Core/Configuration/ConfigurationLoader.cs>)共享原 YAML/schema/settings 解析，不开 overrides、不验证任务业务语义、不读目标；结束再投影环境路径。protected binding 的路径仅作当前路径一致性期望，认证后的原叶名/ID 经原布局规则派生实际槽，不探测账本历史路径。 |
| 实现 — 同柄与生存期 | [租约](<src/SystemAutorun.Windows/Startup/StartupHistoryNativeReadLease.cs>)持 root/source/archive base/archive 四目录及 present 双叶；目录末尾按当前路径重开比 volume/fileId/case。[内容柄](<src/SystemAutorun.Windows/Startup/StartupShortcutCapture.cs>)显式不请求 DELETE，拒 RenameHandle，身份/长度/单链接/SHA 同柄取得并复读。native pins 保留至历史 seal、root 元数据终检及最终来源/槽复查，早于配置/协调柄释放。 |
| 实现 — 独立关联不补证 | [reader](<src/SystemAutorun.Windows/Startup/StartupHistoryStoreReader.cs>)在受保护 index/proof/旧 candidate 完整关联后才调用只读 observer，调用中保留其句柄；late seal 或 root 终检失败会丢弃 native join。缺历史、错 Original、配置自声明重定向不能产生原生准入。旧 raw 前态仍未知；匹配并不允许终结。 |
| DATA — 新增 42 例 | [隔离对账测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryNativeReconciliationTests.cs>)覆盖两个 owned 来源与 prepared/native-unknown/ledger/committed 四阶段、八类前置拒绝、配置自声明与环境重定向、同字节换对象/同对象换字节、双存/双失、硬链接/reparse/目录/超限、三种目录身份替换、已有与晚到 writer、晚到原槽（含 history_read 末阶段）、来源换映射、case-sensitive 缺失、late index ACL seal、无关坏 overrides、取消及实际句柄释放。使用隔离目录映射和真实 Windows SID、NTFS、DPAPI，不操作生产 Startup 或执行原目标。 |
| DATA — 纯配置 8 例 | [投影测试](<tests/SystemAutorun.Core.Tests/ConfigurationSettingsSnapshotTests.cs>)验证不存在 root/override 也不创建文件、任务语义不在该投影范围、版本/必需段/负 delay/重复键/坏 YAML 与取消。普通完整 loader 继续读取 overrides 并验证任务，不改变其合同。 |
| 首轮失败与修复 | [首次 focus](<docs/visual/sa-20261102/r45-focused-initial.txt>)的较宽 History filter 为 376 通过、2 失败；[详细诊断](<docs/visual/sa-20261102/r45-focused-failures.txt>)定位到非空目录切 case 及 managed ACL 回写增加 AI 标志。夹具以保留对象身份的移出/移回切换空目录，并先建立稳定私有 ACL 基线后，finally 显式恢复原 DACL 二进制并核 SDDL。未跳过、放宽或删除用例。主线自查另把 native pins 延长至全查询终检，并加测晚到原槽。 |
| ENG — 实跑 | [修正后 focus](<docs/visual/sa-20261102/r45-focused-corrected.txt>)构建 **0 警告/错误**，新增 native **42/42**、纯配置 **8/8**。[最终矩阵](<docs/visual/sa-20261102/r45-matrix-final.txt>)退出 **0**；Debug 历史 focus **367/367**，Debug/Release 全方案各 **0 警告/错误**、Core 各 **490/490**，沿 R44 同一已审查隔离 filter，各连续两轮 **1319/1319**。全部 **0 跳过**。不是未筛选全集、跨进程强杀或产品完整旅程证据。 |
| 审查与声明 | 限定范围的只读安全咨询未证实阻塞项，不构成安全认证或权限审批；主线核验实际 actor/布局/helper，并随后补全 native 生存期。ObservedAt 是该次槽判断结束时间，不是跨轴原子时刻；不宣称 native CAS、ABA/整套回滚防护、返回时侧库全局最新，或防恶意同 SID/SYSTEM/管理员/既有映射写。 |
| VIS/FLOW 与生产接线 | **N/A**：本片无 UI/用户交互/CLI/RPC 变化。新 pending-history 方法没有生产调用；原 startup-inspect-native 的已有同名方法不等于新查询接线。未实测本片跨进程强杀/重启，也未完成 HS02/HS04、恢复权限及产品完整旅程。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r45-cleanup.txt>)相关进程、私有注册根、fixture 服务及最终临时项均 **0**。仅删除当场核实绝对路径/父目录、非 reparse 且为空的共享测试父目录。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r45-baseline.txt>)一致，canonical 默认历史目录未创建，实跑源 hash 已记录。保留既有未提交工作，无提交、安装、发布或生产试写。 |
| 下一片 | 隔离验证强杀/重启下的持久状态与只读查询，再完成显式终结协议、外层 runtime/Journal 协调及恢复权限，之后连接批准的双端旅程。 |

## 2026-11-02 — Typed intent 严格格式与无写入查询（R44）

继续 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--typed-intent-严格格式与无写入查询r44>)。
本片只补内部格式与 metadata/历史关联观察，**不清 root intent，不做启动项原槽/归档原叶对账，不接生产或双端入口**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 格式兼容 | [DTO/codec](<src/SystemAutorun.Windows/Startup/StartupHistoryIntentFormat.cs>)保留 R42 外层 camelCase、candidate PascalCase 与既有 record 属性名；[writer](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryTransaction.cs#L114-L115>)改为调用同一严格格式。对两个 owned 来源实比原匿名对象默认序列化字节，一字不变；不是数据迁移或版本升级。 |
| 实现 — 严格而不可信 | .NET 10 Strict 及 bounded bytes/depth 拒未知/重复、错误大小写、缺必需构造参数、非法版本/操作/ID/hash、前态声明矛盾、相同前后 hash，以及新 disable 元组的来源/名称/路径/时间/digest/phase/sequence 不一致。合法 shape 仍为可编辑 root metadata，不是 provenance、用户回执或权限。 |
| 实现 — 只读准入 | [观察器](<src/SystemAutorun.Windows/Startup/StartupHistoryIntentReader.cs>)按 Mutation→Root 只读打开既有空协调文件，不调用 writer Acquire，不创建锁或侧库；活 owner 或第二配置 writer 的共享冲突立即 busy。typed pending 缺协调文件仅 unavailable，不补建。仅将 [RootWriteLease](<src/SystemAutorun.Core/Infrastructure/RootWriteLease.cs#L9>)的原文件名提为公共常量，writer 布局和行为不变。 |
| 实现 — 镜像不是过去事实 | root intent/config/ledger 以现有 local/NOFOLLOW/read-only pin 和缓冲区持有，结束前精确复读。配置 matched_image 仅比较自声明 hash，故自己改配置再改 hash 仍不能证明 archive 上下文可信。账本须完整解析、新记录最后一条/字段相同、空前态恰一条、proposed 为 canonical 字节；即使 hash 自洽也拒坏 JSON、非 canonical 和条目冲突。declared_empty 与 missing_after_declared_previous 分开。旧原始字节未存，不能从 proposed 镜像证明旧前缀的真实过去。 |
| 实现 — 侧库关联 | [reader](<src/SystemAutorun.Windows/Startup/StartupHistoryStoreReader.cs>)增加可选完整 expected candidate，默认旧调用不变；本查询比完整 binding/Original、候选正文/nonce/序号，committed 时还比 CandidateAt、递增序号与保留 candidate 正文。缺旧 candidate 时只让本次关联 unverified，普通 reader 仍可观察 protected_committed；不补证、降级或倒推终结。此关联不是密码学父指针、返回时最新保证或原生 CAS。 |
| DATA — 新增 64 例 | [隔离测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryIntentTests.cs>)覆盖兼容字节、25 种错误元组/字段、5 种边界包、prepared/未知 native/ledger/committed 四种释放后观察、活 owner/同进程第二配置 writer 忙、缺与晚到意图、旧/坏/新版本保留、9 种账本镜像、同 context 不同 nonce/Original、缺 candidate、缺协调文件、取消与既有 ShareAll 写柄拒绝，另覆盖跨 root/actor 与配置 changed/missing/self-declared。使用实际 NTFS、CurrentUser DPAPI 及已有 R42/R43 owned 会话，不触生产 Startup，也未实测新跨进程查询或强杀/掉电。 |
| 门禁与源码核查 | NativeState 恒 unobserved；NativeWriteReady/ExecutionRequested/TransactionFinalized/MayFinalize 恒 false。未知 native 已实际移动但账本未发的用例，也不会被查询误报为未移动。源码搜索新 reader 仅有类型/构造声明，没有生产调用；旧 inventory unresolved issue、disabled CanModify=false 与旧未决拒绝均未改。 |
| 咨询与复核 | 设计期只读安全咨询不构成新代码安全认证。自证 hash 不应升级为可信的建议已落实；仅存旧 SHA 无法重建未知旧 raw bytes，故明确只报镜像。关于“双 handle 不能 pin/既有写者不受限制”的推测未读 lease；主线核 [实际共享访问](<src/SystemAutorun.Windows/Health/SshInputFileLease.cs#L57-L77>)并以已有 ShareAll 写柄实测拒绝。读仍用第二路径 handle，边界是保留 NOFOLLOW/file/parent pin，不宣称抗既有映射写、恶意同 SID 或整套回滚。 |
| ENG — 实跑 | [首轮](<docs/visual/sa-20261102/r44-focused-initial.txt>)构建 0 警告/错误、历史 **320/320**；补五例后，[最终矩阵](<docs/visual/sa-20261102/r44-matrix-final.txt>)退出 **0**，Debug 历史 **325/325、0 跳过**。Debug/Release 全方案各 **0 警告/错误**、Core 各 **482/482**；沿 R43 同一已审查隔离 filter，广域各连续两轮 **1277/1277、0 跳过**。不是未筛选全集、完整产品旅程或 HS02/HS04 通过，不抹去旧 R42 占用失败的未证实根因。 |
| VIS/FLOW | **N/A**：本片没有 UI/用户交互/业务 CLI/RPC 改动，不复用工程通过充当 G2 或真实双端旅程证据。HS01–HS06、ST03–ST08 及 GUI/CLI/监控产品旅程仍未全项验收。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r44-cleanup.txt>)相关进程、私有注册根、fixture 服务、最终临时项各 **0**；只删除当场核实绝对路径/父目录、非 reparse 且为空的共享测试父目录。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r44-baseline.txt>)一致，canonical 默认历史目录未创建，实跑源 hash 已记录。保留既有未提交工作，无提交、安装、发布或生产试写。 |
| 下一片 | 先以实际主体/known-folder 来源/当前配置与原槽/归档身份做只读原生对账，再隔离验收强杀/重启及显式终结；不能从匹配自声明 hash、candidate-only、孤立 proof 或 protected_committed 自动清意图。之后才接 fresh 请求/draft、外层 runtime/Journal 协调、批准双端流程和恢复/重建权限。 |

## 2026-11-02 — 同会话受门控 committed 发布（R43）

继续 G1-history-r1；[本片边界](<docs/architecture.md#2026-11-02--同会话受门控-committed-发布r43>)。
内部 owned 数据链现可核验历史 committed，**根事务仍 pending，不接生产请求/GUI/CLI/IPC，也不开放恢复或执行**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 同一 owner | [Root transaction](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryTransaction.cs#L168-L196>)在自身 semaphore 内，仅实际 archived_uncommitted 且账本回读标志为真时激活 commit；[session](<src/SystemAutorun.Windows/Startup/StartupHistoryCommitSession.cs>)还必须校验 active 和 ReferenceEquals。已有文件不能升级未知 native/未核验账本，不收任意 committed payload 或 callback-only 授权。 |
| 实现 — 独立发布 | 由本会话 candidate 派生 committed，checked 序号/代次、新 nonce、实际 UTC 时间且禁止早于 candidate。旧 candidate 叶保留，新固定 committed 叶用已有 CREATE_NEW/flush/持 parent NT no-replace 发布并精确回读，之后才替换认证 latest。完整旧索引原始镜像守卫防止覆盖晚到无关行；正常更新保留所有其他行。既有 stage/NT 语义不放宽 Delete 共享或使用 POSIX 标志。 |
| 实现 — 读回与 phase | [reader](<src/SystemAutorun.Windows/Startup/StartupHistoryStoreReader.cs#L20-L56>)只按认证 latest.phase 选 GUID.dpapi 或 GUID.committed.dpapi，白名单精确限制这两种名称；没有旧 committed fallback、迁移或补证。已知 index rename 成功即推进 session expected phase，每处 checkpoint 前后经 owner 重验原生/配置/意图/账本与完整 index/proof/payload，而不是 commit 后仍只接受 candidate。 |
| 实现 — 未决 | proof 已发而 index 未完成为 partial；index 已发后取消/回读变化为 uncertain，不能返回成功。保留原生事实、candidate、已发布 proof/index 和 root intent；没有逆向 move、删除已发证据、降级或自动重试。每活 session 一次有授权提交尝试，重复 owner 调用返回该次结果，不是新探测。历史结果 Committed=true 也不清意图，TransactionFinalized/NativeWriteReady/ExecutionRequested 仍 false。 |
| DATA — 新增 41 例 | [提交测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryCommitSessionTests.cs>)复用真实 NTFS/CurrentUser DPAPI/token/原生 class-10 与实际 config/intent/ledger；双 owned 来源保持绑定、序号、nonce、时间、candidate 字节与账本不变，成功后持柄删除归档被拒；Dispose 后原字节正确，旧 Restore 仍被 unresolved gate 阻止。另用 WScript.Shell 在 owned 用户来源生成真实 .lnk，重捕原叶 FileID/字节后贯穿 native→ledger→committed，目标不执行。这不是生产 Startup 或用户确认旅程。 |
| DATA — 失败矩阵 | 六阶段各 IO/合作取消；三阶段各 root intent/ledger/当前 proof 自洽重签漂移；已占/晚到 committed 槽、index reader 实际拒绝替换、无关旧行保留/晚到新行不丢失、借用他 session owner、代次耗尽、真实原槽重现、未知 native/未核验 ledger 均保留证据且不假成功。按 phase 的无 fallback 和六种白名单形状另有覆盖。index 已提交但调用 uncertain 时 reader 可见 protected_committed，仍不等于 root 已终结。 |
| ENG — 实跑 | [首轮](<docs/visual/sa-20261102/r43-focused-initial.txt>)构建 0 警告/错误、历史 **257/257**；增加四个边界后，[最终矩阵](<docs/visual/sa-20261102/r43-matrix-final.txt>)退出 **0**，历史 **261/261、0 跳过**。Debug/Release 全方案各 **0 警告/错误**、Core 各 **482/482**；沿 R42 同一已审查隔离 filter，广域各连续两轮 **1213/1213、0 跳过**。不是未筛选全集、发布验收、强杀/掉电或 HS02/HS04 全通过；本轮全过也不抹去 R42 原占用失败的未证实根因。 |
| 审查与复核 | 只读安全咨询未提出可独立复核的 critical finding，不是安全认证。保留 candidate 是刻意的对账证据，不归为应删除的临时残留；最终 checkpoint 的 proof 回读经 RequireCommitOwner→VerifyForCommit→Session.VerifyCurrent 执行，不能只看本地 VerifyIndexImage 就说没有。结果定义的三布尔位置是 ProofPublished/IndexPublished/Committed，执行/写就绪/根终结是独立恒 false 属性；源码搜索只有声明、内部 owner 调用，没有生产入口。 |
| VIS/FLOW | **N/A**：本片没有 UI、用户确认、业务 CLI/RPC 或交互变化，不用内部数据链替代批准的双端旅程。HS01–HS06、ST03–ST08 与整体产品验收保持未完成。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r43-cleanup.txt>)核对相关进程、私有注册根、fixture 服务和最终临时目录均 **0**；仅删除当场核实绝对路径/普通目录/父目录和为空的共享测试父目录。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r43-baseline.txt>)一致，实际默认历史目录未创建；最终 diff 与本轮未跟踪源码空白检查通过。保留所有既有未提交工作，没有提交、安装或发布。 |
| 下一片 | 先做 typed intent 的严格查询与显式对账/终结协议，并隔离验收真实强杀/重启边界；再补 fresh 请求/draft 和外层 runtime gate/Journal 协调。不能从释放后路径、candidate-only、孤立 proof 或 pending 账本升级历史。后续才接批准双端流程与恢复/重建权限；整体 GUI 简化、CLI parity 和监控产品旅程仍未全项验收。 |

## 2026-11-02 — Root 意图与同会话账本回读桥接（R42）

承接 G1-history-r1；[当前范围与门禁](<docs/architecture.md#2026-11-02--root-意图与同会话账本回读桥接r42>)。
只在 owned 假 Startup 映射中调用内部接口；**没有 Agent/GUI/CLI/IPC 接线、生产试写或 committed 签发**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — Root/连续租约 | [内部 transaction](<src/SystemAutorun.Windows/Startup/StartupManager.HistoryTransaction.cs>)先取得 StartupMutationLease，再取得 RootWriteLease，不持配置 root 排队等旧扫描。config 另用 local/NOFOLLOW read-only pin 持有，实际加载 archive base 并保存原始配置镜像；所有原叶/父链、历史 writer、Root/Mutation 持到异步 Dispose。未来入口 runtime gate 必须在外，不能持另一 Root lease 调此接口。 |
| 实现 — draft/意图 | 完整解析旧账本、拒已有 ID/不匹配 draft/原叶/config，并在原生前完成容量和字节预编码。typed history-disable 意图含独立 transactionId、candidate/record、旧账本缺失或原始 SHA、新账本 SHA、config SHA，flush/发布/精确回读后才调用 R41。意图为可编辑元数据，不是认证历史或 OperationJournal 回执；不在持 Root 时重入 Journal。 |
| 实现 — 账本回读 | [旧 JSON writer](<src/SystemAutorun.Windows/Startup/StartupManager.cs#L455-L490>)只加可选 action 内 guard，旧调用方不改语义；同目录独占 stage、flush、普通 File.Move，不借普通快照的 POSIX/读共享策略放宽钉扎事务。故障 Before 钩子后、Move 前再次比对 typed intent、完整旧账本镜像及 live session；原生已验证后才发布，精确字节/完整 codec/新记录 digest 回读并再核上下文。此守卫不是对不遵约写者的内核 CAS。 |
| 实现 — 未决 | 只返回 ledger_verified_uncommitted；Committed/NativeWriteReady/ExecutionRequested 仍 false。进入发布边界不等于确认 rename 执行；原生已动、账本守卫拒绝时整体仍未决。取消、IO/聚合清理异常、镜像漂移均保留意图和真实产物，不逆向 move、删已发布证据或重试。Dispose 不清意图；同一活会话 VerifyForCommit 只复核，不签发权限。 |
| DATA — 新增 34 例 | [事务测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryTransactionTests.cs>)使用真实 NTFS/token/CurrentUser DPAPI、actual config/ledger 文件和 class-10 rename。双源正例保持同对象与原字节、意图与新账本相符、Root/Mutation 互斥、config/归档写删被拒；重复调用返回既有结果，不再次 native。另在 owned 用户来源用 WScript.Shell 生成真实 .lnk，重捕其 FileID/字节后归档，目标从未执行。这仍不是生产来源或真实用户禁用/恢复旅程。 |
| DATA — 失败/兼容 | 七处 IO 故障、四处取消、意图/账本发布前后故障、清理及双故障 Aggregate、native 前后/guard 后的外部账本替换、最终 intent/ledger 漂移均拒绝或保留 unverified_result。已有或晚到 legacy 意图逐字节保留、不覆盖；旧同 ID 不补证，非法/513 条结果在 candidate/意图/native 前拒绝，合法不相关旧记录精确保留。缺 committed 时保留 pending 意图，Dispose 后直接旧 Restore 仍被现有 unresolved gate 拒绝。没有新增 disposition、迁移或遗失原备份的重建。 |
| ENG — 首次失败如实保留 | [初跑](<docs/visual/sa-20261102/r42-focused-initial.txt>)构建 0 警告/错误，历史定向 **212/214**；当时新增 28 例均过，两项既有 root/state 目录替换测试在 Directory.Move 遭占用，未捕获占用者，根因**未证实**。检查释放路径和独立夹具后，没有改断言、加删除/目录 rename 重试或声称修复了该初次占用。[定向复验](<docs/visual/sa-20261102/r42-focused-final.txt>)相关 4 case 连续 10 次均过，扩展后历史 **218/218**；它们不抹去首失败，也不证明没有系统/测试时序波动。 |
| ENG — 最终矩阵 | 补清理/聚合故障后，[最终矩阵](<docs/visual/sa-20261102/r42-matrix-final.txt>)退出 **0**：历史 **220/220、0 跳过**；Debug/Release 全方案各 **0 警告/错误**、Core 各 **482/482**，沿 R41 同一已审查隔离 filter，广域各连续两轮 **1172/1172、0 跳过**。不是未筛选全集合、安装/发布、无波动根因证明或 HS02/HS04 全通过。 |
| 审查与事实核对 | 只读安全咨询提出 legacy 意图归属、发布边界和锁排队问题；按源码与真实测试复核。归档实际位于 record.BackupPath 的既有 backup/startup/GUID.lnk，不在 protected 历史库，不能照抄“永不落到备份槽”而扩 Core disposition。旧意图有前置和 action 内双门禁，不无条件覆盖。Root 不是 legacy writer 的互斥来源，Mutation 才是；本片将它置于 Root 外，并显式 pin YAML。R41 仍只核 candidate，不假称已支持 committed。 |
| VIS/FLOW | **N/A**：没有用户界面、确认、业务 CLI/RPC 或交互变更。内部真实 .lnk/元数据写入不代替双端用户旅程，HS01–HS06/ST03–ST08 保持未完成。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r42-cleanup.txt>)确认相关进程、私有测试注册根、临时映像服务和临时目录均 **0**；只删除核实绝对路径/父目录/非重解析/为空的共享测试父目录。默认生产 history 目录仍不存在，三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r42-baseline.txt>)一致，diff check 通过。既有未提交工作保留；未提交、安装或发布。 |
| 未完成/下一片 | 仍须真正 fresh request/draft 生产器、同会话可信 committed 发布与回读、typed intent 查询/崩溃显式对账及安全清理，再连接批准的双端确认/未知态。未测强杀/掉电、SYSTEM/不同账户或重启后可见性；不能凭 pending 账本或释放后路径观察提升历史权限。整体 UI/CLI parity、服务监控产品验收、安装发布和总体目标仍未完成。 |

## 2026-11-02 — 连续候选会话与同句柄同卷归档（R41）

沿 G1-history-r1 接续；[范围与门禁](<docs/architecture.md#2026-11-02--连续候选会话与持柄同卷归档原语r41>)。
这里只实测 owned 假 Startup 目录中的文件移动；**没有生产 Startup、GUI/CLI/IPC、intent/账本、committed 或恢复接线**。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 连续会话 | [publisher](<src/SystemAutorun.Windows/Startup/StartupHistoryCandidatePublisher.cs>)保留原 Publish 行为，新增 OpenSession 只在候选发布回读成功时转移同一个 writer 租约；失败释放资源且不转移会话。[session](<src/SystemAutorun.Windows/Startup/StartupHistoryCandidateSession.cs>)保持 store/root、live capture 的原叶/来源父链与 archive/base 父链到 Dispose，gate 串行、每会话仅一次原生尝试，重复调用不重试。首个入口取消不接管输入，第一次实际尝试即使被拒也接管。 |
| 实现 — 绑定 | 每次核验保留 R39 context reader，并持两个只读叶回读独立 index/proof，认证后逐字段比对本会话完整 expected candidate、nonce/序号/phase/binding/原叶事实/正文时间/密文摘要。再比对 live capture 的 actual actor、scope/kind/known-folder/source SID、来源目录 ID/case、name 与 FileID/长度/单链接/SHA。archive base 必须由实时配置函数传入，规范路径和实际目录 ID/case/volume 原生前后核对；不从历史路径自己选配置。 |
| 实现 — 同柄原语 | [archive lease](<src/SystemAutorun.Windows/Startup/StartupShortcutArchiveLease.cs>)不创建 archive，不做跨卷 copy/delete、不解析/执行。原叶继续 R37 READ+DELETE/share READ，archive 父才加 ADD_FILE；NT legacy class 10 相对 held parent、固定 GUID.lnk、replace=false。早期缺失观察不是 CAS，晚到碰撞由内核拒绝。核后原持柄精确字节/FileID/长度/链接、archive 槽同对象和 origin 槽缺失，重核 scope/root/config/当前候选。唯有临时观测 lookup share ALL 以兼容自有 DELETE 句柄，不放宽原叶或 R39 reader 共享。 |
| 实现 — 失败事实 | 只有完整核验返回 archived_uncommitted；NativeWriteAttempted 与确认 NativeWritePerformed 分开，Committed/NativeWriteReady/ExecutionRequested 为 false。进入 NT 边界后失败是 unverified_result，不能仅因 API 报错说一定未动；返回成功后取消/回读失败/配置漂移/原槽重现保留候选和归档，不逆向 move、删新原项、回滚或自动重试。结果不构成恢复/重建权限。 |
| DATA — 真实边界 | [新增会话测试](<tests/SystemAutorun.IntegrationTests/StartupHistoryCandidateSessionTests.cs>)使用 current token、CurrentUser DPAPI、NTFS、真实持柄 rename。双源是 owned native 映射，输入为明确三字节合成 .lnk，非真实可解析快捷方式。两 root 实例同物理 store 互斥、会话期间归档叶写/删和来源目录/archive base 改名真实被拒，Dispose 后可独占打开，原字节保持、来源/归档 FileID 连续核验通过。不是另一独立进程、真实系统账户或用户旅程证明。 |
| DATA — 失败矩阵 | 四阶段分别注入 IO 异常/合作取消；早期目标已占和 preflight 后内核期碰撞均不覆盖。原槽重现保留新原项与旧归档，配置后漂移保留已移动事实；source ID/hash/FileID、archive ID/case、当前配置、来源观测不可用、已释放输入、真实同用户 impersonation、store mapping 漂移、missing/reparse archive 在 NT 前拒绝。没有自动补目录、old-record 补证、ledger、committed 或原生逆操作。 |
| DATA — 精确会话漂移 | 审查后补 nonce、sequence、original SHA 和正文时间四种真实受保护文件替换：两份新包相互自洽，R39 context reader 仍显示 candidate；本会话须拒绝 NT，源未动。不以重加密就声称真实签发，也不将这些受控同 SID 注入当成恶意同 SID/全库回滚防护。 |
| ENG — 纠错/基线 | [首编译](<docs/visual/sa-20261102/r41-focused-initial.txt>)发现测试缺显式 System.IO；[第二次编译](<docs/visual/sa-20261102/r41-focused-corrected.txt>)发现混合型 backup record 的 nullable 测试路径，固定 .lnk 夹具显式非空。未跑到测试，不冒充运行失败/通过。[首次运行](<docs/visual/sa-20261102/r41-focused-runtime.txt>) **177/177**；补身份/互斥/目录用例后[首矩阵](<docs/visual/sa-20261102/r41-matrix-final.txt>) **182/182**，双配置方案各 0 警告/错误，Core 各 482/482，广域各连续两轮 1134/1134。 |
| ENG — 加强后矩阵 | [精确候选最终矩阵](<docs/visual/sa-20261102/r41-matrix-exact-final.txt>)退出 **0**：新增共 **31** 例会话测试，最终历史定向 **186/186、0 跳过**。Debug/Release 全方案各 **0 警告/错误**、Core 各 **482/482**；同 R40 已审查隔离 filter 的广域各连续两轮 **1138/1138、0 跳过**。不是未筛选全集合、真实原生双端旅程、安装或产品发布通过。 |
| 审查/未验证 | 只读安全咨询复核了单次尝试、同柄/不覆盖及诚实失败，但“没有 store 回读”并不符合 session.VerifyCurrent 的源码；实际加强的是 context-only 到 exact candidate。共同备份布局、legacy restore/新历史槽归属、真实 intent 和会话内 ledger/committed 是生产接线硬门禁，不因没有生产 caller 而把未完成事项算通过。未强杀/断电、SYSTEM/不同账户、旧 restore 互操作、跨进程全旅程；未新增命名空间/迁移或自动回滚。 |
| VIS/FLOW | **N/A**：内部能力，无 GUI/CLI/IPC、确认或布局变化；owned FS 移动不算启动项业务完整禁用/恢复旅程，HS01–HS06/ST03–ST08 保持未完成。 |
| 清理 | [加强后最终审计](<docs/visual/sa-20261102/r41-cleanup.txt>)确认相关进程、私有测试注册根、测试目录和临时映像服务均 **0**；只删除已核对绝对路径/非重解析/为空的共享父目录。默认生产 history 目录不存在；三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r41-baseline.txt>)一致，git diff --check 通过。保留原工作区未提交改动，未提交、安装或发布。 |
| 未完成/下一片 | 接真正 RootWriteLease/intent 生命周期、fresh record 与真实 config/ledger 规范摘要，Native 后同会话发布并回读账本、门控 committed，再做未知结果查询/崩溃对账；在这些门禁实现前，不连接新业务写或让旧无凭据记录进入自动恢复/重建。整体 UI/CLI parity、安装/发布和最终产品目标未完成。 |

## 2026-11-02 — 私有侧库候选发布与部分结果接续（R40）

承接 G1-history-r1；[当前事实](<docs/architecture.md#2026-11-02--私有侧库候选发布与可接续部分结果r40>)。
本片只发布合成 candidate，**没有 committed 入口、原生签发/禁用、账本或双端业务接线**，不授予恢复资格。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 私有创建/互斥 | [写租约](<src/SystemAutorun.Windows/Startup/StartupHistoryWriteLease.cs>) 在 actual primary/None、实际 root ID/case 和 root 外祖先校验后，仅创建最多两层缺目录，不制造 profile/drive。新目录/锁/stage 用 native security attributes 原子赋 actor owner/protected actor+SYSTEM+Admin DACL，不修现有 ACL；现有父容器 owner 仅三主体、拒其他有效写授予。固定零长度/单链接普通锁叶 OPEN_ALWAYS/share=0，持有到回读完成、释放不删；同物理侧库两 root 互斥，不冒充全原生事务锁或不同 profile 间唯一权威。 |
| 实现 — 文件发布 | [candidate-only publisher](<src/SystemAutorun.Windows/Startup/StartupHistoryCandidatePublisher.cs>) 在新空库先发布受保护空索引基线，再发布包及追加索引；缺索引且非空明确 damaged，不扫账本/孤儿重建权威。CREATE_NEW 私有 stage 限长、分块合作取消、Flush(true) 和同句柄逐字节回读；NT class 10 相对 held parent rename，包不 replace、仅索引可 replace，无 POSIX/ignore-readonly。关闭写句柄后核对精确索引密文并走 R39 reader 验 candidate；未发布 stage 仅通过自有 DELETE 句柄 class 13 清理，已发布状态不盲删/回滚。 |
| 实现 — 可接续结果 | rejected/partial/uncertain/conflict/damaged 分开；已知包在、索引未发布为 partial，而不是说什么也没发生或未知原生结果。显式重提同一完整 candidate 时，须有有效独立索引，protected 包解封逐字段完全一致才收养原密文，仅补索引；已索引同候选只读确认，不增代次。不同 nonce/序号/phase/绑定/载荷、现有 native 槽或容量冲突不覆盖；索引宣称包但缺包是 damaged，不重生包。三项原生写/执行标记始终 false。 |
| DATA — 真实 NTFS/Token/DPAPI | [新增 39 例](<tests/SystemAutorun.IntegrationTests/StartupHistoryCandidatePublisherTests.cs>) 用 closed owned 父容器、真实 SID/NTFS/CurrentUser DPAPI/安全描述符/文件 IO，双源合成候选成功，Root/DACL/owner 实际复核；两 root 实例争同一文件锁真实互斥。读者持 index 时 NT rename 真实返回 Win32 5/32，不改旧索引，留下同一候选包为 partial；读者释放后显式同候选接续成功、包字节不变。不是不同会话/账户或另一独立进程部署矩阵。 |
| DATA — 故障/防修复 | 九处阶段注入 IO 异常，覆盖 store/index/bootstrap、包/索引 flush 和发布及最终回读；真实取消在三处注入。已发布证据保留、未发布 stage 清理、锁可重开，同候选显式续接成功、不重放 native。缺索引非空、缺 proof、错 latest hash/nonce/committed、冲突包拒修；128 项/代次溢出在新 proof 前拒绝。锁数据非空/硬链接/重解析、unsafe root/parent、root 内位置、mapping 和 impersonation 拒绝；unsafe owned parent 的原二进制 DACL 在 finally 显式恢复并核对 SDDL。 |
| ENG — 纠错 | [首编译](<docs/visual/sa-20261102/r40-focused.txt>)发现 NTSTATUS uint/int ABI 声明不一致，改为与原 Rtl mapper 一致的 int。[首测试](<docs/visual/sa-20261102/r40-focused-corrected.txt>) **127/144**：系统 Temp 对额外主体开放写权限，父门禁因此在创建前拒绝，正例/故障夹具未达预定阶段；没有因此放宽门禁或改 Temp 权限，改为夹具自己新建的 closed parent，并补显式 unsafe parent 拒绝例。[接续版](<docs/visual/sa-20261102/r40-focused-resumable.txt>) **146/146**；原失败日志保留，不作已通过或产品路径故障。 |
| ENG — 最终矩阵 | [最终矩阵](<docs/visual/sa-20261102/r40-matrix-final.txt>)退出 **0**：最终定向 **155/155、0 跳过**；Debug/Release 全方案各 **0 警告/错误**、Core 各 **482/482**、同 R39 已审查隔离 filter 的广域各连续两轮 **1107/1107、0 跳过**。不是未筛选全集合、真实双端原生旅程、安装或产品发布通过。 |
| 审查/未验证 | 只读安全咨询提出孤儿/reader 共享冲突、目录耐久性和部署身份门禁；按源码与真实 IO 取证补基线/同候选收养/partial/explicit damaged，不照抄“只能删除文件恢复”。current SID owner 在创建即指定，目录仅父容器允许 SYSTEM/Admin owner，state root/叶仍要求 actor；当前用户实际创建回读通过，不据此宣称 SYSTEM×用户交叉已验。无父目录 fsync/断电排序证明，Flush(true)+rename+回读不等于多文件 durable transaction；真正进程崩溃/掉电及显式 damaged 对账未测。 |
| VIS/FLOW | **N/A**：无 GUI/CLI/IPC、布局、确认或用户旅程变更；没有接 prepare 或新原生写，不能替代 HS01–HS06。 |
| 清理 | [最终审计](<docs/visual/sa-20261102/r40-cleanup.txt>)确认相关进程、私有测试注册根、临时目录与临时映像服务均 **0**；仅删除已核对绝对路径/非重解析/为空的共享测试父目录。默认生产 history 目录不存在；三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r40-baseline.txt>)一致，git diff --check 通过。保留既有未提交工作；未提交、安装或发布。 |
| 未完成/下一片 | 真实业务 producer、跨原生/账本/committed 的连续事务会话与全局槽租约、旧无凭据只读接线、孤儿审计回收和崩溃对账未完成；不能把私有 candidate 发布后释放的锁当持续原生租约。下一片接 R37 持柄同卷结果与事务会话，再以账本回读事实门控 committed。整体产品目标、ST03–ST08/HS01–HS06 双端真实旅程、完整 UI parity/安装/发布仍未完成。 |

## 2026-11-02 — root 外最新索引与只读历史侧库（R39）

本片按 G1-history-r1 接续；[当前数据边界](<docs/architecture.md#2026-11-02--root-外最新状态索引与只读侧库r39>)。
这里只实现真实独立文件读取，不发布生产历史、不重建缺证、不开放原生写；HS01–HS06 的完整双端旅程仍未完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 独立索引 | [v1 index](<src/SystemAutorun.Windows/Startup/StartupHistoryIndexFormat.cs>) 是当前 actor 下以 record ID 寻址的完整 binding+latest 集合，带 generation/nonce，限 128 项/256KiB/深度 10。拒重复 ID、跨 root 同物理 native name 槽、错误 actor/nonce/序号与严格 JSON 形状，编码流先限字节。读出集合不可改；generation 不是外部防回滚锚点。 |
| 实现 — 域/布局 | [索引保护 codec](<src/SystemAutorun.Windows/Startup/StartupHistoryIndexProtection.cs>) 在原 CurrentUser native wrapper 中选择固定独立 magic/entropy，密文限 512KiB，record 仍为原 128KiB；两种包不能代用。[reader](<src/SystemAutorun.Windows/Startup/StartupHistoryStoreReader.cs>) 从 actual primary/None 的 LocalApplicationData/固定后缀派生位置，DoNotVerify、不创建、不修复、不借别的 profile。只打开固定 index 和 record GUID 派生叶；不读 ledger/旧 backup path，不从待检 proof 自证 latest。 |
| 实现 — 真实读取/ACL | [持柄 reader](<src/SystemAutorun.Windows/Startup/StartupHistoryReadLease.cs>) 复用本地 NOFOLLOW 祖先 pin、父相对打开与 FILE_ID_INFO/case，READ-only、share READ、不预留 DELETE；两叶到最终校验均持有，单链接/普通文件/分配前限长、dispose 清零。词法/物理 root 内侧库拒绝，真实 project root ID/case 复核。GetSecurityInfo 从同句柄取 owner/DACL；state root 必须 protected/current owner，非 actor/SYSTEM/Admin 的写/删/child-delete/WRITE_DAC/OWNER/generic 写授予、null/不支持 DACL 拒绝且不修复。只是保守 ACL 规则与读期间 pin，不宣称所有祖先 ACL/静态整库防回滚。 |
| DATA — 真实 IO | [新增 49 例](<tests/SystemAutorun.IntegrationTests/StartupHistoryStoreTests.cs>) 用真实 sibling owned 目录、NTFS、token、CurrentUser DPAPI、owner/DACL 与文件 IO：双源×双阶段读回，两条不同槽并存；同路径 project root 替换、目录/叶重解析、硬链接、超限、独占占用、不安全/非 protected ACL 拒绝。目录和双叶保持到 seal，write/delete/move 真实被拒；叶 DELETE + 父 DELETE_CHILD 双路 deny 下只读仍可读，finally 显式恢复两份原二进制 DACL 并核对。缺失不创建/补证，取消后两叶可独占写打开，映射/ACL 漂移拒绝，缓冲释放清零。 |
| DATA — 合成业务/限制 | candidate/committed/index 内容均由测试独立合成，**没有真实禁用、归档、账本或 committed 发布**。新版 index 指向新包时旧包拒绝，域混用/篡改/严格格式/128 项与编码超限通过；整套旧 index+包回放明确仍可读 protected_committed，三标记 false，mutation 零调用。这是 G1 已排除的全库回滚/同 SID 伪造边界，不把“解封自洽”称为应用签发。来源/归档的 current context 仍由调用方提供；reader 不是完整持柄原生快照、prepare token 或原生 CAS。 |
| ENG | [后端首构建](<docs/visual/sa-20261102/r39-build-initial.txt>) 0 警告/错误；[首版测试构建](<docs/visual/sa-20261102/r39-build-tests.txt>) 暴露仅测试 FileSystemSecurity→FileSecurity 缺显式类型，已修正并保留原失败。随后[定向](<docs/visual/sa-20261102/r39-focused.txt>) **111/111**。补齐容量、root/目录替换、整库回滚与缓冲/ACL 漂移后，[最终矩阵](<docs/visual/sa-20261102/r39-matrix-final.txt>)定向 **116/116、0 跳过**；Debug/Release 全方案各 **0 警告/错误**，Core 各 **482/482**，同 R38 已审查隔离 filter 的广域集成各连续两轮 **1068/1068、0 跳过**。不是未筛选全集合、真实 UI 旅程或产品发布通过。 |
| 审查与身份未测 | 只读安全咨询读取的是 R38/实现中版本，提出多记录、权限、叶持有和部署身份门禁；本片按实际文件/用例复核，不照抄“没有 token guard/多记录结构/ACL”作当前事实。原路径 File.Move 确未接 R37 租约，但新 reader 没有接它或赋写权。真实 SYSTEM/交互用户/不同管理员的交叉执行未测，只能报当前账户与真实同用户 impersonation 拒绝，不能用字段替身冒充换账户验收；Common Startup 的空 SourceUserSid 与 actor 的实际 SID 分别定义在原合同。 |
| VIS/FLOW | **N/A**：内部只读数据能力，无 GUI/CLI/IPC、确认或布局变更；未接 prepare、历史 UI 或新原生写。 |
| 清理 | [审计](<docs/visual/sa-20261102/r39-cleanup.txt>)确认相关进程、私有测试注册根、临时目录与临时映像服务均 **0**；仅删除已核对绝对路径/非重解析/为空的共享测试父目录。默认生产 history 目录不存在，三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r39-baseline.txt>)一致，diff check 通过。未提交/发布，保留原工作区改动。 |
| 未完成/下一片 | 私有侧库的安全创建、全局协作租约、candidate/最新索引发布回读和故障边界尚未实现；随后仍须同句柄原生核验、账本发布回读、committed 发布、崩溃对账与旧无凭据只读兼容接入。三项写/执行标记保持 false，不把 R39 的文件读取当作完整 HS02/HS04 或整体产品完成。 |

## 2026-11-02 — 历史凭据格式与当前账户解封（R38）

沿已批准 G1-history-r1 推进后端基础；[当前事实与限制](<docs/architecture.md#2026-11-02--历史凭据有界格式与当前账户解封r38>)。
本片没有独立侧库文件 IO、可信原生生产者、旧记录补证或业务原生写，不能视为 HS01–HS06 完整双端验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 有界格式 | [内部格式/匹配器](<src/SystemAutorun.Windows/Startup/StartupHistoryFormat.cs>) v1 限新同卷 disable 的 candidate/committed。绑定 actor、root/source/archive base/archive 子目录的路径/物理身份/case、记录 ID/名称/独立规范摘要、原叶事实、nonce/序号/阶段时间。严格 .NET 10 JSON 拒重复/未知/遗漏/null、错误 ID、跨卷、矛盾阶段；64KiB JSON、8 层、4096 字符路径、251 字符 basename。摘要归一 UTC 瞬间与有效 disposition，不替换旧 IPC 摘要，不赋予真实性。 |
| 实现 — Windows 保护 | [原生 CurrentUser DPAPI codec](<src/SystemAutorun.Windows/Startup/StartupHistoryProtection.cs>) 使用固定公开协议 entropy、内部 magic/正文 SHA256/JSON，只设 UI_FORBIDDEN、无 LOCAL_MACHINE；128KiB 密文先限长，native 结果在托管复制前限长。保护器检查实际 primary/None SID 前后一致，不接受 impersonation；私有原始字节/native 缓冲清零并释放，取消为同步合作检查，不限制 OS 内部分配、不宣称清零 CLR 字符串。没有安装包、建立应用密钥或生产凭据目录。 |
| DATA — 真实部分 | [67 个最终用例](<tests/SystemAutorun.IntegrationTests/StartupHistoryFormatTests.cs>) 使用 owned 根/固定源映射与真实 Windows token、NTFS 卷/文件/目录 ID、CurrentUser DPAPI。双源×双阶段加解封往返通过；真实 root/source/archive 同路径目录替换后物理身份不同，匹配拒绝。真实本用户 RunImpersonated 下保护/解封拒绝，退出后恢复。未换实际账户、未碰真实 Startup，原叶只是隔离的 3 字节默认流，不是 COM/可执行快捷方式验收。 |
| DATA — 受控部分 | 阶段时间、伪造字段、来源/SID/case/ID/摘要/长度漂移、序号/nonce/阶段回退与缺最新值为受控输入。篡改/截断/追加密文均不能匹配独立预期 SHA256；前三种也直接检验 DPAPI/内帧拒绝。精确 64KiB JSON 允许，超限 JSON/密文及膨胀编码拒绝；等价账本重写保留关联，业务字段变化改变摘要。最新状态为明确替身，candidate/committed 只是合成载荷，**不是实际禁用、发布成功或防整侧库回滚证据**。所有检查三项写/执行标记 false，mutation 零调用、无账本/intent。 |
| ENG — 最终矩阵 | [初始构建](<docs/visual/sa-20261102/r38-build-initial.txt>) 0 警告/错误；[首批定向](<docs/visual/sa-20261102/r38-focused.txt>) **60/60**。[最终双配置隔离矩阵](<docs/visual/sa-20261102/r38-matrix-final.txt>)：补齐边界后定向 **67/67、0 跳过**；Debug/Release 全方案各 **0 警告/错误**，Core 各 **482/482**，R37 同一已审查 filter 的广域集成各连续两轮 **1019/1019、0 跳过**。不是未筛选全量集成、UI 验收或完整历史提交链验收。 |
| ENG — 中断与清理 | 首矩阵误用了无筛选全集合，在 Debug 集成运行中主动中止，**不是完整通过，也不作产品失败**；保留原日志。回到 R37 已审查隔离 filter 后重跑。中止后确认相关进程为零，对基线后新增的 10 个独占根与 1 个共享测试父目录逐一核对绝对路径/父目录/非重解析/后代范围、所有文件独占读可开，再仅删除这些已核实残留；见[中断审计](<docs/visual/sa-20261102/r38-interruption-audit.txt>)。[最终清理](<docs/visual/sa-20261102/r38-cleanup.txt>)确认相关进程、私有测试注册根、临时目录与临时映像服务均 **0**；只再删除已核实空的共享测试父目录。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r38-baseline.txt>)一致。新增文档末尾空行触发 diff check 后已去除，最终检查通过，仅有既有换行格式警告；保留原工作区改动，未提交/发布。 |
| API/审查 | [严格 JSON 契约](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries#serialization)、[DPAPI protect](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptprotectdata) 与 [unprotect](https://learn.microsoft.com/en-us/windows/win32/api/dpapi/nf-dpapi-cryptunprotectdata) 已核对。只读安全咨询提出最新独立锚点、语义摘要、阶段表和值域/缓冲边界；本片明确锚点未实现，不把可自行构造的 latest 参数当成已认证权威。路径物理 ID 原本就在本片计划中，并非按路径字符串认证；咨询不是通过证明。 |
| VIS/FLOW | **N/A**：纯后端内部 codec/匹配，不改变用户界面、确认、RPC 或 CLI 输出；未接 GUI/CLI 的历史验证或新原生动作。 |
| 未完成/下一片 | root 外独立最新状态侧库的安全位置/实际读取/发布一致性、同句柄原生生产者、账本发布回读、committed 发布核验与崩溃对账尚未完成。Protect 是加密而非应用签发；protected_committed 仍为 producer_not_connected 且不可写。旧无凭据只读兼容完整接入、HS01–HS06/ST03–ST08 双端真实旅程、整体 parity/安装/发布均不在本片通过范围。 |

## 2026-11-02 — 当前原链接叶子/字节租约（R37）

承接已批准 G1-startup-r2 原字节保真与写前身份基础；[当前事实](<docs/architecture.md#2026-11-02--当前原链接的叶子字节租约r37>)。
没有实现历史凭据、producer/commit、旧记录迁移、跨卷移动或新增业务原生写；UI/RPC/schema/账本不变。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 | [capture](<src/SystemAutorun.Windows/Startup/StartupShortcutCapture.cs>) 从固定源和 basename 派生输入；现有普通、单链接、≤64KiB 默认流 .lnk 持有 READ+DELETE 句柄，只共享 READ，保留原字节/文件身份/目录身份/长度/link count/case/SHA256。DELETE 仅预留，不实际调用移动/删除；scope 在叶子持有下复读并匹配，字节再次读取比较。敏感副本不序列化/落盘/日志，dispose 清零内部缓冲与释放句柄。 |
| DATA — 原生正例 | [新增 28 例](<tests/SystemAutorun.IntegrationTests/StartupShortcutCaptureTests.cs>) 使用 owned 固定源映射和真实 token/NTFS：双源的 0/3/65536 字节逐字节一致，hash 正确；真实叶子与 scope 的目录/卷/ID/case 一致，副本可改而内部不变。保持租约时普通 write/move/delete、目录 rename 均被拒，释放后能写/移动；同路径同字节替换有不同文件 ID。无 COM 解析、目标探测或执行。 |
| DATA — 拒绝/身份 | 真实缺叶/目录占位/重解析/硬链接/超大/独占占用拒绝；重解析目标原字节不变。真实 ReadData deny 与 DELETE 双授权路由 deny（叶 DELETE + 父 DELETE_CHILD）均拒绝，并在 finally 恢复两份原二进制 DACL、核对两份 SDDL。真实 impersonation 下重证拒绝，退出后恢复；case-sensitive 精确名称可捕获，大小写不同的另一拼写返回原生 NOT_FOUND，不能误认。映射漂移、取消/异常 seal 为受控注入，均释放原叶，未发布任何凭据/账本。 |
| ENG — 首轮纠错 | [原始 26/28 日志](<docs/visual/sa-20261102/r37-focused.txt>) 保留：重解析以 OPEN_REPARSE_POINT 打开后由属性拒绝，实际是 InvalidOperationException，不是用例假定的 Win32Exception；本机 NTFS 非空目录切 case flag 返回 ERROR_DIR_NOT_EMPTY，不能冒充“已改变并检测”。修正明确异常分类，并在空目录先设 sensitive，验证实际精确/错误拼写；没有跳过、吞错误或假称租约中动态 case 漂移已实测。 |
| ENG — 聚焦/矩阵 | [最终聚焦](<docs/visual/sa-20261102/r37-focused-final.txt>) capture 28 + 原 scope 33 = **61/61、0 跳过**。[最终双配置矩阵](<docs/visual/sa-20261102/r37-matrix-final.txt>)：Debug/Release 全方案各 **0 警告/错误**，Core 各 **482/482**，日志所列过滤广域各连续两轮 **952/952、0 跳过**；不是全量集成/安装/发布。 |
| ENG — 取消清理竞态 | [原矩阵](<docs/visual/sa-20261102/r37-matrix.txt>) Debug 第二轮 **951/952**，旧 GUI 取消用例在回收 fixture 时遇仍占用的 root lock。根因是 caller 完成/模拟 getter release 不等于整个 Agent workflow 完成，后续 workspace 复读仍可能持锁。[最小测试修复](<tests/SystemAutorun.IntegrationTests/StartupGuiControlPlaneTests.cs#L184-L197>) 在 finally 释放 getter 后，按有界业务闸等待真实 startup-list available，再回收；不重试删除、不固定睡眠、不改产品取消语义。修后该例 **10 次连续通过**，再跑上述双配置广域矩阵；保留原失败日志，不靠重跑掩盖。 |
| API 复核 | [FileStream 缓冲契约](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestreamoptions.buffersize?view=net-10.0) 规定 bufferSize=1 禁托管缓冲，capture 复读不复用托管旧缓存；这不等于绕过内核缓存或硬件/映射写 CAS。 |
| 审查边界 | 设计咨询仅只读，叶子连续性、candidate/committed 与兼容风险为线索，主审核对现有路径 Move/账本事务后限定本片；未把建议当通过证明。安全咨询把 `==None ? SID : null` 方向读反；实际 None→SID、非 None→null，[R36 原始正负实测](<docs/visual/sa-20261102/r36-focused.txt>) 与本片真实复测均已有，不采纳“没有实测/非 None 放行”的错误日志结论。 |
| VIS/FLOW | **N/A**：仅后端内部输入原语，无用户可见行为变更，不冒充 ST03/ST04/完整原生旅程。用户随后明确批准 [G1-history-r1](<docs/architecture.md#2026-11-02--历史凭据首版兼容范围g1-history-r1已批准>) 分阶段方案：新同卷 .lnk 历史链、旧无凭据 .lnk 记录保留只读且不补证、跨卷/迁移另批；root 外当前账户保护、原生核验与账本回读后 committed，丢证/换账户/上下文变化拒绝自动恢复重建。仅范围批准，HS01–HS06 未执行，兼容实现尚未落地；不改变本片 G2/VIS 不适用或误报原生写已开。 |
| 清理/未完成 | [最终清理审计](<docs/visual/sa-20261102/r37-cleanup.txt>) 确认首轮失败仅遗一 owned root 的空 lock，核对绝对路径/非重解析/唯一空子文件并独占打开成功后才删除；另删已核实空共享测试父目录。最终相关项目/测试进程、私有测试注册表键、SystemAutorun 临时目录均 **0**。三份生产配置 SHA256 与[测试前](<docs/visual/sa-20261102/r37-baseline.txt>)一致，git diff --check 通过，保留全部既有未提交改动。当前 input 不认证过去 disable，不是 native CAS，未签 candidate/committed，未创建生产凭据目录/密钥；原生三标记 false。历史凭据与旧记录约束的实现、同句柄原生生产者、提交/崩溃对账仍未完成；跨卷/迁移不纳入已批准首版。 |

## 2026-11-02 — 当前 Startup 来源 scope 只读实测（R36）

沿 G1-startup-r2 的准备前置基础推进；[当前事实](<docs/architecture.md#2026-11-02--当前-startup-来源-scope-的只读身份r36>)。
本片只增加后端当前来源观察，不扩 GUI/CLI/RPC，不改旧 schema 1/2 或账本，不开放原生写。

| 类别 | 实际结果与边界 |
| --- | --- |
| 契约/实现 | [Manager 入口](<src/SystemAutorun.Windows/Startup/StartupManager.SourceScope.cs>) 使用 [固定源 probe](<src/SystemAutorun.Windows/Startup/StartupSourceScopeProbe.cs>) 和 [原生目录观察](<src/SystemAutorun.Windows/Startup/StartupNativeSlotProbe.cs>)；真实 token SID、known-folder 类型/目录、FILE_ID_INFO、case mode 前后复核。用户来源的 SourceUserSid 绑定用户；公共来源 SourceUserSid=null，ObserverSid 不是 ACL owner。所有写/执行标记 false，无历史 trusted 字段、摘要或迁移。 |
| DATA — 真实能力 | [新增用例](<tests/SystemAutorun.IntegrationTests/StartupSourceScopeTests.cs>) 使用 owned 固定源映射、真实 Windows SID/64 位卷号/128 位目录 ID；同路径目录替换被新的 ID 识别，观察期间目录 rename 被 pin 拒绝、返回后可移动。锁死损坏 .lnk 叶子不影响 scope，未读链接正文/target。真实 DACL deny、独占目录句柄、缺目录、重解析和 case mode 变化均为 unknown；缺目录不创建。DACL 在 finally 从原二进制恢复并核对 SDDL。 |
| DATA — 身份/漂移 | 真实本用户 token 的 [RunImpersonated](https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.windowsidentity.runimpersonated?view=net-10.0) 进入后捕获为 unknown，退出后恢复 observed；不需密码、不更换账户、不碰真实 Startup。SID/固定映射及 case-only 路径漂移用受控 getter/callback 模拟，明确不是实际跨用户/重定向或 UAC 验收；错误/网络/设备/令牌/相对路径不成为 observed，也不跟随变更后的目录。 |
| ENG — 不授权/兼容 | 伪造旧记录与当前 scope 相符且 archive/original 双 missing，原生动作仍不能 reviewable，三标记 false、mutation 零调用、账本字节不变。scope 本身不读账本/未决 intent，所以损坏账本下仍可 observed，但双槽/准备继续拒绝；scope 绝不是许可。原 schema 1 序列化不增 scope；旧 schema 1/2、Agent/typed/CLI 兼容通过广域用例另验。 |
| ENG — 聚焦/矩阵 | [聚焦原始日志](<docs/visual/sa-20261102/r36-focused.txt>) **33/33、0 跳过**。[双配置矩阵](<docs/visual/sa-20261102/r36-matrix.txt>)：Debug/Release 全方案各 **0 警告/错误**，Core 各 **482/482**，所列过滤广域各连续两轮 **924/924、0 跳过**；不是全量集成、安装或发布验收。 |
| 独立审查/主审 | 设计与安全咨询均只读、未执行测试；建议经主代理逐项复核。[.NET 定义](https://learn.microsoft.com/en-us/dotnet/api/system.security.principal.tokenimpersonationlevel?view=net-10.0) 明确 None=0、Anonymous=1，不采纳咨询将两者等同或推断生产永不 observed 的误报；真实正负例已反证。保留事实：目录 pin 不锁子项内容，取消合作而非 OS 硬中断；非法 source 以参数错误拒绝，不伪造 unknown/成功。没有把咨询当成通过证明。 |
| VIS/FLOW | **N/A**：纯后端原语，未改界面、输入/确认旅程或既有 IPC 输出；不冒充完整原生准备/重建、ST03–ST08、整体 parity 或发布验收。 |
| 清理 — 已核实 | 与 [测试前生产配置 SHA256](<docs/visual/sa-20261102/r36-baseline.txt>) 对比，[最终核对](<docs/visual/sa-20261102/r36-cleanup.txt>) 确认配置/覆盖/GUI 偏好均未变；相关项目/测试进程、私有 Mutation/Registration 测试注册根、SystemAutorun 临时目录均 **0**。仅删除完整绝对路径/父目录/非重解析/空内容均已核实的测试父目录；fixture 各自释放 owned 根与私有键，DACL 原样恢复。diff 无空白错误，既有未提交/scratch 工作保留；未改真实 Startup、未执行目标、未提交/发布/外发。 |
| 未完成 | 当前 observed 不是历史来源证明、跨机身份、持续租约或原生 CAS/ABA；历史凭据、配置/记录前后与写时重证、跨 root、提交/崩溃对账及对应双端真实旅程仍未完成。 |

## 2026-11-02 — 原纳管 ID 的只读回执查询（R35）

沿原 G1 unknown「查询原操作」与 G2-r2 的更多/按需诊断契约补齐窗口内观察，不增加解锁、
重提或原生权限。[当前事实](<docs/architecture.md#2026-11-02--原纳管操作的按需只读回执r35>)；
本节仍**不是**跨窗口/重启未知恢复、完整原生准备/重建、完整 r2、ST03–ST08、整体 parity 或发布验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| 契约/接线 | [共享 reader](<src/SystemAutorun.Core/Ipc/AgentOperationClient.cs>) 校验 schema 1、原 ID、字符串状态白名单、必要字段/终态骨架和历史 response 身份；保留 orphan unknown、旧实例、rootId=null、completed/省略正文等合法形态。不应用 response.Data。GUI 使用独立锁存的原 ID，展示属性不能重定向；Console 沿同一 reader 校验成功结果。RPC/receipt schema 不升级，operation 仍非 mutation。 |
| 只读边界 | 查询不造新操作 ID、不重提、不解锁、不改配置/原生登记；既有 GetAsync 仍持 root lease、校验日志并清理精确 owned 临时文件，所以不声称零文件系统活动。未知门控仍仅有置 true 的路径；completed/历史 ok/不可得/读失败均不构成当前业务成功。 |
| VIS — 按需详情 | 实际查看 [更多菜单](<docs/visual/sa-20261102/r35-operation-menu.png>)、[1120×700](<docs/visual/sa-20261102/r35-operation-1120x700.png>)、[1320×820](<docs/visual/sa-20261102/r35-operation-1320x820.png>)、[1620×651](<docs/visual/sa-20261102/r35-operation-1620x651.png>) 的运行 WPF 像素；均为 150% DPI 的 DIP 尺寸，宽窗物理高 977 px。沿原详情容器/字号/内部选中与焦点下划线，无新布局或操作外框。未声称跨 DPI、完整重建表单或整套 r2 通过。 |
| FLOW — 实际键鼠 | owned Debug 窗口中实际点击更多/查询、键盘 Enter/Down 查询、滚动正文、Escape 和返回按钮；UIA 核实两种返回均聚焦 MoreButton。[刷新后](<docs/visual/sa-20261102/r35-operation-refresh-locked.png>) 同一原 ID/未知提示仍在、纳管仍 disabled。一次 popup HWND 的坐标点击被 POINT_COVERED 拒绝且未发输入；重新观察/激活同一 owned 原生主窗后从主窗路由完成，没有操作浏览器。 |
| DATA — 真实查询 | 原前置 unknown 由受控 ST-F1 provider、真实 VM/Pipe/Agent 和缺失 owned 目标产生；不是键鼠重做 adoption，也非真实 Startup 接管。真实独立 Console 查询 [同一原回执](<docs/visual/sa-20261102/r35-original-operation.json>)，外层 operationId=null、内层保持原 ID，原 receipt SHA256 不变且仅一个 operation 文件；配合 NeverRunExecutor，未执行目标或改原登记。 |
| DATA/FLOW — 不可得与错误 | 在第二 owned root 内**故意移除已核实的原回执**来模拟不可得（非真实过期机制），实际键盘查询显示 [不可得警示](<docs/visual/sa-20261102/r35-operation-unavailable.png>)；独立 Console 保留 [operation_not_found/exit 1/Data=null](<docs/visual/sa-20261102/r35-unavailable-operation.json>)，没有新 receipt。再只写该 owned receipt 为坏 JSON 骨架，实际查看 [查询失败](<docs/visual/sa-20261102/r35-operation-store-error.png>)，原 ID 与纳管封锁仍在。两探针身份见 [首个](<docs/visual/sa-20261102/r35-owned-probe.txt>)/[第二个](<docs/visual/sa-20261102/r35-unavailable-owned-probe.txt>)。 |
| ENG — 拒绝/迟到 | [Core 组](<tests/SystemAutorun.Core.Tests/AgentOperationClientTests.cs>) 和 [actual Pipe 组](<tests/SystemAutorun.IntegrationTests/AgentOperationProtocolTests.cs>) 拒错 schema/ID/enum/缺字段/终态/历史操作/缺或重复 data，区分显式 null、not_found 与 store_corrupt；错误帧夹带的证据不输出。 [VM 组](<tests/SystemAutorun.IntegrationTests/StartupGuiControlPlaneTests.cs>) 验替换/清空展示 ID、Root lease 下关闭详情的迟到 read、合成 completed、坏日志及缺失均不解锁；[真实 WPF 组](<tests/SystemAutorun.IntegrationTests/StartupGuiWindowTests.cs>) 验命令绑定和同一只读面板，不冒充该组为真实键鼠。 |
| 失败保留/修正 | 第一轮聚焦 **Core 478/478、集成 97/99**：缺/重复 data 的 JsonException 被 Console 当成用户输入，现限定 operation 的 transport 将其映射 protocol_mismatch，未改 mutation 未知语义。随后 **478/478、100/100**。增加实际缺 receipt 后，[首轮广域](<docs/visual/sa-20261102/r35-matrix.txt>) **884/885**：dispatcher 把 GetAsync 的 null 转成 operation_not_found，并非成功 null；修正 typed 不可得映射，Console 保留 exit 1/原 code、丢弃错误 Data。后聚焦 **479/479、104/104**。未放宽外层协议或跳过断言。 |
| ENG — 历史版本修正与最终矩阵 | 不可得修正后 [前一矩阵](<docs/visual/sa-20261102/r35-matrix-final.txt>) 为两配置各 Core **479/479**、广域两轮 **889/889**。最后复核还发现历史 response 强制等于当前 IPC 版本会跨升级误拒，现只校验历史版本 1..当前，0/未来版本仍拒绝，外层版本仍严格匹配；新增合成旧历史 Core/Pipe 正负例，不冒充实际版本升级。该变更不改当前版本的 UI 结构/输入路径。[最终兼容矩阵](<docs/visual/sa-20261102/r35-matrix-compat.txt>)：Debug/Release 全方案各 **0 警告/错误**，Core 各 **482/482**，广域各连续两轮 **891/891**，均 **0 跳过**。广域不是全量集成、安装或发布验收，真实桌面旅程只来自上面的 Debug owned 探针。 |
| 清理 — 已核实 | 两个 owned 探针正常关闭且各 **1/1**；fixture root 随夹具释放。[初次清理](<docs/visual/sa-20261102/r35-cleanup.txt>) 核对 7 份归档 PNG 的实际签名，验证完整绝对路径、普通目录/无嵌套重解析后仅删除两个 owned 探针与 11 个已知临时截图。[最终核对](<docs/visual/sa-20261102/r35-cleanup-final.txt>) 另只删已验证为空的共享测试父目录；相关项目/测试进程、私有 Mutation/Registration 测试注册根、SystemAutorun 临时目录均 **0**。生产配置/覆盖/GUI 偏好 SHA256 与首个探针前一致，diff 无空白错误。既有未提交工作与 scratch/fatal 保留，未改真实 Startup，未提交/发布/外发。 |
| 明确保留 | 查不到回执不证明原操作未执行，查询失败也不证明原操作失败。本窗口不会因此重新纳管；跨窗口/重启恢复、可信历史/来源 scope、写时重证、跨 root/崩溃对账、受控准备/真实重建、全 CLI parity 及 uptime/日志完整监控仍未完成。 |

## 2026-11-02 — r2 启动项 GUI 只读与配置纳管子集（R34）

本轮沿已批准的 [G2-r2 图片](<docs/visual/sa-20261031/startup-rebuild-g2-candidate-r2.png>) 与
[补充契约](<docs/design-system.md>) 落实 GUI 只读与配置纳管；
[当前事实入口](<docs/architecture.md#2026-11-02--r2-启动项-gui-的只读与配置纳管子集r34>)
保留范围及下一步。本节**不是**完整 r2 GUI、原生准备/真实重建、ST03–ST08、整体 parity 或发布验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| 接线/权限 | [视图模型](<src/SystemAutorun.App/ViewModels/StartupManagerViewModel.cs>) 改用同 root 的 typed `AgentStartupClient`，清单、metadata、显式 native 和配置 adopt 共用 Agent；旧本地扫描/原生写不再进入窗口。缓存/error/unknown/截断只做展示，无法产生写授权；禁用/原备份恢复的 CanExecute 恒 false，直接调用体也不改原生。 |
| VIS — 有界壳体 | 实际查看运行 WPF 像素，并重新查看已批准 r2 参考。ST-F1 四行的 [1120×700](<docs/visual/sa-20261102/r34-startup-1120x700.png>)、[1320×820](<docs/visual/sa-20261102/r34-startup-1320x820.png>)、[1620×651](<docs/visual/sa-20261102/r34-startup-1620x651.png>) 均为 150% DPI 下的 DIP 尺寸；宽桌面 651 DIP 高度按物理像素舍入。已检查标题/细线图标/白色内部选中条/红色异常/页脚及可见按钮边界；未添加操作外框。此处只比较基础壳体，参考图中的原生重建表单仍未实现，右工作区尚非完整 r2 交付；不声称跨 DPI 或精确像素相似度。 |
| FLOW — 键鼠 | 只在本次 owned WPF 窗口实际完成 Ctrl+F、输入“媒体”、[搜索结果](<docs/visual/sa-20261102/r34-startup-search.png>)、[已禁用筛选及刷新](<docs/visual/sa-20261102/r34-startup-filter-refresh.png>)、选择、按需详情/诊断、Escape、更多菜单开关。更多菜单的诊断关闭后，UIA 核实焦点回到 MoreButton。修复菜单默认沿鼠标点展开、行无可读自动化名称、成功提示被自动刷新覆盖，以及菜单详情关闭后焦点回错位置；保持现有补充契约，不重开视觉方向。 |
| DATA/FLOW — 配置落盘 | owned 探针使用真实 WPF → typed Client → Pipe → Agent → 配置编辑。实际修改任务名与参数并显式确认，先前正向样例落盘 `r34-final-adopt` / `--background --kept-final`；最后复验明确成功的 [页脚](<docs/visual/sa-20261102/r34-startup-adopt-success.png>) 及 [配置副本](<docs/visual/sa-20261102/r34-owned-configuration.yaml>)。目标仅为 owned root 内一字节普通数据文件，配合 `NeverRunExecutor`，没有执行该文件或停用原登记。metadata provider 是 ST-F1 合成来源，不能冒充真实 Startup 接管验收。 |
| 未知结果 — 实际业务错误 | 在已明确成功的独立纳管之后，另起**不同任务名、不同意图**的缺失 owned 目标案例，触发 Agent accepted `outcome_unknown`。[提示](<docs/visual/sa-20261102/r34-startup-adopt-unknown.png>) 保留原 ID；只用同 root Console 查询该 ID，保存 [unknown 回执](<docs/visual/sa-20261102/r34-unknown-operation.json>)，没有换 ID 重试。失败后同一表单的编辑仍在；关闭表单并 [刷新](<docs/visual/sa-20261102/r34-startup-unknown-refresh.png>)，纳管图标仍 disabled，原 ID 和明确未知提示仍在；配置仅有前一个已成功任务。 |
| ENG — 控制面与真实槽 | [控制面组](<tests/SystemAutorun.IntegrationTests/StartupGuiControlPlaneTests.cs>) 检查缓存不可写、来源错误、过滤选择、迟到结果丢弃、fresh review/确认、配置/metadata 漂移、accepted unknown 后刷新/直调/清空展示 ID 都不重提。[原生数据组](<tests/SystemAutorun.IntegrationTests/StartupGuiNativeDataTests.cs>) 经真实 Agent/Pipe、账本与 Win32 getter 检查 fixed owned 两侧槽及锁定 unknown；不访问真实用户 Startup，也不产生原生写。 |
| ENG — 窗口夹具修复 | 首轮 Release 广域 **855/857**，新窗口两例为 StaticResource 解析失败；独跑和随后原整轮又通过，不能据重跑宣称解决。源码核实两类窗口测试可并发初始化全局 `Application.Current`，且实例发布早于 `InitializeComponent` 加载资源；将 [启动项窗口组](<tests/SystemAutorun.IntegrationTests/StartupGuiWindowTests.cs>) 与 [远程窗口组](<tests/SystemAutorun.IntegrationTests/RemoteHostsWindowTests.cs>) 归同一非并行 WPF collection。未改生产主题或延长时限；首轮缺失 key 的完整原始日志未保留，不声称还原了当时精确调度。 |
| ENG — 最终矩阵 | 修复后 Debug/Release 全方案各 **0 警告/错误**，Core 各 **445/445**；原广域过滤器由 839 增至 **857**，两配置各连续两轮 **857/857**，均 **0 跳过**。[完整最终输出](<docs/visual/sa-20261102/r34-matrix.txt>) 可复核。广域仍非全量集成、实际安装或发布验收；真实键鼠证据来自上面的 owned Debug 探针，不用 Release 自动测试冒充 Release 桌面旅程。 |
| 清理 — 已核实 | 三次 owned 探针关闭后正常通过；独占配置 root 随夹具释放。检查绝对路径、普通目录及无嵌套重解析后，仅删三个 owned 探针目录、已归档的已知临时截图与为空的共享测试父目录。SystemAutorun 临时根、项目/测试残留进程、私有 Mutation/Registration 注册根均 **0**。广域矩阵前后主配置/覆盖/偏好的 SHA256 一致；`git diff --check` 无空白错误。保留既有未提交工作及 scratch/fatal，未发布/提交/外发，也未改真实 Startup。 |
| 明确保留 | 未知门控仅作用于**当前窗口生命周期**；GUI 原操作回执查询、跨窗口/重启持久恢复尚未实现。可信记录/来源 scope、写时身份重证、跨 root/崩溃对账、受控原生准备/真实重建与完整 GUI/CLI 旅程仍未完成。 |

## 2026-11-02 — 显式 native 只读 CLI 模式（R33）

本轮沿 G1-startup-r2 的按需只读准备旅程，将 R32 组合证据接入 Console；默认 schema 1、
GUI 和原生写门禁不变，G2/VIS 不适用。[当前事实入口](<docs/architecture.md#2026-11-02--显式-native-只读-cli-模式r33>)
记录范围；本节不是完整原生准备/重建、r2 GUI、ST03–ST08、发布或总体 parity 验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| CLI — 显式兼容 | [参数/路由](<src/SystemAutorun.Cli/CliArguments.cs>) 仅为 startup inspect 增 --native：默认原 RPC/schema 1，显式模式才是 startup-inspect-native/schema 2，外层 CLI 信封仍 1。选中数据和 revision 来自清单，native 输入限 disabled shortcut；拒其他命令上的标志、赋值/重复标志、--yes、operation ID、路径/身份/授权注入和坏 preview，不隐式启动后台、提权、执行或 fallback。 |
| CLI — 结果/错误 | [调用器](<src/SystemAutorun.Cli/CliApplication.cs>) 成功先通过严格 typed reader；native 失败同样走 reader 的异常路径，保留业务 code/revision/requestId，但丢弃错误信封夹带的证据。任一 unknown 保留 data.slots、ok=false/exit 1，permission 则 4；配置/选择/record 漂移 5，取消/超时 6 且无 Data。明确 present/missing 的只读完成可 exit 0，但所有写/执行标记仍 false。 |
| CLI — 人类显示 | [输出](<src/SystemAutorun.Cli/CliOutput.cs>) 独立列归档/原槽的状态、原因、native error、case、时间，标明 sequential-independent/unverified、非 snapshot/CAS、present 不证明历史原件及双 missing 不授权重建。可选 lexical_only preview 保留；共用 helper 不改变旧 metadata 文本，完整身份和 blocked actions 留在 --json。 |
| DATA/FLOW — 真实 Console | [真实数据组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionCliDataTests.cs>) **16 项**启动本次构建的独立 Console PE，经真实 Pipe、owned 账本与实际 Windows native reader：两固定源各四状态，真实共享锁的 JSON/人类非零部分结果，实际目录 ACL 拒绝的 exit 4，派生时真实改账本的 exit 5/Data=null，上下文拒绝；完整 CLI list → 选择 JSON → 默认 metadata → 显式 native 人类结果。固定源只映射独占临时目录，不动真实用户 Startup。 |
| DATA — 零变异/释放 | 成功组合后配置/账本字节不变，所有 native 写/执行标记 false，操作 seam 和 writable registry open 均 0。共享锁释放后立即能写本夹具两侧文件；真实 ACL 在 finally 恢复原 DACL 二进制并核对 SDDL。ordinary 数据文件只证明当前槽状态，不冒充真实原 LNK。Console 进程只持有本次启动的 Process handle，超时清理不按通用进程名杀进程。 |
| FLOW — 模拟调度 | [CLI 控制面组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionCliTests.cs>) **24 项**使用实际 Agent/Pipe、模拟 getter/身份。验证默认不进 native、非法选择在 connect 前拒绝，三类 unknown 的双输出、显式 preview、三类漂移。caller cancel 和真实等待 5 秒 server deadline 均 exit 6/Data=null，非合作 Task 未结束时第二 CLI 仍 busy、reader 仅一次，释放后才重新准入；不是 OS getter 挂起或 GUI FLOW 证据。 |
| ENG — 协议/无降级 | [合成 CLI 协议组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionCliProtocolTests.cs>) **21 项**拒 flags/CAS/replay/schema/嵌套授权/缺槽/错 record/revision/非法 reason/父身份/computed field；旧 Agent 无新端点明确失败且仅一个 RPC。其中 3 项失败信封夹带伪授权与成功样貌时 Data/observedAt=null，code/requestId/revision 保留。该组含合成 transport，不冒充 OS 身份或完整 wire root 验证。 |
| 失败保留/夹具修复 | 首轮 Startup **565/566**：新测试把既有 lexical_only 文本误写成 lexical-only，仅修断言。第二轮仍 **565/566**：旧裸 Pipe entry-computed 夹具 15 秒取消；核实其连接 EOF 留到客户端断言之后，和生产 [单响应连接所有权](<src/SystemAutorun.Agent/AgentServer.cs#L79-L89>) 不同。将 [Serve 自身持有并关闭连接](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionProtocolTests.cs>)，加整 KiB、非 4096 倍数字节帧回归；不改生产传输、不延长时限。没有记录失败那次精确帧长度，不声称证明具体缓冲触发值。最终聚焦 **570/570**。 |
| ENG — 最终矩阵 | 新增集成 **62 项**（含旧协议组新增 1 个 actual Pipe 边界例）。Debug/Release 全方案各 **0 警告/错误**，Core 各 **445/445**；原广域过滤器两配置各连续两轮 **839/839**，共四轮，全部 **0 跳过**。广域含原 Agent/CLI/GUI、inventory/inspect/adopt、COM/native、配置/日志等，不是全量集成或发布验收。 |
| 咨询界限 | 安全角色的有界源码咨询未返回结果，已停止并释放，不计作审查或执行证据。上表只采用主代理直接核实的源码、实际运行和清理结果。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 与轮前一致。测试/项目进程与构建服务器、私有 Mutation/Registration 注册根均 **0**；仅删除已核实普通、非重解析、为空的共享测试父目录，SystemAutorun 临时根 **0**。保留既有未提交工作、scratch/fatal；未发布/提交/外发/迁移或操作浏览器。 |
| 下一步/未完成 | 继续可信原记录/来源 scope、写时重证、跨 root 与崩溃对账门禁，落实 r2 GUI 和完整受控准备/真实重建。新原生写 CLI 仍关闭；ST03–ST08 与整体 parity 不计完成。 |

## 2026-11-02 — Agent 版本化原生观察与完整 single-flight（R32）

本轮完成已批准只读准备的 Agent 组合子集，不改 GUI、CLI 路由/输出、旧 inspect schema 1
或原生动作门禁，G2/VIS 不适用。[当前事实入口](<docs/architecture.md#2026-11-02--agent-版本化原生观察与完整-single-flightr32>)
记录范围；结果不是完整原生准备、ST03–ST08、r2 实装或整体 parity 完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| 契约/兼容 | [独立 schema 2](<src/SystemAutorun.Core/Models/StartupNativeInspection.cs>) 与 [类型客户端](<src/SystemAutorun.Core/Ipc/AgentStartupClient.NativeInspection.cs>) 使用 startup-inspect-native。选中 disabled shortcut + revision + 元数据摘要，没有来源/归档路径、身份、写授权或 operation ID 输入；原显式 RebuildInput 仍仅词法预览。nested schema 1 的 originalBackupState 继续 not_checked，所有层写/执行标记 false，固定 sequential-independent/unverified。旧 metadata RPC、CLI 和 GUI 不变。 |
| single-flight/时限 | [协调器](<src/SystemAutorun.Agent/AgentStartupService.NativeInspection.cs>) 在旧 admission 下持有整个前检→双槽/record 后读→revision/metadata 后检 workflow，避免阶段间交错与嵌套进门。共用 5 秒协作预算，caller WaitAsync/取消不释放仍工作的 getter；只有实际完成和 cleanup 后才放门。没有队列、重试、迟到回填、缓存成功或替换 worker。metadata reader 同样在 manager Dispose 后才 completion，两个外层等待均显式重查取消。 |
| DATA — 真实接线 | [真实数据组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionDataTests.cs>) 的 **14 项**使用完整账本、实际 Windows reader、NtOpenFile/Kernel32、真实 Pipe 与 typed client；固定来源映射限定独占临时目录。Startup/Common Startup 各四种存在组合与既有后端逐字段差分，双 missing 仍无写权，字节保持，普通文件不冒充真实原 LNK。真实共享锁保留单侧 unknown/occupied，释放后新请求才得 present。 |
| DATA — ACL/上下文/漂移 | 只对 owned 来源父目录实际 deny ListDirectory，wire 保留 original_permission_denied 且无目录身份；finally 显式恢复原 DACL、重读 SDDL 一致。网络 origin/archive 先完整词法拒绝为两侧 context_invalid，不探测这些地址。固定源解析阶段真实改本次账本，前后同序列化器 record digest 不等后返回 startup_backup_changed/Data=null，没有写入口/执行/事务残留。 |
| FLOW — 受控调度子集 | [模拟控制面组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionControlPlaneTests.cs>) 的 **25 项**区分模拟与 OS 数据：三个阶段的非合作 Task 取消保留整段 admission；5 个并发调用均 busy、request ID 各自绑定、reader 仅调用一次。真实等待 5 秒得显式 timeout/Data=null，slot 继续 busy，ping/config 可用，解除后新请求重新观察而非用迟到缓存。另验选择/记录摘要/revision 漂移、前后不完整来源、非法选择/注入与 reader 故障。不是 GUI 真实旅程或 OS getter 强制超时证据。 |
| ENG — 严格协议 | [合成响应组](<tests/SystemAutorun.IntegrationTests/StartupNativeInspectionProtocolTests.cs>) 的 **36 项**含 actual Pipe。严格 required constructor/unknown member；复用旧 metadata raw computed fields、动作与 preview 复核。拒省略 flags/time、任一层写/执行授权、cas、新历史信任/atomic scope、错 revision/ID/source/name/digest、坏时序/身份/本地目录、非法 missing/present，以及 operation/replay/error。没有探测响应内目录或 identity。 |
| 失败保留/最小修复 | 首轮 Startup 聚焦 **497/504**，7 项新夹具前置错误：5 个裸 Pipe 根缺必需 YAML，另 2 项把 typed transport 的非法 revision/只读 operation ID ArgumentException 误当服务响应。仅补独占空配置并保持字节，按实际 client/server 边界断言；没有放宽生产 root/输入验证、跳过断言或把失败覆盖成成功。增加 4 个负例后最终聚焦 **508/508**。 |
| ENG — 最终矩阵 | 新增集成 **75 项**。Debug/Release 全方案各 **0 警告/错误**，Core 各 **445/445**；原广域过滤器两配置各连续两轮 **777/777**，共四轮，全部 **0 跳过**。含原 inventory/inspect/adopt、native/COM、Agent/CLI/GUI/日志等回归，但不是全量集成、发行、VIS 或完整用户旅程验收。 |
| 可信边界/咨询 | record 前后都用同一 startup-backup-metadata-v1 全部已知字段序列化器，不拿 entry 摘要与 record 摘要比较；相等不证明账本原字节/真实性、历史来源、原对象、ABA 或 CAS。安全角色仅无源码设计建议，无项目执行证据；主代理以已有 Agent error envelope 明确失败/Data=null，不造假 unknown 两槽。真实 FileShare.None 会立即 occupied，非合作 getter 挂起只用受控 Task 模拟；adapter 复用既有观察，没有两套 native 实现。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 均与轮前一致。项目/临时测试进程、构建服务器、私有 Mutation/Registration 根均 **0**；只删除核实为空且非重解析的共享测试父目录，SystemAutorun 临时根 **0**。保留既有未提交工作、scratch/fatal；没有真实系统 Startup 改写、迁移、发布、提交、外发或浏览器操作。 |
| 下一步/未完成 | 将新只读证据作为显式 CLI inspect 模式接入已批准旅程，继续历史可信记录/来源 scope、写时身份重证、跨 root 与崩溃对账门禁。新原生写 CLI 关闭；r2 GUI/历史、完整原生准备/真实重建、ST03–ST08 与整体 parity 仍未完成。 |

## 2026-11-02 — 固定来源与归档的双槽只读证据（R31）

本轮继续获批 G1 的后端证据基础，未改窗口、用户旅程、metadata IPC/CLI 或原生动作门禁，
G2/VIS 不适用。[当前事实入口](<docs/architecture.md#2026-11-02--固定来源与归档的双槽只读证据r31>)
记录边界；本节不计为 ST03–ST08、完整准备、r2 实装或总体完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| 共享契约/兼容 | [新模型](<src/SystemAutorun.Core/Models/StartupShortcutSlotEvidence.cs>) 分别承载 Archive/Original 的状态、原因、时间和本机原生身份；Original 只代表当前固定来源的名字槽。组合接口统一读取一份完整记录，先验证两侧全部布局再观察，两者不构成原子快照，也不判断历史来源/原对象真实性。原 archive-only API 的形状/原因与不探测 origin 的行为保留。 |
| 原生复用 | [共用 primitive](<src/SystemAutorun.Windows/Startup/StartupNativeSlotProbe.cs>) 保留 R30 相对父句柄 lookup、完整自身父链、独立前后 case flag、FileIdInfo、share-read/非重解析及 64 KiB 守卫；[适配器](<src/SystemAutorun.Windows/Startup/StartupArchiveSlotProbe.cs>) 保留归档 GUID-only 与 archive_/original_ 原因空间。补拒普通路径分量超过 255 UTF-16 单元。未增加链接解析、target/workdir 探测、重试、worker、lease 文件或原生写。 |
| DATA — 实际双槽 | [owned 双槽组](<tests/SystemAutorun.IntegrationTests/StartupShortcutSlotEvidenceTests.cs>) 使用真实 NtOpenFile/Kernel32、临时私有源映射，覆盖 Startup/Common Startup 各四种存在组合。故意非 LNK 的普通字节只给 present，不冒充原文件或可恢复；fake 记录双 missing 也不授写权，全部 NativeWriteReady/NativeWritePerformed/ExecutionRequested=false。JSON 往返、记录指纹与账本原字节保持稳定。 |
| DATA — 独立失败/兼容 | 独占锁原叶时新 API original_occupied/32、归档仍 missing，旧 API 仍正常 archive-only；独占锁归档时 archive_occupied，但来源仍 present。两侧分别有目录身份，缺父目录是 original_directory_missing 且不创建目录。取消/未决 intent 不产生成功证据。真实目录叶、reparse 叶/父与 65537 字节均拒为 unknown，未跟随目标或解析内容。 |
| DATA — ACL/持柄 | 只对 owned 原叶或来源父目录增加实际 ReadData/ListDirectory deny，观察 original_permission_denied/5，父拒读无身份、叶拒读有父身份。finally 强制置入原 DACL 二进制后应用，重读 SDDL 相等。普通 held 原叶 writer 与父目录 rename 被拒；组合返回后可换同字节新 fileId，记录指纹不变，fixture 递归清理证明没有残留 pin。 |
| DATA — case/名字/别名 | [原生来源组](<tests/SystemAutorun.IntegrationTests/StartupShortcutSlotNativeCasesTests.cs>) 实际设置并还原敏感标志：精确存在叶 present、变体/空敏感槽非 missing，持柄期间 sensitive→insensitive 返回 original_case_semantics_changed。敏感祖先下同时建立 source/SOURCE 两目录，仅另一父目录有文件时仍观察固定 source 槽，不采用可编辑 origin 的父目录拼写；这不是历史来源身份认证。 |
| DATA — 最终叶/硬链接 | Unicode、basename 含 .lnk、扩展名前点/空格的合法最终叶正常 present，不追加/剥离扩展名。拒读来源父目录下，300 字符/ADS/保留设备/遍历等最终分量仍 context_invalid，Archive/Original 均无目录信息，说明先完整词法校验。真实跨槽硬链接给出同一 fileId 与 linkCount=2，但不授信；返回后可移动归档名并经原名写同一对象。 |
| ENG — 首轮失败保留 | 首轮 Debug 两次 **701/701**；Release 第一次 **700/701**，既有 [进程夹具](<tests/SystemAutorun.IntegrationTests/HiddenProcessRunnerTests.cs>) 读已发布 pid.txt 时遇 IOException sharing violation，尚未执行取消/超时断言。PID 已用 tmp+Move 发布，持锁进程未定位，不宣称生产 runner 有缺陷；第二次 **701/701** 未覆盖首次失败，整 job 仍返回失败。 |
| ENG — 夹具最小修复 | 仅测试 PID 读取加有界、单调计时的 missing/sharing(2/3/32/33) 等待，主断言复用已读 PID，cleanup 同样有界；权限及坏报告不吞掉。新增实际 FileShare.None 的有界超时/释放后成功与坏 PID 拒绝负例。生产 HiddenProcessRunner 未改，取消/期限与真实退出断言保留，未跳过、放宽或自动重跑生产操作。 |
| ENG — 最终验证 | 新双槽 **34** 项，加 PID fixture **1** 项。原生兼容聚焦 **59/59**；含 deterministic PID/runner 的最终聚焦 **66/66**。最终 Debug/Release 全方案均 **0 警告/错误**，Core 各 **445/445**，原广域各连续两轮 **702/702**，共四轮，全部 **0 跳过**。不是全量集成/发行或 UI FLOW/VIS。 |
| 咨询边界 | role-6 只做无源码设计建议，未读项目、改代码或执行。主代理采纳固定源自身身份/最终叶/独立状态/无重试的核查，并用真实数据验证；没有将不存在的历史 capturedIdentity 伪装已绑定，也未机械拒绝真实合法的 basename 双扩展。原记录可信性、历史来源 scope 和非合作写者 CAS 仍未完成。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 均与轮前一致。项目/测试/持 PID PowerShell 进程、构建服务器与私有 Mutation/Registration 根均 **0**；仅删除核实为空且非重解析的共享测试父目录，SystemAutorun 临时根 **0**。测试 ACL/case 均在独占对象范围内还原/清除；既有未提交工作、scratch/fatal 不动，无真实 Startup/生产配置写、发布、迁移、提交或浏览器操作。 |
| 下一步/未完成 | 将双槽读证据接入 Agent 的版本化只读组合与单 worker 协调，保留旧 metadata/lexical 契约。历史可信记录/来源 scope、配置/记录前后复核、写时重证、跨 root 和崩溃对账未闭合，新原生写 CLI 继续关闭。完整原生准备/真实重建、r2 窗口及历史、ST03–ST08 与总体 parity 均未完成。 |

## 2026-11-02 — 原 GUID 归档槽的只读原生证据（R30）

本轮是获批 G1 的后端准备基础，不新增用户旅程/窗口，也不复用图片来宣称 UI 实装；G2/VIS
不适用。已重读 SPEC、当前 G1/ST01–ST08 与既有实现；保留 r2 批准，原 schema 1 inspect
不改成 native preparation。[范围与残余](<docs/architecture.md#2026-11-02--原-guid-归档槽的只读原生证据r30>)
继续沿原事实入口记录，下面不计为完整 ST03–ST08 或总体完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| 共享契约/接口 | [证据模型](<src/SystemAutorun.Core/Models/StartupBackupSlotEvidence.cs>) 分离可编辑 recordMetadataDigest 与原生观察；[后端入口](<src/SystemAutorun.Windows/Startup/StartupManager.BackupSlotEvidence.cs>) 只读当前完整账本、拒未决事务、从当前配置和固定源派生原 GUID 槽。它不接 IPC/CLI、不生成凭据或新 worker，不探测 origin/target/workdir；全部 NativeWriteReady/NativeWritePerformed/ExecutionRequested=false。 |
| DATA — 真实对象 | [owned 主组](<tests/SystemAutorun.IntegrationTests/StartupBackupSlotEvidenceTests.cs>) 在独占临时根与私有 native 映射下真实调用 Kernel32/NtOpenFile；普通文件（故意不是有效 LNK）报 present 并给出 64 位卷号/128 位 fileId、长度、linkCount。重复读取身份稳定，保持旧文件后新建同字节文件可观测不同 fileId；换父目录可观测不同 directoryId，recordMetadataDigest 仍不能替代原生身份。 |
| DATA — 真实缺失/异常 | 父目录存在并确认身份、相对 lookup 为 ERROR_FILE_NOT_FOUND、前后语义 insensitive 才 missing；同一虚构账本记录也能得到 missing，但仍不授权。父目录缺失报 unknown 且无目录身份；真实 FileShare.None 报 archive_occupied/32；目录叶、重解析叶/父目录及 65537 字节对象均 unknown，不读链接内容或启动目标。 |
| DATA — 路径与旧记录 | [纯 Resolve 复用](<src/SystemAutorun.Windows/Startup/StartupShortcutPathLease.cs>) 沿当前 GUID 布局校验，不改哈希命名；旧根/网络 archive、网络 origin、名称遍历与 CON 空格别名报 archive_context_invalid，未探测这些路径。已终结处置/注册表记录 not_applicable，无归档目录身份；取消与未决 intent 不产生成功观察。 |
| DATA — held 共享/释放 | 相对父句柄 lookup 不沿账本绝对路径重新寻址，封存前重开当前父路径比身份；callback 中普通 Directory.Move 与叶子 writer 都被实际 no-delete/read-share 句柄拒绝，返回后文件可写、目录可换，取消后也可写。此证据覆盖普通 Windows 共享规则，不是所有外部写者的 CAS。 |
| DATA — 真实 ACL | [原生边界组](<tests/SystemAutorun.IntegrationTests/StartupBackupSlotNativeCasesTests.cs>) 只给本次独占临时叶/父目录加入当前身份的 ReadData/ListDirectory deny；分别实测 unknown/archive_permission_denied/5，叶拒读仍有父身份，父拒读无身份，均非 missing。finally 显式还原原 DACL 二进制并重读 SDDL 一致；无生产/共享目录 ACL 更改。 |
| DATA — 真实大小写 | 实际在 owned NTFS 目录设置/还原 case-sensitive 标志，不启 WSL、不用 UAC。普通目录的 uppercase GUID+.LNK 仍 present；敏感目录的精确存在叶 present，大小写变体不报 missing，空敏感目录 unknown；在持柄阶段真实将模式 sensitive→insensitive，前后漂移返回 unknown/archive_case_semantics_changed。不是抵抗任意反复翻转/外部竞争的原子证明。 |
| DATA — 真实硬链接 | 在同一 owned 目录实际建另一 GUID 硬链接，观察同一 fileId 与 linkCount=2；持柄时经另一名字请求 writer 也被共享仲裁拒绝。返回后不承诺内容稳定，不认证任何原字节或记录来源。未用 mock 替代这些 Native/ACL/case 数据。 |
| 失败保留/夹具修复 | 首次扩展组 **22/24**，两项 ACL 恢复/后续清理失败：未修改的 GetAccessControl 原对象直接 SetAccessControl 无实际回写。改为 finally 显式 SetSecurityDescriptorBinaryForm 原 DACL 后应用，并核验 SDDL。原文件 deny 随其独占根删除；只对精确失败目录核对并移除新增的一条当前身份 deny，再核实仅剩空 backups/startup 后删除。没有广泛重置 ACL、清空临时目录或吞掉失败。随后 **24/24**，补模式漂移后最终 **25/25**。 |
| ENG — 最终产物 | Debug/Release 整解决方案均 **0 警告/错误**；Core 各 **445/445**；含新增 **25** 项的原广域过滤器各连续两轮 **667/667**，共四轮，全部 **0 跳过**。包含既有 mutation/restore、COM、Agent/CLI/GUI 等原广域回归，但不是全量集成/发行验收，也不是 UI FLOW/VIS。 |
| 安全咨询 | role-6 仅设计咨询，未读项目/改代码/运行。主代理核实已有祖先 pin 的真实 access/share、采用相对 NtOpenFile、独立大小写查询与 64 位 FILE_ID_INFO；没有机械采信普通父目录可随意改名、32 位字段或同流硬链接绕开共享的假设。咨询与实际执行证据分开，未向外部网页顾问发项目。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 与轮前基线一致。项目/测试进程、构建服务器、私有 Mutation/Registration 注册表根均 **0**；失败根已精确清掉，测试 DACL/大小写变更均已还原或随独占对象删除，最终只删除核实无重解析且为空的共享父目录，SystemAutorun 临时根 **0**。原未提交工作/scratch/fatal 保留，没有真实系统 Startup 改写、发布、迁移、提交或浏览器操作。 |
| 未完成/下一步 | 配置与账本不是同一原子快照，观察句柄返回即释放。下一切片补固定原生来源槽的真实只读证据及 Agent 单 worker 组合；前后 revision/记录复核、可信原记录、写时身份重证、跨 root 与崩溃对账未闭合，写 CLI 继续关闭。完整原生准备、r2 窗口/历史、真实重建、ST03–ST08 与总体 parity 仍未完成。 |

## 2026-11-02 — 显式重建输入的只读预览（R29）

本轮完成获批 r2 的共享显式输入预览子集，不称完整原生准备。主代理重新读取 G1/ST01–ST08、
r2 补充契约并实际查看原 r2 图片，没有更改批准产物或 WPF 窗口；本切片 G2/VIS 不适用。
[输入、规范化与剩余边界](<docs/architecture.md#2026-11-02--显式重建输入的只读预览r29>)
沿原事实入口维护，不新增任务/验收账本。

| 类别 | 实际结果与边界 |
| --- | --- |
| 共享契约 | [输入策略](<src/SystemAutorun.Core/Models/StartupRebuildInput.cs>) 接受显式 target/arguments/workingDirectory，输出规范输入和带版本前缀的 inputDigest；纯词法，无 File/Directory/Drive/Process/Environment 访问。它只声明 lexical_only/not_checked，不证明盘符介质、目标可用、原记录来源、归档状态或原生身份。 |
| DATA — 正常/边界 | [新增 Core 组](<tests/SystemAutorun.Core.Tests/StartupRebuildInputTests.cs>) 覆盖 Unicode/emoji、本地 exe/com、引号/tab/参数原文、规范化幂等与 JSON 往返；拒绝 UNC/设备/扩展/相对/展开/遍历/流/通配符/设备名（含 CON 空格别名）、非程序扩展、超限、控制字符及未配对代理项。独立 UTF-8 JSON SHA256 计算固定向量，防止无意改变摘要协议；路径分量大小写仍影响摘要，不猜文件等价。 |
| DATA — 客户端/服务端 | [现有 inspect 扩展](<src/SystemAutorun.Core/Models/StartupInspection.cs>) 只允许所选禁用快捷方式附加输入；[Agent](<src/SystemAutorun.Agent/AgentStartupService.Inspection.cs>) 仍复读配置/选中项，不从账本补 target。真实 Pipe 直接注入非法输入、缺失/null target、未知字段、活动项/注册表项均在扫描前拒绝，source.Calls=0。 |
| DATA — 协议拒绝 | [类型化校验](<src/SystemAutorun.Core/Ipc/AgentStartupClient.Inspection.cs>) 重算并严格匹配输入/摘要/范围/可用性；缺失、篡改、未经请求或伪称 native_ready/verified 的预览拒绝。真实假服务 Pipe 将未经请求的预览交给两类客户端，均返回 protocol_mismatch；未产生 operation ID。旧无预览报文分支继续通过，未宣称跑过旧版本二进制组合。 |
| FLOW — 真实 CLI | [端到端组](<tests/SystemAutorun.IntegrationTests/StartupRebuildPreviewControlPlaneTests.cs>) 实际启动现有 Console，以独占 JSON 文件和 stdin 分别通过真实 Pipe 查询；JSON 保留完整规范输入、not_checked、false 写入/执行标记与 null operationId。另在进程内运行人类文本分支，明确提示“拟议输入，不是原备份/资格/确认，原生重建仍关闭”。三种输入编辑各产生新规范摘要，选中记录与 metadataDigest 不变。没有以该 CLI FLOW 代替 GUI 旅程。 |
| DATA — 真实账本/适配器 | [owned 数据组](<tests/SystemAutorun.IntegrationTests/StartupRebuildPreviewNativeDataTests.cs>) 通过真实有界 JSON 账本、现有 Windows reader、真实 Pipe 与 CLI，核对网络历史 origin/archive/target 只作元数据。显式拟议目标为独占临时根内的锁定未运行文件，预览仍成功；目标、账本、配置字节不变，writable opens/operations 均无增加。锁测试仅证明没有打开该目标，不能单独证明所有形式的探测；零 I/O 策略代码审阅为另一项证据。 |
| ENG — 最终产物 | 新增 **54 Core + 16 集成项**。定向 Core **112/112**、集成 **65/65**；Debug/Release 整解决方案均 **0 警告/错误**，Core 各 **445/445**，广域各连续两轮 **642/642**，共四轮，全部 **0 跳过**。范围仍是原广域过滤器，不称全量集成/发行验收。 |
| 权限/原记录 | 安全咨询未读项目或运行测试，不是独立执行审计。主代理核对归档槽是原 GUID 布局，未采纳哈希重命名建议；也未把“告知来源未验证”变成创建任意旧记录的批准。虚构记录同样能呈现 missing，原生动作继续 blocked/not_applicable，所有预览均不是原生提交凭据。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 与轮前基线一致。项目/测试进程、构建服务器、私有 Mutation/Registration 注册表根均 **0**；仅删除精确路径、非重解析且已核实为空的共享父目录，SystemAutorun 临时根 **0**。保留原有未提交工作/scratch/fatal，无真实系统启动项改写、发布、迁移、提交或浏览器操作。 |
| 未完成/下一步 | 完整原生准备、可信原记录、叶子身份/写时重证、跨 root 与崩溃对账仍未闭合，写 CLI 不开放。下一切片沿现有 GUID 归档槽实现正向目录身份/缺失证据，拒读/占用/重解析/上下文漂移不能推导 missing。r2 界面、真实重建、ST03–ST08、整体 GUI/CLI parity 及总体目标继续保持未完成。 |

## 2026-11-02 — 固定 COM 调用与 CLR 生命周期（R28）

本轮按原批准行为的修复快速路径恢复快捷方式与注册观测可靠性，没有新增 GUI/CLI 业务、
权限或用户旅程。[当前边界](<docs/architecture.md#2026-11-02--固定-com-调用与-clr-生命周期r28>)
区分机制证据、失败候选、最终缓解与尚未证明的根因；没有通过串行化测试或吞异常取得全绿。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 计数证据的反例 | [普通托管别名](<tests/SystemAutorun.IntegrationTests/StartupComLifetimeTests.cs#L10-L36>) 在旧单次 `ReleaseComObject` 上真实报 `InvalidComObjectException`；同次运行的第二次 IUnknown 获取及 32 次 STA 创建/读取均通过，合计 **2 通过/1 失败**。普通赋值不增加 RCW 导入计数，故 R25 的“平衡释放”实验证明不了独占。该实验是断开机制证明，不是 R27 广域触发者的定位。 |
| ENG — 保留的失败候选 | 首先只移除全源的手工 RCW 断开，保留 DLR。定向 **3/3**，但 Debug 广域依次为 **620/620、619/620、619/620**；后两轮分别在真实创建器的 `GetTypeAttrForTypeInfo` 和清单读取的 `GetITypeInfoFromIDispatch` 失败。Release 三轮各 **620/620** 不能掩去两次 Debug 失败；因此未把此候选单独视为充分修复。 |
| 实现 — 固定成员边界 | [内部调用器](<src/SystemAutorun.Windows/Startup/StartupComDispatch.cs>) 通过固定反射访问成员，成员名只来自受控代码，不来自 RPC。WSH 读取及所有 WSH 创建夹具不再走 DLR；[注册观测图](<src/SystemAutorun.Windows/Startup/StartupRegistrationReader.cs#L65-L110>) 同步转换。未重构其他 Scheduler 写入路径，不宣称其已获本轮验收。 |
| 实现 — 不强制断开 | [结束使用](<src/SystemAutorun.Windows/Startup/StartupManager.cs#L514-L521>) 仅 `GC.KeepAlive`，由 CLR 回收 RCW。没有 `ReleaseComObject`/`FinalReleaseComObject`、全局 GC、Finalize 等待或 COM 重试。测试自行 AddRef 的原生 IUnknown 指针仍成对 `Marshal.Release`；它不是 RCW 断开。所有对外结果仍为托管 DTO，不增加生产线程。 |
| DATA — 异常与元数据 | [新增调度组](<tests/SystemAutorun.IntegrationTests/StartupComDispatchTests.cs>) 用三种失败位置断言反射重抛的原异常身份/HResult/栈，防止 `TargetInvocationException` 改变缺失/拒权分类；必需的 null 结果拒绝，不编造元数据，可选字符串仍保留 null。已有注册 double 的固定读取顺序、缺失与拒权分类一起通过。 |
| DATA — 真实 Scheduler | 在真实 STA 上 Connect，仅创建**未注册的内存任务定义**和内存 Exec action，实测方法/索引属性联合调用、Count/Type 及 Path setter/getter。未设置 Path 时本机返回 null，不伪造空串；随后只写入随机临时根下的未创建目标字符串并读回，目标未创建/运行。没有 RegisterTask、Save、Run、删除或真实任务更改，不用托管 double 代替该项。 |
| DATA — WSH 回归 | 保留二次 IUnknown 获取，加普通别名的预热/结束后再调用，以及 **32 次独立且已 join 的 STA 创建 + 生产专属 STA 读取**，逐次核对临时链接字节不变、参数与无目标运行。它们不构成长时间内存平台期或断电证据。最终共新增 **8 项**，广域从 618 增至 626。 |
| ENG — 最终产物 | 定向 **38/38**。Debug/Release 整解决方案均 **0 警告/错误**，Core 各 **391/391**；每配置连续三轮广域各 **626/626**，共六轮，全部 **0 跳过**。未用排除新组、固定测试次序或禁并行取得这些最终结果；原 Journal 19 项也包含其中。不是全量集成、发行包或 r2 界面验收。 |
| 解释 — 不夸大根因 | 策略咨询未读项目/执行测试；公共源码探索也不是本机执行审计。主代理抽查 [.NET 10 描述符源码](<https://raw.githubusercontent.com/dotnet/runtime/v10.0.0/src/libraries/Microsoft.CSharp/src/Microsoft/CSharp/RuntimeBinder/ComInterop/ComTypeDesc.cs>)，未见保留传入 ITypeInfo 的字段，未采纳“全局描述符缓存保存旧 STA RCW”的推断。固定反射避开实际失败路径；没有跟踪证明具体 COM 身份、宿主退出或 CLR 原生映射如何导致原事故，也不宣称已修复运行时本身。 |
| 资源与剩余风险 | STA 退出、任务完成与 DTO 返回不保证 COM 已立即回收；现有 worker/扫描预算仅限制活跃工作，不是累计待回收 RCW 的硬上限。本轮无长期内存验收，不承诺所有外部 COM 服务故障均消失；硬回收预算若成为要求，须另行设计真正独占边界或预算化辅助进程。 |
| 范围与下一步 | GUI 未改，本切片 G2/VIS 不适用；没有把 COM 自动化当作 r2 键鼠旅程。继续回到已批准 r2 的可信原记录与真实身份准备，不能把可编辑 metadataDigest 升级成原生写权。原生重建、完整 ST03–ST08、GUI/CLI parity 及整体目标仍未完成。 |
| 清理 — 已核实 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 与轮前基线一致。项目/测试进程、构建服务器、私有 Mutation/Registration 注册表根均 **0**；只删除精确路径、非重解析且已核实为空的共享测试父目录，SystemAutorun 临时根为 **0**。既有 scratch/fatal、未提交工作保留；无发布、迁移、提交或浏览器操作。 |

## 2026-11-02 — 操作日志未变水位重复发布修复（R27）

本轮恢复已有的顺序配置持久化路径，不增加命令、权限或交互，不更换 journal 发布原语。
[范围与不变量](<docs/architecture.md#2026-11-02--操作日志不重复发布未变水位r27>) 只覆盖冗余元数据
发布；初始化、退休水位推进及回执提交仍可遇到 I/O 失败。R26 那次间歇故障的实际句柄/权限
来源仍未知，不能把源码调用图当成“排除进程外干扰”的证明。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 修复前实证 | 先只附加诊断上下文，不改 I/O。独占临时根内的 7 项候选实验 **3 通过/4 失败**：两种 Delete 共享下，元数据覆盖均在真实 `File.Move` 报 Win32 5；两种回执候选也在副作用后进入 unknown。不是模拟文件操作。后两项不是承诺兼容打开回执的依据，最终改为三种共享模式下的 unknown/不重执行断言，而不是改发布原语或抹去保护。 |
| 实现 — 最小修复 | [完整读取](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L248-L267>) 区分成功读到的有效 schema 1 与缺失时的内存默认值。[准入](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L105-L120>) 仅在初建或退休水位实际推进时发布元数据；仍在删过期回执之前持久化水位。无过期回执的 40 次 fresh 请求原先反复覆盖同一 `long.MinValue` 图像，这个多余失败点现已移除。没有重试、扩大共享、修改根租约或套用 SnapshotFile。 |
| DATA — 19 项最终新增 | [真实 I/O 组](<tests/SystemAutorun.IntegrationTests/AgentOperationJournalPublicationTests.cs>) 覆盖首次初始化、只读不初始化；三种共享下各 40 次准入保持元数据字节与写入时间；两个 legacy 水位；缺失且有回执/坏 JSON/未知 schema 拒绝；水位发布失败零删除/零回调、成功后回退时钟不复活旧 intent；合成清理残留无需重发；四个时钟边界；三种回执故障保留 unknown、重启后不重执行。另含 8 个独占 root 各 40 次类型化配置变更，经真实管道、revision 游标与实际持久化验证。合成残留不是杀进程/断电证据。 |
| 诊断 — 范围受限 | [服务器异常上下文](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L338-L371>) 保留原类型/HResult/栈，附加阶段、目标和临时路径到 `Exception.Data`，不改 RPC 投影。测试 [限定目标的 Restart Manager 查询](<tests/SystemAutorun.IntegrationTests/JournalFileHolderProbe.cs>) 在已知持有句柄的实验中成功找到本测试进程，并验证结束会话成功；没有关闭/重启任何应用或更改 ACL。它只证明控制实验中的持有者，不指认 R26 的未知持有者。 |
| 静态与设计复核 | 调用图未发现其他应用模块读写 metadata；根租约等待关闭其流后才返回。该结论不覆盖进程外访问，未采纳“冲突必定完全内生于 journal”的推断。另一次纯算法复核未找到跳过已落盘相同水位破坏 at-most-once 的反例，但未读文件/执行；不当作独立运行验收。R26 静态报告已收回，其“其他记录字节不变”也仅按字段语义保留更正。 |
| ENG — 已通过部分 | Debug/Release 整解决方案均 **0 警告/错误**，Core 各 **391/391**。Debug 广域连续两轮各 **618/618**。此前最终组扩展到边界/残留前独立运行 **14/14**；最终 19 项全部包含在两配置广域及后续 Release 对照中。全部 **0 跳过**。未运行全量集成、发行包、真实系统启动项、UAC 或用户工作负载。 |
| ENG — 保留的 Release 失败 | Release 广域两轮均为 **617 通过/1 失败**，同一 [真实 COM 快捷方式用例](<tests/SystemAutorun.IntegrationTests/StartupInventoryReaderTests.cs#L69-L93>) 报 `InvalidComObjectException`。一轮在 [清单读取](<src/SystemAutorun.Windows/Startup/StartupManager.Inventory.cs#L92-L108>)，另一轮在夹具创建，均为“RCW 已与底层分离/另一线程仍在使用”。本轮未改 COM 代码，未用 catch 或串行化测试隐藏它；但尚不能断言与新增诊断的并发时序无关。 |
| DATA — 有界对照与下一步 | 同一 Release 产物依次运行：排除新 journal 组的原广域 **599/599**、只跑清单组 **13/13**、新 journal + 清单 **32/32**。这些通过不抹去先前两次失败，也不足以确定 RCW 触发者。当时将跨 STA/dynamic 绑定与平衡释放的真实生命周期列为下一切片；后续 [R28 边界](<docs/architecture.md#2026-11-02--固定-com-调用与-clr-生命周期r28>) 已修正策略并补回归，不把本行历史通过当作定位证据。 |
| VIS/FLOW 与整体范围 | 该修复不改 GUI；本切片 G2/VIS 不适用，未拿自动化测试冒充 r2 实装截图或键鼠验收。原生准备/重建、完整 ST03–ST08、GUI/CLI parity 与原目标仍未完成。 |
| 清理 — 已实查 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 均与轮前基线一致。项目/测试进程、构建服务器与私有 Mutation 注册表根均 **0**。只删除核实为空、非重解析且路径精确匹配的共享测试父目录，其他 SystemAutorun 临时根为 **0**。既有 scratch/fatal 和未提交工作保留，未发布、迁移或提交。 |

## 2026-11-02 — 共享处置编码与账本守卫（R26）

本轮把原记录的 disabled/restored/rebuilt 处置编码及有界账本守卫接入现有后端，沿用已批准
G1/G2-startup-r2。[契约与边界](<docs/architecture.md#2026-11-02--共享处置编码与有界账本守卫r26>)
明确区分“数据形状有效”和“原记录可信”；没有实现真实重建、完整原生准备或 r2 窗口。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 共享原记录 | [不可变模型](<src/SystemAutorun.Core/Models/StartupBackupRecord.cs>) 和 [有界编解码](<src/SystemAutorun.Core/Models/StartupBackupLedger.cs>) 保留老记录的缺失/null 处置与 kind 兼容，拒绝未知处置、重复 ID/字段、不认识的字段和新旧时间/处置矛盾。重建历史必须单独带时间与输入摘要，不得同时伪记恢复；摘要仍是不可信数据，不是新写入请求。 |
| 实现 — 读/写准入 | [快照](<src/SystemAutorun.Windows/Startup/StartupManager.Inventory.cs>) 与 [原生后端](<src/SystemAutorun.Windows/Startup/StartupManager.cs>) 共用固定 root 的有界钉扎读取。只读报告 partial/truncated/source issue；变更要求完整账本。恢复只接受 disabled；restored/rebuilt 不进入 pending。整个候选账本及 intent 在原生/意图效果前完成大小预检，避免先移除/创建原生项才发现账本无法再读。 |
| DATA — 36 项共享契约 | [Core 新组](<tests/SystemAutorun.Core.Tests/StartupBackupLedgerTests.cs>) 覆盖旧/新三态、孤立/矛盾字段、未知/忽略属性、大小写重复字段与 ID、嵌套摘要重复、错误根类型、kind 与原文、记录/字节上限。它不打开任何元数据路径，不证明归档真实性或允许重建。 |
| DATA — 18 项 owned 适配器 | [新原生组](<tests/SystemAutorun.IntegrationTests/StartupLedgerDispositionTests.cs>) 在私有 HKCU 映射中实际禁用/恢复并保留 ExpandString 和原字段；注入 rebuilt 历史验证不列 pending、不接受 restore，且另一合法变更完整保留该摘要。注入的历史不是实际重建成功证据。坏/超大/重解析/目录占位/超量账本均在 intent 与可写原生打开之前拒绝，字节保持不变；禁用和恢复候选溢出也保持原生状态不变。未碰系统 Run/Startup、未启动目标。 |
| 回归夹具修正 | 先前两个用例把发布目录障碍预先放在输入路径，现被新守卫更早拒绝，首轮启动组 **346/348**。将障碍准确注入 [实际 LedgerPublication 调用](<tests/SystemAutorun.IntegrationTests/StartupMutationAdapterTests.cs>) 后，仍验证发布 unknown 保留 native/intent、禁止盲补偿；另新增“目录占位零原生效果”用例，而非放松产品守卫。随后启动组 **349/349**，最终新增恢复容量用例后的处置组 **18/18**。 |
| ENG — 双配置与已通过部分 | Debug/Release 整解决方案均 **0 警告/错误**，Core 各 **391/391**。沿 R13 的广域过滤：Debug 首轮 **599/599**；Release 连续两轮各 **599/599**。全部 **0 跳过**。新组均包含在两配置验证中；这不是全量集成、真实 HKLM/UAC、崩溃恢复、跨 root 或发行包证明。 |
| ENG — 保留的一次失败 | Debug 广域第二轮 **598 通过/1 失败**：[40 次串行配置客户端](<tests/SystemAutorun.IntegrationTests/AgentConfigurationClientTests.cs#L11-L28>) 报 Access denied。本次取得服务端栈，位于 [OperationJournal 元数据发布](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L105-L109>) 的 [File.Move](<src/SystemAutorun.Core/Ipc/OperationJournal.cs#L324-L352>)，在新 receipt 与命令执行前；不是启动项账本调用栈。R20 记录过同用例症状，但本次仍未定位造成拒绝的实际句柄/权限来源，未改 journal、未归因于 R26，也未用 Release 通过冒充修复。广域不能称连续全绿，下一切片优先定位此持久化问题。 |
| VIS/FLOW 与安全剩余项 | 纯后端切片的 G2/VIS 不变更；未运行 r2 截图或键鼠旅程。没有重建方法/原生 CLI，没有把 metadataDigest、处置字段或历史摘要当成 native 身份/授权；可编辑输入信任、真实叶子身份/替换、非协作账本 CAS、跨 root 和持久对账仍未闭合。ST03–ST08 与整体目标未完成。 |
| 清理 — 已实查 | [主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>) SHA256 逐一匹配轮前基线。项目/测试进程、私有 Mutation 测试注册表根与构建服务器均 **0**；仅删除核实为空、非重解析且路径精确匹配的共享测试父目录，其他临时根为 **0**。既有 scratch/fatal、生产数据和所有未提交工作保留，未提交、publish 或迁移。 |

后续收回的只读代码复核在所查范围内未建立具体新回归，但未构建、测试或运行，不计独立执行
验收。其“其他记录字节不变”表述不成立：本实现保留已知字段语义，重新序列化仍可改变字节。
主线已复核完整候选预检、不可变副本、完整读取与处置拒绝的引用；未据此关闭真实身份/信任等
既有缺口。后续优先定位配置操作日志的元数据发布失败，不提前开放完整原生写入。

## 2026-11-02 — 只读启动项元数据复核（R25）

本轮交付共享 `startup inspect` 的元数据复核与拒绝原因，不是完整原生准备，也未实现 r2
窗口或开放原生新写 CLI。[契约与限制](<docs/architecture.md#2026-11-02--只读元数据复核与-startup-inspectr25>)
继续沿已批准 G1/G2-startup-r2；没有重画或改变批准基线。

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 共享复核 | [Core 契约](<src/SystemAutorun.Core/Models/StartupInspection.cs>)、[Agent](<src/SystemAutorun.Agent/AgentStartupService.Inspection.cs>)、[类型化客户端](<src/SystemAutorun.Core/Ipc/AgentStartupClient.Inspection.cs>) 与 CLI 贯通。输入只传 source/name/显式 isDisabled/metadataDigest，要求列表配置 revision；不接收目标/任意路径，不接收 --yes/--operation-id，不进入 mutation journal。返回固定逐动作原因，原生写权/写入/执行均明确为 false，禁用记录的原备份状态为 not_checked 而非缺失/可恢复推断。 |
| DATA — 真实 owned 原生读取 | [3 组原生用例](<tests/SystemAutorun.IntegrationTests/StartupInspectionNativeDataTests.cs>) 用私有 HKCU 映射 + 当前 Agent root + 真实 Pipe，覆盖 String/ExpandString/Binary。配置/账本归服务端 root，不读系统 Run/Startup；检查未增加可写打开、备份或变更操作，不执行目标。另明确验证：仅改注册表 kind 而可见字段不变时摘要仍相同，且仍无 native 授权；随后真实改命令，旧引用得到 startup_entry_changed。 |
| DATA — Pipe 与 Console | [控制面组](<tests/SystemAutorun.IntegrationTests/StartupInspectionControlPlaneTests.cs>) 将注入的固定 reader 接到真实 AgentServer/Pipe，验证前/中 revision 漂移、元数据漂移/消失、截断/重复/选中来源失败、读权限/IO 错误、共用扫描槽及取消。Console 子进程真实读取独占文件/stdin 并返回 envelope；其源 reader 为测试替身，不冒充生产原生端到端。大于请求上限的显示元数据以小引用复核；重复检查不改文件、配置任务或执行。 |
| DATA — 坏回复与错误不伪成功 | [恶意回复组](<tests/SystemAutorun.IntegrationTests/StartupInspectionProtocolTests.cs>) 经真实 Named Pipe 把伪 native 授权、动作、遗漏标记、原备份与 revision 送入类型化客户端及进程内 CLI，均 protocol_mismatch/退出 3。Core 另覆盖完整字段、摘要、时间、动作与标记矩阵；缺状态/必需字段不默认成功，旧列表可缺摘要但不能谎报摘要。CLI 检查取消返回明确 cancelled/退出 6，不返回空成功。 |
| 回归修复 — COM 引用 | 首次广域运行 **579 通过/1 失败**，暴露跨线程 InvalidComObjectException。新增 [第二次真实获取回归](<tests/SystemAutorun.IntegrationTests/StartupComLifetimeTests.cs>) 在旧 FinalRelease 上 **0/1 通过**，稳定证明仍被另一获取持有的 RCW 被作废。改为对等释放本次获取，Startup、注册状态和测试创建器共用规则；定向组随后 **52/52**。没有禁并行或仅重跑掩盖。该计数策略仅覆盖二次 COM 导入；R28 已用固定成员调用及 CLR 生命周期取代，不能把此行当作当前独占所有权证明。 |
| ENG — 最终双配置 | Debug/Release 整解决方案各 **0 警告/错误**；Core 各 **355/355**；沿 R13 的广域过滤集成每配置连续两次均 **581/581**；inspection 组另独立复跑各 **49/49**。全部 **0 跳过**。构建 --no-restore，测试 --no-build --no-restore；不是全量集成、真实 HKLM/UAC、跨 root 原子性或崩溃恢复证明。 |
| VIS/FLOW — 窗口未运行 | 本后台/CLI 切片没有窗口或布局改动，G2 沿用既有批准。CLI 文件/stdin、错误、取消有真实链路证据，但未运行 r2 窗口截图和键鼠旅程，不能抵扣 GUI 的 VIS/FLOW，不能称双端操作准备、重建或完整 parity 完成。 |
| 安全边界与剩余项 | schema 1 复核只覆盖显示元数据，不包含注册表 kind、归档字节/身份或原记录处置，不认证 native 输入，不签发提交凭据。adopt=reviewable 只允许进入配置纳管审核，不是提交授权。完整原生准备、信任/身份、叶子替换、跨 root 协作、原记录处置与持久对账仍未闭合；ST03–ST08 与整体目标未完成。 |
| 清理 — 已实查 | 三份生产配置逐一对比 SHA256 均不变：[主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[偏好](<autorun.gui.settings.json>)。项目/测试进程、私有测试注册表根、构建服务器均 **0**；只删除核实为空且非重解析的共享测试父目录，SystemAutorun 临时目录最终 **0**。未停止用户/未知进程，未删除既有 scratch/fatal，未提交、发布或生产迁移。 |

现有未提交工作保留。下一步落实可信原记录/处置与受控准备，再接原生后端及 GUI/CLI；
只读摘要不可复用为 native 身份或写许可，批准的主屏 r4、G2-startup-r2 和补充契约不变。

## 2026-11-02 — 共享启动项只读数据切片（R24）

本轮落实获批 G1/G2-startup-r2 的数据基础，不开放原生新写 CLI，也不把后台回归算作 r2
界面、操作准备、独立重建或完整 ST01–ST08 验收。契约见 [R24 架构增量](<docs/architecture.md#2026-11-02--共享启动项元数据读取r24>)。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 先复现 | 首组 21 项真实 owned 夹具在旧实现上 **20 失败、1 通过**。确认 Binary/None/MultiString/DWord/QWord 被 ToString 变成假命令，来源拒读令整次读取抛错，坏账本处理与来源计数丢失，备份记录按目标/历史路径存在性筛掉。 |
| 实现 — 共用算法 | [兼容快照/变更前复读](<src/SystemAutorun.Windows/Startup/StartupManager.cs>) 与 Agent 共用 [有界元数据读取器](<src/SystemAutorun.Windows/Startup/StartupManager.Inventory.cs>)，删除重复旧扫描、按存在性拆命令/筛备份的辅助路径。保留原生备份/恢复、kind/raw 保真和失败阶段守卫；不是 GUI 已迁移共享 IPC，也不是写前元数据等于原生 CAS。 |
| DATA — 类型与坏输入 | [最终 32 项快照用例](<tests/SystemAutorun.IntegrationTests/StartupSnapshotDataTests.cs>) 在真实私有注册表与临时目录逐字段比对两入口，涵盖五类非文本值、String/ExpandString 的字面 %TEMP%、不闭合/嵌入/尾接引号、未引用空格路径（含实际文件存在）、超长文本/字段、513 项源上限与取消。条目错误不成为猜测目标，输出只读/未纳管标记；没有启动目标。 |
| DATA — 来源与历史 | 单源拒读保留其他元数据；全部失败保留零可读来源和六条来源问题，不伪装可靠空列表。坏 JSON/null/超大/重解析账本与未决事务均保留来源错误，未恢复/删除/改写输入。原备份缺失时，不论目标存在与否，记录均只是只读元数据；外来/旧 profile 记录不改掉当前真实登记项。原跨 profile 用例已移到 owned 夹具，不把读取真实系统 Startup 当隔离证据。 |
| ENG — 最终双配置 | Debug / Release 整解决方案各 **0 警告/错误**；Core 各 **297/297**，沿 R13 的广域过滤集成各 **531/531**；新增快照组另外独立复跑各 **32/32**。全部 **0 跳过**。运行使用 --no-restore，测试使用最终构建的 --no-build；不是全量集成、真实 HKLM/UAC、跨 root 原子性或崩溃恢复证明。 |
| VIS/FLOW — 未运行 | 没有改窗口布局、启动 r2 实装窗口或跑键鼠旅程。旧 GUI 对 Error/CanModify/来源问题/缓存的完整呈现和动作门控仍待接线；ST01/ST02 只取得后端 DATA/ENG 子集证据，不能说双端旅程完成。 |
| 安全边界 | 只读字段形状/条数/文件大小限制不认证配置、账本或链接字节，不验证目标可用性或当前可恢复性。原生写入账本的完整输入/权限/身份、叶子替换窗口、跨 root 协作、准备/确认、重建处置与持久对账仍未闭合，新原生写 CLI 继续关闭。 |
| 清理 — 已核对 | 三份生产配置 SHA256 与轮前相同：[主配置](<autorun.yaml>)、[覆盖](<autorun.overrides.json>)、[GUI 偏好](<autorun.gui.settings.json>)。仓库/owned 测试进程、私有测试注册表根均 **0**；仅删除核实为空且非重解析的测试共享父目录，最终 SystemAutorun 临时目录 **0**，构建服务器检查也无残留。没有停止用户/未知进程，未删除既有 scratch 或 fatal 日志。 |

原未提交工作保留，未提交、发布或生产迁移。下一步建立共享操作准备/拒绝原因与结果契约，
再接受控后端、历史处置及 GUI/CLI；原主屏 r4 与已批准 G2-r2 的图像/补充契约均不变。

## 2026-11-01–02 — 启动项 G1/G2 批准与设计验证（R23）

本轮完成流程/设计阶段推进，没有改运行代码、上线或开放原生写 CLI。

| 类别 | 实际结果与边界 |
| --- | --- |
| G1 — 已批准 | 用户明确选择“批准，继续效果图”，批准 [G1-startup-r2](<docs/architecture.md#2026-11-01--启动项观测与独立重建-g1-增量提案r23>)：只读观测不探测目标、非文本只读、原备份恢复与独立重建分离、目标/参数/工作目录可修正但来源/名称固定，首版限本地 .exe/.com；成功须记已重建，不伪记已恢复。ST01–ST08 均为拟验收，不是已执行。 |
| G2 — 已批准 | 先纯文字生成 [r1](<docs/visual/sa-20261031/startup-rebuild-g2-candidate-r1.png>)，发现越界颜色/重复说明/错误来源，用户未批准并要求整体沿原参考。用户确认 r4 原图并允许本轮用作唯一图像输入后生成 [r2](<docs/visual/sa-20261031/startup-rebuild-g2-candidate-r2.png>)；已看像素，框架/密度/灰阶回归原参考，无服务曲线/虚构遥测，没有上传其他截图或生产资料。用户于 2026-11-02 明确选择“批准，按 r2 实施”，批准图像、补充契约及实施边界。 |
| 产物验证 | r1 为 1536×1024 PNG、1,407,983 字节；r2 为 1978×795 PNG、1,298,998 字节。两者均经 Pillow 验证真实 PNG 并完整解码，项目副本与原输出 SHA256 一致。源图/旧批准图及两轮候选均保留。[r1 输入](<docs/visual/sa-20261031/startup-rebuild-g2-r1.prompt.md>)、[r2 参考身份与输入](<docs/visual/sa-20261031/startup-rebuild-g2-r2.prompt.md>)、[已批准补充契约/实施边界](<docs/design-system.md#2026-11-01--沿原参考的启动项-g2-候选-r2r23>) 绑定同一合成 ST-F1，可复查。 |
| 源码核实 — 未修复 | 旧 ScanRunKey 仍 ToString 非文本；旧 SplitCommand 和 PathExists 仍有目标/备份路径探测；旧 GUI 状态与动作仍忽略 Error/CanModify。现代 CaptureInventory 避免前两类做法不表示旧 GUI 已迁移。本轮只读核实，没有把计划修复写成通过。 |
| 工程/数据/交互 — 未运行 | 本轮仅更改审批/设计文档与新图片，没有重跑构建或单测，没有操作真实 Startup/Run/UAC、生产迁移或真实窗口。R22 的 297/500/161 是上一轮代码回归，不算本轮新流程验收；VIS/FLOW/DATA/ENG 均待相关实现后验证。 |

G1/G2 与执行范围已独立批准，当前尚未开始运行代码实现，不能凭审批或设计验证宣称业务实现、发布及总体验收完成。
独立重建写入、可信记录/链接身份、跨 root 协作、持久阶段/崩溃核查、完整 GUI/CLI parity 与
真实权限验收继续未完成；原未提交工作保留。当前仓库/owned 测试进程、SystemAutorun
临时目录和私有测试注册表根均 **0**，没有停止任何未知/用户进程。git diff --check 通过，
仅有原工作树的换行提示；图片是需保留的设计产物，不作为测试残留删除。

## 2026-11-01 — 启动项备份位置与目录租约候选（R22）

本切片先复现恢复路径缺陷，再提供位置约束的后端候选。**用户已在本轮两项问答确认**：
缺失原备份时另设明确确认的独立重建流程，恢复不能静默代替重建；改 backup_dir 后须
显式恢复原配置上下文或迁移。当前守卫覆盖恢复侧，独立重建的完整旅程/数据契约仍待
补齐确认与实现，不能以工程通过或原则选择替代全流程批准。目前不发布，不开放原生写
CLI；候选无新 UI 布局，G2/VIS 不用于后端工程，GUI 可恢复性与错误旅程仍待处理。契约见
[架构增量](<docs/architecture.md#2026-11-01--启动项备份位置约束与目录租约r22>)。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 先复现实际文件位移 | 只新增两个私有夹具：改账本 backup_path 指向同 root 无关二进制文件或另一 root 的 GUID.lnk。旧实现构建成功、两例均失败，文件确实被移进测试 Startup；不是只检查函数文本或模拟 File.Move，也未碰真实系统 Startup。 |
| 实现 — 位置候选 | [路径租约](<src/SystemAutorun.Windows/Startup/StartupShortcutPathLease.cs>) 先做普通本地绝对路径、非空 N GUID、源/名称/扩展绑定，再要求当前配置的 startup/{id}.lnk 精确匹配。拒绝相对/UNC/设备/ADS/点段/尾随符/保留名/百分号路径和归档嵌入任一 Startup。不是只允许 root 内目录，更不是配置或账本认证。 |
| DATA — 兼容与拒绝 | [边界夹具](<tests/SystemAutorun.IntegrationTests/StartupBackupBoundaryTests.cs>) 验证正常默认路径、root 内外自定义本地归档、Unicode/空格及嵌套目录的原链接字节往返；配置变更时原文件/账本不动，显式切回原上下文后可恢复。原文件缺失即使 target 存在也拒绝重建；不取重复匹配 ID 的首项、不接受非 disable action。恢复侧规则已获本轮 G1 增量确认，独立且明确确认的重建尚未提供。 |
| DATA — 目录命名空间 | 实际目录/文件符号链接均拒绝；初次准入后插入叶子重解析点、缺失父级被换成重解析点也在移动准备期拒绝，未向另一私有根创建更深目录。真实目录改名在租约中被阻止、方法返回后可改名，涵盖禁用/恢复和两端目录；删除原 Startup 目录后仍能在钉住祖先下逐级创建并恢复。不是链接文件句柄改名或叶子身份 CAS。 |
| 复核与阶段修正 | 只读安全复核指出目录准备不应被误分成 native unknown；已把 Ensure*Directory 与状态检查放在独立 ShortcutPreparation、Move 标记之前。新增四项准备失败/调用前取消测试：入口不动、无 pending intent、后续调用不自锁。R21 的原生/发布未知结果规则不放宽；创建的空父目录不伪称零副作用。主线复核并真实回归，未把一次静态评审当全局安全证明。 |
| ENG — 双配置 | 最终 Debug / Release 整解决方案各 **0 警告/错误**，Core 各 **297/297**，沿 R13 的广域集成过滤各 **500/500**、0 跳过；包括原生夹具 **161 项**（比 R21 新增 48）。两配置原生切片单独运行也各 **161/161**、0 跳过。新增权威路径派生断言：实际操作路径来自固定源而非可编辑父目录拼写，名称大小写不一致拒绝；不冒充真实大小写敏感卷验收。不是全量集成、真实 HKLM/UAC、跨卷故障或发布验收。 |
| 未闭合与待决 | 账本输入界限/来源权限、原生叶子字节真实性与外部替换窗口、跨 root 协调、持久阶段与崩溃恢复仍未闭合。旧 GUI GetSnapshot/PathExists 仍可能按未校验路径探测、列表可恢复性可能与严格恢复候选不同，非文本快照和完整日志/恢复/纳管旅程继续待验。不能把变异路径的 I/O 准入推及旧 GUI 枚举。R20 Access denied 仍无根因闭合证据。 |

双配置前后三份生产配置 SHA256 不变；最终仓库/owned 测试进程 **0**、私有测试注册表根
**0**、SystemAutorun 临时目录 **0**。仅核实并移除了空、非重解析的测试共享父目录；没有
停用户进程。原未提交工作保留，未提交/发布；R16 候选不含 R21/R22，App/Helper 仍须成套
更新。独立重建的完整流程确认与实现、GUI/VIS/FLOW、严格原生写控制面及总体验收继续未完成。

## 2026-11-01 — 启动项失败阶段与提交后清理修复（R21）

本切片沿 G1-r1 的 unknown 不盲重放约束修复 Windows 后端，没有新增 GUI/CLI/UAC。
G2/VIS 为 N/A；GUI 错误旅程、全局原生事务与产品总体验收仍未完成。
边界见[架构增量](<docs/architecture.md#2026-11-01--启动项失败阶段与未决意图r21>)。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 先复现真实错误 | 只添加两个 owned 私有注册表测试，用不共享删除的真实意图文件句柄阻止提交后清理。旧实现构建成功，但禁用/恢复各失败一例：账本已提交，禁用却把原值写回、恢复却把已恢复值删除。不是模拟 UI 或对全局 Run 的操作。 |
| 实现 — 阶段拆分 | [StartupManager](<src/SystemAutorun.Windows/Startup/StartupManager.cs>) 分开意图发布、原生调用、账本发布与清理。调用或发布已开始但抛错时保留可能存在的意图、不盲目补偿；只在已确认 native 返回且账本发布尚未调用的失败中尝试补偿。账本已提交后的清理失败不再回滚 native。没有按错误码 5 猜测未生效或增加重试。 |
| 实现 — 原因与结果 | [需核查异常](<src/SystemAutorun.Windows/Startup/StartupMutationRecoveryRequiredException.cs>) 携带 operation/backupId/stage/CommitConfirmed，保留原异常及 AggregateException 双因；不把 cleanup 失败覆盖主因。CommitConfirmed 只是观察到发布返回，不代表断电持久或外部状态。删除可能已执行后抛错，所以不承诺意图必在。未新增公开 Pipe 错误码。 |
| DATA — 实际提交后失败 | 两个真实句柄复现用例修复后均通过：确认新账本与对应 native 状态仍相符，返回 intent_cleanup、CommitConfirmed=true，事务仍在且下次变更拒绝。受控前/后删除故障还验证事务已不存在的情况不会回滚或被伪称仍在。 |
| DATA — 副作用故障边界 | [故障阶段回归](<tests/SystemAutorun.IntegrationTests/StartupMutationFailureStageTests.cs>) 在真实 owned 注册表/链接 Action 前后受控抛错：覆盖两方向、链接/注册表、意图/账本发布、固定 key 创建及外来值漂移。验证 unknown 时不补偿、不记成功、不丢意图、新 Manager 也拒绝；模拟发生在边界，不冒充 OS 真在副作用后报错、跨卷部分移动或崩溃复现。 |
| DATA — 补偿与取消 | 发布前取消测试保留 String/ExpandString 与原链接字节，账本不变、无孤儿链接；回滚失败保留意图及主因+回滚因。快捷方式状态已变化/源消失时不再“成功无操作”；主因+候选清理失败不再互相掩盖。发布确认后的晚到取消仍返回已提交成功，不伪报未执行。 |
| 测试契约修正 | R19/R20 原用例将 ledger File.Move 失败视为必定可回滚，现明确改为发布阶段 unknown。原类型/外来值补偿断言移到可证明发布尚未开始的取消路径，未删除保护断言；原生调用前取消改验没有 native Action，而不是先删除再补偿。 |
| ENG — 实际回归 | 首轮针对性 **109/109**；再补四项链接/晚到取消用例后，最终 Debug / Release 整解决方案各 **0 警告/错误**，Core 各 **297/297**，沿 R13 的广域集成过滤各 **452/452**、0 跳过，其中原生夹具 **113 项**（比 R20 新增 35），随后两配置分别单独复跑也各 **113/113**。不是全量集成、GUI/UAC、崩溃恢复、跨 root 原子性或发布验收。 |
| 契约与入口 | [内部操作执行器](<src/SystemAutorun.Windows/Startup/StartupMutationOperations.cs>) 的生产默认只调用真实 Action 一次，注入不在 public 构造/配置/RPC；测试不替换真实 File/Registry/WSH 效果。阶段只在进程内，事务 JSON 未增持久阶段，不提供自动清理/恢复。另更正 [README](<README.md>) 中 R18 adopt 仍未实现的过时文案，没有因此开放原生写 CLI。 |
| 未闭合项 | 阶段修复不解决可编辑 backup_path/输入信任、父链/链接身份钉扎、外部竞态、跨 root 协调、崩溃对账或严格 legacy 类型准入。旧 GUI 非文本快照呈现与日志/恢复/纳管旅程仍待验。R20 配置 Access denied 本轮未再现，不代表已找根因；原记录保留。 |

最终双配置回归前后三份生产配置 SHA256 相等；本轮不写真实 Run、Startup 或任务计划，
不重新 publish。R16 候选不含 R21，App/Helper 仍须成套更新。原未提交工作保留。
阶段函数与需核查异常的只读安全复核未提出必修项；主线自行复核并实际构建/测试，
不把该有限静态复核当作全局安全证明。
最终仓库/owned 测试进程 **0**、私有测试注册表根 **0**、SystemAutorun 临时目录 **0**；
仅核实并移除了测试创建的空、非重解析共享父目录，没有停任何用户进程。git diff --check
通过，只有原工作树换行提示。GUI/VIS/FLOW、真实机器权限、全局事务和总体验收继续未完成。

## 2026-11-01 — 注册表启动项类型保真与关联诊断（R20）

本切片按 G1-r1 接续 R19，只修原生数据正确性，并补真实回归失败所需的进程内诊断。
没有新增 GUI/CLI/UAC 入口，G2/VIS 为 N/A；没有完成 GUI 旅程或原生写控制面。
契约见[架构增量](<docs/architecture.md#2026-11-01--注册表启动项类型保真与状态复核r20>)。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 先复现旧缺陷 | 修改类型逻辑前，仅新增三个 ExpandString 往返夹具，在 HKCU 私有键中分别代表 HKCU/HKLM64/HKLM32。构建成功，三例恢复均失败：Expected ExpandString、Actual String；没有写真实 Run 键。 |
| 实现 — 类型和原文 | [StartupManager](<src/SystemAutorun.Windows/Startup/StartupManager.cs>) 在新账本/事务内记录可空 `registry_value_kind`，只允许 String/ExpandString；在意图前只读捕获原类型及未展开原文，恢复和禁用回滚显式保留类型，不 trim、展开或依据 `%` 猜类型。typed null 原文、非文本/未知类型及坏类型 JSON 均拒绝。 |
| 实现 — 变化和回滚 | 紧邻删除前再比较 kind + 原文；回滚遇外来异类型或异文本值不覆盖/删除，而保留未决事务。内部存在检查修正为 Windows 值名的大小写不敏感语义；固定 key 整体被外部删除后，恢复沿用只重建该固定 key/本值的旧语义，未承诺恢复其他被删值。 |
| DATA — 原生夹具 | [原生回归](<tests/SystemAutorun.IntegrationTests/StartupMutationAdapterTests.cs>) 从 R19 的 32 项增至 **78 项**（本轮新增 46），最终双配置广域回归均包括且通过全部 78 项。覆盖三源类型往返、空/空白/Unicode/字面变量原文、五类非文本值写前拒绝、四种删除边界漂移、坏/未知/legacy 元数据、两类型提交失败回滚、双方向外来 kind/raw 变化保留事务、大小写重建及整键被删后的恢复。机器来源仍是私有 HKCU 映射，不冒充真实 HKLM 权限验收。 |
| 兼容边界 | 缺失/null 类型字段的旧备份仍按原来的 REG_SZ 规则读取，不是原类型证据；旧 null 原文仍为空串兼容。新记录不能产生 typed null 原文，快捷方式不写空类型字段。旧二进制忽略新字段会再次丢类型，App/Helper 必须一起更新；本轮没有迁移旧备份或公开新协议。 |
| ENG — 实际失败未抹平 | 首轮双配置回归：两次构建 0 警告/错误，Core 各 297；Debug 集成 **415/415**，Release **414/415**。唯一失败是配置客户端 40 次串行 patch 用例在 WhenAll 得到裸 Access denied，当次没有原服务端栈。未将它归因于类型改动、R14 或 journal 的 File.Move，也未当作已修复。 |
| 实现/DATA — 关联诊断 | [Dispatcher](<src/SystemAutorun.Agent/AgentCommandDispatcher.cs>) 增加默认关闭的进程内原异常观察回调，不改 Pipe 错误投影、不传栈/payload、不改权限、不重试。[夹具](<tests/SystemAutorun.IntegrationTests/ConfigurationControlPlaneTests.cs>) 按独占 root 保存有界原异常，[游标用例](<tests/SystemAutorun.IntegrationTests/AgentConfigurationClientTests.cs>) 失败时输出命令/requestId/operationId/原栈并原样抛错。两项确定故障测试均通过，分别覆盖业务执行和 journal 准入捕获、观察器抛错隔离及 wire 无原栈；它们不冒充原偶发错误的复现。 |
| ENG — 有界复查 | 加诊断后，Release 两项诊断回归 **2/2**；同一游标用例在六个全新独占 root/进程内服务实例各通过一次（共 240 次真实 owned 配置 patch）。没有对同一未知业务意图换 ID 重试；全部通过不能证明之前的拒绝访问因果闭环。 |
| ENG — 最终回归 | 最终 Debug / Release 整解决方案各 **0 警告/错误**；Core 各 **297/297**，沿 R13 的广域集成过滤各 **417/417**，0 跳过。git diff --check 通过，仅有原工作树 CRLF/LF 提示。没有跑全量集成、VIS/FLOW/UAC、真实启动、跨 root 原子性、崩溃或发布验收。 |
| 复核与留项 | 安全顾问只读对照代码/测试意图，主线实际修改并执行上述验证；内部大小写不一致和固定 key 重建覆盖已补。旧 GUI 快照对非文本值 ToString/可修改状态的呈现问题明确留项，写入口会拒绝但不说 UI 已修；备份输入/路径信任、跨 root 协调和 native 未知阶段仍未解决。 |
| 清理与保留 | 最终仓库/owned 测试进程 **0**、SystemAutorun 前缀临时根 **0**、私有测试注册表根 **0**。额外发现一份 8 月遗留、仅含 PID 的私有夹具；核对固定 GUID 路径/唯一内容和该 PID 已不存在后，只移除该夹具及空共享父目录，没有按旧 PID 停进程。最终双配置回归前后三份生产配置 SHA256 相等，真实 Run/Startup/任务计划未写入，原未提交工作保留。无新 publish，R16 候选不含 R20。 |

下一步收敛备份信任边界、原生结果确定/未知阶段及全局协调，再开放 startup disable/
restore；后续复现配置 I/O 拒绝时必须取本地关联栈，而不是仅重跑宣称恢复。self enable/
disable、boot install/uninstall、旧 GUI 纳管/日志/恢复接线、R12 VIS/完整 FLOW、真实远端
成功以及 SA01–SA10 总体验收仍未完成，本轮没有标记总目标完成。

## 2026-11-01 — 启动项原生夹具与事务租约修复（R19）

本切片按 G1-r1 接续原生变更前置能力，并修复旧的异步锁失败；只改 Windows 后端与
测试，不新增 GUI/CLI/UAC 入口或数据格式，G2/VIS 为 N/A。契约见[架构增量](<docs/architecture.md#2026-11-01--启动项系统访问边界与异步事务租约r19>)。
**未开放 startup disable/restore，也未验证 GUI/UAC 完整旅程或全局原生并发安全。**

| 类别 | 实际结果与边界 |
| --- | --- |
| 实现 — 内部访问边界 | [StartupNativeAccess](<src/SystemAutorun.Windows/Startup/StartupNativeAccess.cs>) 将管理员角色、两个 Startup 目录和三个 Run 来源集中映射。公共构造仍只选固定生产实现；测试注入为 internal + 既有 friend 程序集，配置/RPC 不接受自定义系统位置。旧变更、回滚与新只读清单共用映射，没有替换实际 File.Move、RegistryKey 或 WSH 行为。 |
| DATA — 修复前真实失败 | 仅完成访问边界后，三个独占 HKCU Software 夹具分别代表 HKCU/HKLM64/HKLM32。实际禁用写入完成，但三例恢复准入全部抛 **AbandonedMutexException**，堆栈来自 AcquireMutationLock.WaitOne；原 Mutex 跨 await/ConfigureAwait(false) 使用，确有线程亲和问题，不是单靠书面推断。 |
| 实现 — 独立文件租约 | [StartupMutationLease](<src/SystemAutorun.Windows/Startup/StartupMutationLease.cs>) 改为物理 root 内固定 FileShare.None 句柄，15 秒合作式准入、可取消、只对 32/33 等待；空锁文件保留。未放宽权限或吞掉真实 I/O。刻意不与配置 RootWriteLease 混锁，保留配置操作独立性；旧版本仍不参与新协议。 |
| DATA — 32 项原生回归 | [新增回归](<tests/SystemAutorun.IntegrationTests/StartupMutationAdapterTests.cs>) **32/32**：三 Run 逻辑来源的真实私有注册表 String 值禁用/恢复；用户/公共 Startup 逻辑来源的真实 owned WSH link 字节移动/恢复；模拟非管理员角色拒绝、缺项/错 origin/未决事务、恢复冲突不覆盖新条目、固定生产映射和 internal 注入契约。机器来源仍映射 HKCU 私有键，不冒充真实 HKLM 或非管理员 OS token 验收。 |
| DATA — 实际失败与回滚 | 用 ledger 叶子为目录、以及不共享删除的真实 ledger 句柄，分别触发禁用/恢复完成 native 后提交失败，验证注册表值/链接字节回滚和原 ledger 保留。另注入 rollback 打开失败，验证事务仍保留、下一次操作拒绝；这是受控数据，不证明任意外部并发写者下的原子恢复。 |
| 夹具修正 | 文件租约接入后的首轮 **22 通过/4 失败**：故障夹具错误预期 IOException，Windows 实际在备份 File.Move 返回 **UnauthorizedAccessException**。只按真实平台结果修正四个断言，没有改产品异常或权限；再补六项契约/租约用例后 **32/32**。 |
| DATA — 租约生命周期 | 专属线程取得文件租约并退出后，其他获取仍超时；由另一个线程释放后可重取。同 root 目录符号链接别名争用同一锁、持有时不能删除锁文件、取消准入不作 native 写，native 边界取消会回滚；配置 lease 仍可取得。另明确验证**另一 root 能在本 root 持租约时修改同一个外部夹具**，防止把 root 串行化冒充全局保护。 |
| ENG — 最终构建与回归 | Debug / Release 整解决方案各 **0 警告/错误**；Core 各 **297/297**，沿 R13 的广域集成过滤各 **369/369**，0 跳过。Debug 另单独运行新增 **32/32**。未跑全量集成、GUI 像素/UAC、崩溃掉电、真实系统启动或发布验收。 |
| 复核 — 范围与未解风险 | 探索与安全顾问给出定点线索，主线亲读/修改实际代码并执行所有上述测试；不计独立全库安全审计。注册表类型保真、备份路径/父链信任、旧存在性探测、有界备份/元数据及全局原生协调仍未解决，不能据本轮包装旧方法开放 CLI。 |
| 清理与保留 | 最终仓库/测试进程 **0**、SystemAutorun 前缀临时根 **0**、私有 Mutation/Registration 注册表 fixture **0**；每个 WSH 夹具线程及 RCW 已收尾。最终双配置回归前三份生产配置 SHA256 与回归后相等，真实 Run/Startup/任务计划未写入，旧未提交工作保留。没有新 publish 或第二份大候选，R16 候选不含 R17–R19。 |

下一步先在本轮 owned 原生夹具上收敛 String/ExpandString 值类型和备份信任边界、增加
实时身份/状态校验，再定义跨 root 合作协调与 journal 确定/未知结果；不直接用配置
revision 授权全局 Windows 变更。startup disable/restore、self enable/disable、boot
install/uninstall、旧 GUI 纳管接线、GUI 日志/恢复、R12 VIS/完整 FLOW、真实远端成功和
SA01–SA10 总体仍未完成。历史 R14 因果边界亦保持原记录，不由本轮全部通过推定闭环。

## 2026-11-01 — startup adopt 配置提交与新鲜度检查（R18）

本切片接续 G1-r1，只补 Core / Agent / Console 的启动项纳管提交，不改 WPF、原生注册或
旧 GUI 表单接线，G2/VIS 为 N/A。它不是全量 startup parity、GUI 双端旅程或总体验收。
契约与明确边界见[架构增量](<docs/architecture.md#2026-11-01--启动项纳管提交-clir18>)。

| 类别 | 实际结果与边界 |
| --- | --- |
| 范围 — 显式审阅 | `startup adopt ENTRY_ID --file JSON --expected-revision SHA256 --yes` 接受完整 expectedEntry 与审阅后的 task-add 表单。CLI 文件/stdin、Agent 递归拒绝未知字段；确认不能由文件绕过。没有自动运行原始命令、原生禁用或隐式 UAC。Task 参数和路径仍由原 task-add 规则验证，不把全部任务路径校验说成无 I/O。 |
| DATA — 新鲜度与 CAS | [Agent](<src/SystemAutorun.Agent/AgentStartupService.cs>) 在配置修订检查之后重读所选来源，要求 ID 唯一及完整记录相等；截断、选中来源问题、条目消失/变化均不提交。最终复用原工作区 AddTaskAsync/root lease/CAS，不以配置 SHA256 冒充原生 CAS，不承诺发现外部编辑器 ABA。 |
| DATA — Core | [契约回归](<tests/SystemAutorun.Core.Tests/StartupAdoptionTests.cs>) 新增 **38 项**，覆盖五种固定来源、确认、禁用/已纳管/错误/缺目标等拒绝、长名称预算、坏成功回执为 unknown，以及只对本命令预检错误生效的 journal 分类；其他命令和提交 I/O 仍保持 unknown。原生 target 使用不可达 UNC 元数据验证纯词法请求检查，不运行 WSL。 |
| DATA — Pipe / Console | [控制面回归](<tests/SystemAutorun.IntegrationTests/StartupAdoptionControlPlaneTests.cs>) 新增 **28 项**，使用真实 Pipe、真实 owned 配置及 fail-fast 执行器；native 来源为 managed double。覆盖两 root 隔离、typed/CLI、文件/stdin、明确警告、重放不重扫、不同参数拒绝、九种条目变化、来源不完整、权限/不可用、扫描前/中配置冲突及严格原始 RPC；另实际启动独立 Console 子进程提交/重放。不是 GUI 或真实业务负载验收。 |
| 首轮发现与修复 | 首轮 focused 集成发现：journal 将新的“预检已拒绝、未到配置提交”错误统一升级成 outcome_unknown，CLI 因此错误返回 exit 6。修正为命令作用域内四种确定预检拒绝，并补正反范围测试；普通 permission/io/取消不被泛化为安全失败。修正后定向 **28/28**，完整回归见下行。 |
| DATA — 超时生命周期 | 新增受控 pending producer 回归：5 秒预检超时后未新增任务，槽位仍 busy、token WaitHandle 仍有效、ping/配置可读；producer 实际完成后才释放 CTS 和槽。修复同组 startup list 的 CTS 提前 Dispose，而非提前释放 native 工作或另开线程。此为合成 pending 任务，不冒充真实 COM 卡死。 |
| FLOW — 真实 native / Agent | 独占临时根由 Debug Console 显式启动生产 Agent **11272**，真实只读 HKCU Run 返回 **7** 条。选择完整真实条目作 expectedEntry，但将可编辑任务表单显式改为 owned 根内独占空 EXE、manual、expectedRunning=false。实际提交 **exit 0**、重读恰好一项；同 operation ID 回放原修订且不重复添加，旧配置修订的新意图 **exit 5 / revision_conflict**。这是审阅后改写表单的提交验证，不是接管或执行真实业务程序。 |
| FLOW — 原生与生命周期 | 所选原生条目提交前后完整观测相等，未观察到夹具进程。核对 Agent PID/image/start-time/instance 后正式 stop 并等待退出；再次 status **exit 6**，无隐式重启、无强停后备。每个 Console stdout 为单行 JSON、stderr 空；未将真实 RawCommand 输出到验收日志。 |
| ENG — 最终代码 | Debug / Release 整解决方案各 **0 警告/错误**，Core 各 **297/297**，沿 R13 的广域过滤集成各 **337/337**、0 跳过；不是全量集成。此前 Debug/Release 另各有 292/292 Core 与 337/337 集成通过；最终补齐五种来源映射用例后按两配置重新完整运行上述过滤。 |
| 复核与取舍 | 主线检查、实现并实际运行上述验收；安全顾问只审自包含文字方案，未读仓库或执行，不计独立仓库审计。成功字段收窄为本命令 nativeWritePerformed=false / executionRequested=false；`--yes` 和文本说明明确原生/其他 root/自动策略仍可启动，可能重复。本接口不建立跨 root 排他登记或所有权转移，不宣称 durable native 状态或 workloadStarted=false。 |
| 清理与保留 | Agent 正常退出、烟测 root 已删除；最终仓库/测试进程 **0**、SystemAutorun 前缀临时根 **0**、私有注册表 fixture **0**。真实旅程前后三份生产配置 SHA256 不变；没有写真实 Run/Startup/任务计划、修改权限、reset 或覆盖其他未提交工作。没有新 publish，R16 候选仍不含 R17/R18。 |

startup disable/restore、self enable/disable、boot install/uninstall 与旧 GUI 纳管接入仍待完成。
下一步若推进原生写入，先补可注入的系统访问边界和实时身份校验，在私有注册表/owned
目录测试，不以本轮 root CAS 或 native 元数据读取授权全局修改。GUI 日志/恢复、R12
VIS/完整 FLOW、真实远端成功、受保护部署与 SA01–SA10 总体继续未完成；R14 历史错误
仍无关联抛出栈，不能由本轮通过宣布其全部因果已闭环。

## 2026-11-01 — Windows 快照发布竞态修复（R17）

沿用 G1-r1 配置一致性的修复路径，仅调整后端文件 I/O；GUI 布局、交互、数据格式、
权限和操作确认均不变，G2/VIS 为 N/A。**确认并修复了可确定复现的共享句柄机制缺陷，
但没有取得 R14 历史失败的关联抛出栈，不能宣布其全部根因已闭环。**

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 修复前实证 | 原 `FileShare.Read` 策略下，持有生产读入口打开的句柄执行真实提交：Core **4 失败/4 通过**，缓存 **1 失败**，抛出点确为 `File.Move`，含真实偏好工作区保存栈。只补 `Read|Delete` 后 Core 仍 **4 失败/4 通过**；独立 Windows 对照证明 Move 对共享 5/7 均报 **Win32 5**，不能将加 Delete 标志误写为已修复。 |
| 实现 — 有界修复 | [快照入口](<src/SystemAutorun.Core/Infrastructure/SnapshotFile.cs>) 统一相关配置/缓存读取与普通候选发布。本地 NTFS 使用一次扩展 rename，旧句柄留旧字节、新打开者见新版本；候选先检查句柄属性。没有授予 Write 共享、放宽 ACL、改租约、吞掉真实权限错误或自动重发写意图。非本地 NTFS 保留旧发布路径，不保证同样的并发能力；完整文档备份交换不改用此助手。 |
| DATA — 原生句柄回归 | [Windows 回归](<tests/SystemAutorun.Core.Tests/WindowsSnapshotFileTests.cs>) **17 项**：保留旧快照的偏好/CAS、YAML 与覆盖文件真实业务写入，Move 5/7 负例、无 Delete 读者的真实 32、失败后保存队列恢复、禁止原地写、租约不可替换/重取、只读目标、候选源占用、精确同目录前置条件、Unicode/长路径、24 次保留旧句柄发布、BOM 与取消。没有 ACL 修改或真实 WSL 执行，全部在独占小根内运行。 |
| DATA — 展示缓存 | [缓存回归](<tests/SystemAutorun.IntegrationTests/GuiCacheSnapshotTests.cs>) **2 项**，直接调用生产 native/startup cache 服务，持旧句柄时保存并读取新信封，确认旧内容未变且无候选残留；不是 GUI 窗口或视觉验收。 |
| FLOW — 最终真实 Console | Debug Console 显式启动独占空任务根 Agent **13708**；外部受控 `Read|Delete` 句柄保持打开期间，经真实 Pipe 连续提交 **4 次**偏好，修订每次更新，扩展字段保留，旧句柄仍读最初字节。旧修订请求返回 **exit 5 / revision_conflict**，持久化状态不变。此受控长句柄不是假称 GUI 原始竞态再次发生。 |
| FLOW — 生命周期与夹具纠错 | 最终核对 Agent PID/path/start-time/instance 后正常 stop，等待退出，再查 **exit 6**，没有自动重启。首次旅程已完成 4 次提交，但烟测错误期望冲突 exit 3；核对真实 CLI 契约为 5 后重跑成功。首次只对已核验独占进程使用后备清理，**不计正常停机通过**，不是产品冲突处理缺陷。两轮三份生产配置 SHA256 均不变。 |
| ENG — 最终代码 | Debug/Release 整解决方案 **0 警告/错误**；Core 各 **259/259**。最终广域过滤 Debug 连续 **2×309/309**、Release **309/309**，全部 0 跳过；过滤仍沿用 R13，不是全量集成。此前较早版本另有两轮 Debug 309/309，不混算为最终代码结果。 |
| 复核与未覆盖 | 主线读实际源码、审差异并执行 Windows 原生/Console 验收；安全顾问只审提供的 API 方案，未读仓库或执行，不算独立仓库审计。没有断电、FAT/exFAT/SMB、恶意目录交换、真实正向远端或受保护安装测试，不声称这些通过。 |
| 清理与发行边界 | 最终 owned 前缀临时根及仓库/测试进程均检查清理；生产数据和其他未提交工作保留，无 reset。只更新正常 Debug/Release 输出，**没有重新 publish**；R16 候选仍是当轮冻结产物，不含本修复，正式交付前需重发并重验，未生成第二份大候选包。 |

发布策略及限制见[架构增量](<docs/architecture.md#2026-11-01--windows-快照读取与发布r17>)。
GUI 日志/恢复、VIS/完整 FLOW、剩余启动项变更 CLI 与 SA01–SA10 总体验收继续未完成；
本切片回归不能替代完整产品验收，也不删除 R14 的历史诊断证据。

## 2026-11-01 — CLI 自包含发行与实际包验收（R16）

本切片落实 G1-r1 的四入口发布与 Console/GUI PE 分工；不改页面、交互、系统注册或
安装位置，G2/VIS 为 N/A。本次是**本地发行候选验收**，不是全产品或干净机发布放行。

| 类别 | 实际结果与边界 |
| --- | --- |
| 发布 — 真正产物 | 正式[发布器](<scripts/publish-native.ps1>) 在 `--no-restore` 模式完成 App/CLI/Agent/Elevated 四个 Release 自包含 publish；保留[发行候选清单](<artifacts/publish/win-x64-r16/publish-manifest.json>)，包共 **877 文件 / 410.5 MiB**，其中清单覆盖另外 **876** 文件。旧默认发行目录没有作为本轮输出目标，也未安装至 Program Files。 |
| 发现与设计修正 | 首次实际发布被严格同名 SHA256 检查拒绝：[桌面 Microsoft.VisualBasic.dll](<artifacts/publish/win-x64-r16/Microsoft.VisualBasic.dll>) 与 [CLI 同名程序集](<artifacts/publish/win-x64-r16/cli/Microsoft.VisualBasic.dll>) 字节不同。没有改成覆盖或放宽冲突检查；CLI 改为独立 `cli` 运行时，Agent/Elevated 仍在 `runtime`，App 留包根。[共享解析器](<src/SystemAutorun.Core/Ipc/AgentDaemonLauncher.cs>) 新增固定相邻 runtime 候选；显式环境覆盖与旧开发布局继续兼容，不把解析当作二进制信任校验。 |
| DATA — 真实完整包 | [包烟测](<tests/PublishedCli.Smoke.ps1>) 重读 **876** 个文件的 SHA256/size，核对无重复/越界路径、实际文件全覆盖与四个真实 x64 PE32+ EXE。CLI 为 Console **3**，其余入口为 GUI **2**；不是只相信项目配置或清单自报。manifest 保持原 schema-1 字段形状，未添加 entry_points，且不自哈希。 |
| FLOW — 无源码覆盖的实际 Console | 最终旅程 Agent **6000**，前一轮 **28300**，均由候选 CLI 显式启动。子进程清除 Agent/root 环境覆盖、使用包外 CWD、无全局 runtime 指向；实际 Agent image 在候选 runtime，Console host trace 与 Agent native module 均指向各自包内 CLR。仍在已有 .NET 的开发机上运行，**不冒充无 .NET 干净机测试**。 |
| FLOW — 读取与停机 | 两轮均实际完成 daemon status、task list、config show、monitor status、logs sources、logs tail 六项读取，JSON stdout 单行、stderr 空；每次维护 Agent PID/path/start-time 身份。空任务根没有业务负载，未启动 App/Elevated。正常 stop 后等待退出，再查 **exit 6**，没有自动重开后台；fixture YAML 不变。CreateNoWindow 与无 Agent 主窗得到检查，不把它称为桌面 VIS 或任意瞬态窗口的完整跟踪。 |
| DATA — 发布失败/回滚 | [无 Pester 回归](<tests/PublishContract.Tests.ps1>) 最终 **82 断言**。覆盖 GUI/Console 正反、11 类坏头、DLL/non-executable、缺运行时、CLI WPF、framework-dependent、共享哈希冲突、模拟构建失败、旧 current/previous 保留、激活失败回移及 GUID stage 清理。组件是合成不可加载字节，dotnet/Move 在独立测试进程中替身，不声称这些模式是真实 CLR 或 OS 故障注入。 |
| ENG — 实际运行 | Debug/Release 整解决方案均 **0 警告/错误**；Core 两配置各 **242/242**；广域过滤 Debug 与 Release 各 **307/307**、0 跳过。沿用 R13 的过滤集，不是全量集成。正式发行 publish 成功与最终真实包旅程/82 断言另行运行，不由这些 managed 测试代替。 |
| 测试前置纠错 | 首次合成发布夹具的 PowerShell 数组拼接导致缺 Core 文件；前两次包旅程在版本读取后发现 trace 路径变量与 bool 参数大小写同名，尚未启动 Agent。均修正夹具并重跑，不将其冒充产品缺陷或成功业务旅程；测试根和错误 trace 文件随独占 root 清理。 |
| 复核边界 | 安全顾问仅依据主线提供的原始片段与计划，未调用工具，不算独立仓库审计。已补 EXE/DLL/optional-header 检查、固定表驱动 subsystem、预切换验证、现存 previous 失败测试及实际实例身份检查；其对未来 schema 消费者的提醒通过不新增 manifest 字段规避。没有新增发布锁或声称抗恶意同用户目录竞争。 |
| 清理与保留 | 最终本仓库/测试进程 **0**、published 临时根 **0**、合成发布 fixture **0**、staging 子目录 **0**；三份生产配置 SHA256 与轮前一致。只保留上述明确发行候选及正常构建输出，候选不是测试临时数据；旧 scratch、默认发行、既有日志及其他未提交工作保留，无 reset、权限变更或环境级设置写入。 |

复跑入口、目录布局和回滚语义见[部署说明](<docs/deploy.md>)与[发行契约](<docs/architecture.md#2026-11-01--cli-自包含发布与运行时隔离r16>)。
R14 的偏好 Access denied 本轮没有复现，根因仍未确认。GUI 日志/恢复、VIS/完整 FLOW、
启动项变更 CLI、无 .NET 干净机、真实远端成功与受保护安装/SYSTEM 启动验收仍未完成；
本地发行候选不能代替 SA01–SA10 总体验收或用户的最终放行。

## 2026-11-01 — self / boot 只读注册状态（R15）

本轮接续 G1-r1，增加 `startup self status` 与 `startup boot status` 的共享契约、native
读取、Agent RPC 和独立 Console 输出。不变更 GUI、自启注册或系统任务；本切片 G2/VIS
为 N/A。注册存在、当前 root、启用设置与工作负载存活严格分开，不代表全部 CLI parity。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW — 最终真实 Console | 独占空任务根经真实 `daemon start` 启动 Agent **15940**，self / boot 均 **exit 0 / available=true / registered=false / association=absent**，stdout 为单行 JSON；还实测普通文本摘要。此机没有这两个实际注册，不能据此声称已部署/已安装分支经过实机验收。 |
| FLOW — 生命周期与离线 | 真实 `daemon stop` ACK 后 Agent 正常退出，未用后备强停；再次 boot status **exit 6**，没有隐式拉起后台。fixture YAML 及最终烟测前后三份生产配置 SHA256 一致，无 UAC、启动项写入、任务执行或工作负载启停。 |
| DATA — Core | [共享契约回归](<tests/SystemAutorun.Core.Tests/StartupRegistrationTests.cs>) 新增 **32** 项，覆盖固定 scope/id、时间与布尔形状、失败不得带伪观测、必需运行计数/结果不得缺失、self enabled 未知、boot 不推断用户 root、严格多 root/UNC/控制格式字符处理、大小写不折叠命名空间及完整 UTF-8 字节预算。关联仅词法，不是信任或修改授权。 |
| DATA — 真实 Pipe | [控制面回归](<tests/SystemAutorun.IntegrationTests/StartupRegistrationControlPlaneTests.cs>) 新增 **13** 项，覆盖类型化读取/CLI、拒绝参数与写意图、不可用/权限错误映射、5 秒超时后的共享单槽、busy 时 ping 可用，以及实际 reader 完成前 CTS/WaitHandle 仍有效。挂起 provider 是可控合成任务，不是对真实 COM 服务制造卡死。 |
| DATA — 平台适配 | [平台回归](<tests/SystemAutorun.IntegrationTests/StartupRegistrationReaderTests.cs>) 新增 **16** 项。真实注册表测试仅创建 HKCU Software 下随机独占测试键，验证 REG_SZ/EXPAND_SZ 原文、无环境展开、非字符串与超大值；不写真实 Run 键。COM getter 集、单 action 条件、权限和 CLR/COM 缺失分类由公开 managed doubles 检验，不冒充真实正向系统任务测试。 |
| 实机发现与最小修正 | 首次完整 Console 旅程里 boot 保守返回 unavailable。固定只读 COM 诊断证实：此机缺 folder 时，CLR 返回 **FileNotFoundException / 0x80070002**，不一定是 COMException。现仅在 GetFolder/GetTask 阶段按明确类型和 HRESULT 2/3 判断缺失，补 **5** 项回归；成功 lookup 后 getter 缺元数据仍失败，权限/服务错误不吞为 absent。最终 Console 已重验 absent。 |
| 测试前置纠错 | 手工烟测夹具先有两次启动失败：误传不支持的 `--daemon`，以及空 YAML 遗漏必需 settings。均未进入业务读取；已核对真实解析器/既有 fixture，最终使用正式 CLI 显式启动。保留这些失败记录，不把它们算成功旅程或产品根因；新生 bootstrap/fatal 夹具日志已按独占路径、时间和哈希清理。 |
| ENG — 最终运行 | Debug 整解决方案构建 **0 警告/错误**；Core **242/242**。注册状态定向 **29/29**；广域在最终修正后连续两轮 **307/307**，均 0 跳过。修正前另有两轮 **302/302**；这些结果仍不是全量集成、Release、publish 或管理员注册变更验收。 |
| 复核边界 | 安全顾问只审主线提供的真实片段及设计说明，未调用工具、不算全仓读码审计。主线明确 agent-user scope、延后 CTS 释放，补转义/多 root/Ordinal 回归；所谓文化性 IndexOf 与重复 root 误解析不适用于实际已用 Ordinal 且拒绝内部引号的实现。native 清理在 ReadBoot finally 中完成后才设置结果；不可抢占调用持续 busy 是明确的保守降级，不自动重开 worker。 |
| 清理与保留 | 最终仓库/测试进程 **0**、R15 临时根 **0**、随机注册表 fixture **0**。两次失败夹具及成功旅程的独占数据已清理，未落烟测脚本；首次失败新建的 602 字节 bootstrap 日志经时间/完整 SHA256 校验后单独删除，未碰既有日志/父目录、8 月 scratch 或其他未提交工作。未 reset，也未变更权限。 |

广域继续使用 R13 节的过滤命令。完整边界见[注册状态契约](<docs/architecture.md#2026-11-01--自启与-boot-只读注册状态r15>)。
R14 偏好 Access denied 本轮没有复现，**根因仍未确认、未打生产补丁**；日志中的 FileShare
候选不是本次异常抛出栈证据。startup disable/restore/adopt、self enable/disable、boot
install/uninstall、GUI 日志/恢复与 VIS/完整 FLOW、发布及 SA01–SA10 总体验收仍未完成。

## 2026-11-01 — 偏好保存诊断与证据校正（R14）

本轮仅增加测试内的错误层诊断并校正证据，没有修改业务实现、GUI、权限或写入重试。
修复依据沿用 G1-r1 的 SA02/SA03，但**原先 Access denied 的原因仍未定位，不能称已修复**。
本轮没有视觉变化，G2/VIS 为 N/A；这不撤销产品其余未验证项。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 错误层诊断 | [GUI 配置测试](<tests/SystemAutorun.IntegrationTests/GuiConfigurationControlTests.cs>) 在快速偏好用例运行期间订阅 FirstChanceException，仅保留最近 16 条 ACCESS_DENIED 类型/HResult/抛出栈，每栈最多 8192 字符，仅失败时附入断言。该订阅是进程级，可能包含并行测试，不能直接当作当前 root 的因果证据；不记录 exception message、请求 payload、命令或文件内容。 |
| DATA — 诊断本身 | 新增 1 项确定性回归，验证 20 次受控拒绝异常仍由原 caller 捕获、仅保留 16 条、消息中的合成私有值不入栈摘要，Dispose 后不再收集。测试结束撤销订阅，不向 App/Agent 安装运行时日志器，不遗留观察线程。 |
| ENG — 实际运行 | Debug 整解决方案构建 **0 警告/错误**；Core **210/210**。带诊断的广域原过滤四轮均 **277/277**；增加诊断自测后，广域最终连续两轮 **278/278**，均 0 跳过。原快速偏好故障本轮未复现；这些通过不证明其根因消失。仍非全量集成、Release 或 publish。 |
| 调查边界 | 已区分 store 自身没有 root lease 与其工作区调用方实际持 lease，未将某个 FileShare 标志、短错误文案或子代理候选直接当根因。没有吞掉生产异常、修改 ACL、放宽 root 锁或自动重发业务意图；本轮未产生业务修复补丁。 |
| R13 证据更正 | 后续交接披露 R13 子代理无法提供可信文件工具读取；已在原节撤销独立安全复核结论。主线真实文件检查、编译、Pipe/Console 与 native 用例的结果保留，但不把它们冒充独立安全审计。 |
| 清理与保留 | 最终仓库/测试进程 **0**；三份生产配置 SHA256 保持轮前值。本轮新增 fixture 已清理，未创建烟测脚本；仅剩 8 月 12 日既有 scratch，未删除。源码未提交工作及已有日志均保留，无 reset 或环境权限调整。 |

广域过滤继续使用下节 R13 命令。原故障仍保留为待诊断；下一次发生时先核对抛出栈所属
root/线程与 RPC 阶段，再决定补丁。仍可推进独立且已批准的只读自启/boot 状态契约：
现有 HKCU 自启值及 boot 任务是固定的全局注册，不能把“存在”直接解释为当前 root 已启用。
这些 CLI 命令本轮尚未实现，其他产品缺口与 R12 VIS/完整 FLOW 也保持原状态。

## 2026-11-01 — 只读启动项清单（R13）

本切片接续 G1-r1，仅增加共享 `startup list`、过滤、只读 native 适配与 CLI；没有增加
启动项变更、自启/boot 命令或桌面 GUI 变更，因此本切片 G2/VIS 为 N/A。读取旅程已实测，
但**广域重复回归捕获一项尚未定位的偏好保存失败，工程验收不能记为全部稳定通过**。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW — 独立 Console / Agent | 两个独占空任务根分别显式启动 Agent **18256 / 15344**，Console 完成用户 Run 注册表只读过滤、禁用备份元数据查询、损坏备份失败与显式停机。真实注册表每次观察到 **7** 条，再按随机关键字过滤为空；不是虚构空 provider。禁用备份是临时根内的合成记录，不访问记录中的 UNC 目标。 |
| FLOW — 不可用与清理 | 损坏备份返回 **exit 1 / available=false / observedAt=null / counts=null** 及 backup_invalid；明确停止 Agent 后再次查询返回 **exit 6**，未自动拉起后台。JSON stdout 保持单行信封，未输出 native 原始命令、未执行工作负载、未请求 UAC。 |
| DATA — 共享契约 | [Core 启动清单回归](<tests/SystemAutorun.Core.Tests/StartupInventoryTests.cs>) 新增 **23** 项，覆盖范围/类型/状态/搜索/行数、来源与过滤错配、坏修订/时间/计数、重复身份、隐藏错误、lower-bound 标记和未知计数；不是启动项写操作覆盖。 |
| DATA — 真实 Pipe | [控制面回归](<tests/SystemAutorun.IntegrationTests/StartupControlPlaneTests.cs>) 新增 **10** 项，覆盖类型化读取与 CLI、严格拒绝路径/命令/修改参数、5 秒超时后仍持槽、busy 时其他 RPC 可读、配置变更丢弃旧匹配、来源/条目降级及 128 KiB 结果上界。此处列表与不可取消 provider 是合成的，不冒充真实 COM 卡死测试。 |
| DATA — native 只读 | [平台回归](<tests/SystemAutorun.IntegrationTests/StartupInventoryReaderTests.cs>) 新增 **13** 项，覆盖纯词法命令切分、禁用备份不探测 target/backup_path、损坏/超大/重解析/待处理事务、目录钉扎、缺源与坏叶子的区别。真实 WSH 在 owned 临时源创建测试 link，再由生产专属 STA reader 读取，参数和字节保持不变；不在实际 Startup 文件夹创建条目。 |
| 代码检查与复核证据更正 | 主线检查旧扫描的目标存在性探测、同步扫描及整组失败后，未直接包装旧 GetSnapshotAsync。后续交接披露：子代理扩面/收敛复核未获得可信文件工具读取，原先归于该独立复核的结论撤销，不计安全验收证据。readable 计数、中途缺文件降级、槽位及预算由主线按真实源码检查并回归；另核实 dispatcher/service 每 Agent 只构造一次、输入 lease 仅将 Win32 2/3 当作缺失。不能称独立安全审计通过，也不证明 COM 内部行为。 |
| ENG — 实际通过部分 | 当前整解决方案 Debug 构建 **0 警告/错误**；完整 Core 连续两轮 **210/210**。广域过滤首轮 **277/277**；R13 启动项与旧扫描合计 **27** 项在两轮均通过。没有跑全量集成、Release 或 publish。 |
| ENG — 尚未稳定 | 广域第二轮 **276 通过 / 1 失败**：既有 [快速偏好切换用例](<tests/SystemAutorun.IntegrationTests/GuiConfigurationControlTests.cs#L52-L82>) 在最终偏好校验时报 “Access to the path is denied.”。之后只运行该用例 **10/10** 通过，**不等于修复**。错误层级与是否和并行环境有关仍未知；未删测试、未放宽权限、未自动重发写意图，不能把该次失败隐藏为全绿。 |
| 清理 — 已核对 | 两个独占 Agent 均正常显式停止，无强停后备；其临时根、测试 link 与一次性烟测脚本已删除。最终仓库/测试进程 **0**、R13 根 **0**，三份生产配置 SHA256 与轮前一致。既有 8 月 12 日 scratch、fatal 日志和全部未提交工作保留，未作广泛清理或 reset。 |

本轮广域过滤在 R12 基础上加入 Startup：

```powershell
$names = 'Agent','CliControlPlaneTests','ConfigurationControlPlaneTests','ConfigurationDocumentControlPlaneTests','LogControlPlaneTests','HostControlPlaneTests','Ssh','HiddenProcessRunnerTests','Dashboard','Gui','RemoteHostsWindowTests','SingleInstanceServiceTests','PassiveMonitoringSafetyTests','ManagedProcessReaderTests','Startup'
$filter = ($names | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
dotnet build SystemAutorun.sln --no-restore --nologo
dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --no-build --no-restore --nologo
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter $filter
```

读取契约、非原子 revision 与原生预算见 [架构增量](<docs/architecture.md#2026-11-01--只读启动项清单r13>)。
下一步先定位上述偏好保存失败，再继续剩余 parity。startup disable/restore/adopt、self/boot、
GUI 日志/恢复、真实远端成功、发布与 SA01–SA10 总体仍未完成；R12 的 VIS/完整 FLOW
仍未验证。本次只读清单不能授权未来启动项变更，也不能代表全部 CLI 已覆盖。

## 2026-11-01 — GUI 远程概览共用 Agent（R12）

本切片按既有 G1-r1 / G2-r4 走修复路径，保持布局、风格、权限与主机管理旅程不变。
GUI 现在通过共享 Agent 查询配置 ID，不再自行拉起 SSH。实现与数据/工程回归已完成，
但截图通道没有提供有效内容，因此 **VIS 与完整 FLOW 仍未验证，不能称 GUI 验收完成**。

| 类别 | 实际结果与边界 |
| --- | --- |
| DATA — 共享读取 | [GUI 桥接器](<src/SystemAutorun.App/Services/AgentRemoteHostReader.cs>) 共用类型化客户端；实际应用到界面的 revision、配置 cursor 与 RPC revision 必须一致。传输或协议错误只显示不可用，不回退直接 SSH、不重启 daemon、不重发意图。 |
| DATA — 消费端 | [GUI 远程回归](<tests/SystemAutorun.IntegrationTests/GuiRemoteHostControlTests.cs>) 新增 **11** 项，覆盖真实 Pipe 合成成功/失败、修订错配、协议失败、禁用不探测、切换/关闭/选择 ABA 的晚回、后台离线、暂停刷新后过期、cursor 超前，以及真实零值与未知内存读数的通知区别。成功 provider 是合成的，不是远端实测。 |
| FLOW — native UIA 部分 | 两个独占空根分别启动 App **16900 / 11076**、Agent **7004 / 26408**，实际调用主机入口、禁用标签、回环失败标签、返回与退出菜单。仅对 `127.0.0.1:1` 执行失败探针，没有生产远端、真实密钥或业务任务。显式停止对应 Agent 后，GUI 保持配置不可用，没有自动再启动。 |
| DATA — 实窗绑定修复 | 第一轮 UIA 读到未知内存仍是 **0%**，发现旧 XAML 直接格式化数值；改用带可用性门控的读数并补依赖通知。当前构建再次启动后，禁用状态读到 **OFF / --**，连接失败读到 **UNKNOWN / --** 与真实 SSH 错误，更新时间为“尚无可用观测”。这只是绑定与调用证据，不是像素证据。 |
| VIS / 完整 FLOW — 未验证 | 已打开并检查 native PrintWindow 返回的图片：主屏全黑，主机窗口只有标题栏，不能检查内容、对齐、焦点反馈或三档桌面尺寸。声称 1320×820 的首次 resize 实际仍为 1120×700，主机窗实际 1040×650，DPI 为 96；已丢弃误导图片。Windows 也未确认前台激活；未绕过限制、改显示配置或拿 UIA/单测冒充视觉与实体输入旅程通过。 |
| ENG — 已通过 | 整解决方案 Debug 构建 **0 警告/错误**；完整 Core **187/187**、本轮受影响集成过滤连续两轮 **250/250**，均 0 跳过。过滤涵盖 Agent、CLI、文档、日志、主机、SSH、GUI、单实例和被动监测，不是全量集成、Release 或 publish。 |
| 清理 — 已核对 | 两轮均从实际菜单退出 GUI，并显式停止各自 Agent；清理未使用强停后备。独占根、known_hosts、状态指针、一次性烟测脚本与黑色图片均已删除。最终仓库/测试根进程 **0**、临时 SystemAutorun 前缀目录 **0**，生产三份配置 SHA256 均保持不变；既有未提交源码、旧记录与诊断资料保持原样。 |

本轮回归使用以下过滤；其他集成套件不纳入通过数量。

```powershell
$names = 'Agent','CliControlPlaneTests','ConfigurationControlPlaneTests','ConfigurationDocumentControlPlaneTests','LogControlPlaneTests','HostControlPlaneTests','Ssh','HiddenProcessRunnerTests','Dashboard','Gui','RemoteHostsWindowTests','SingleInstanceServiceTests','PassiveMonitoringSafetyTests','ManagedProcessReaderTests'
$filter = ($names | ForEach-Object { "FullyQualifiedName~$_" }) -join '|'
dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --no-build --no-restore --nologo
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter $filter
```

共享契约与取消边界见 [架构说明](<docs/architecture.md#2026-11-01--gui-远程概览共用-agent>)。
仍需补有效截图、实体输入/焦点与桌面尺寸检查；GUI 日志/恢复、剩余 CLI parity、发布、
真实远端成功与 SA01–SA10 总体均未完成。旧切片的完成项不扩张为产品总体验收。

## 2026-11-01 — 主机控制面与显式 SSH 状态

本切片接续 G1-r1，提供共享主机 CRUD、严格观测及 Console CLI；没有修改 WPF 布局、
视觉或交互，G2/VIS 对本切片为 N/A。主屏 G2-r4 仍是批准基线，GUI 远程 RPC、日志与
恢复 UX、剩余 CLI parity、Release/publish 和 SA01–SA10 总体均未完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW — 真 Agent / Console | 独占空任务根显式启动 Agent **27552**，由独立 Console 完成主机 add/update/show/list/remove、相同意图重放和旧修订冲突；disabled 与未知 ID 均被拒绝。YAML 与零任务保持不变，没有业务工作负载参与。 |
| FLOW — 本机 OpenSSH | 只对隔离 loopback 的失败连接执行本机系统 OpenSSH，接受拒绝或超时，结果 offline / available=false / observedAt=null；known_hosts 未改变。没有访问生产远端或真实凭据，不把这条失败旅程当作远端认证、Windows/Linux 成功采样的证明。 |
| FLOW — 修复后独立进程重验 | 当前构建启动独占 Agent **23608**，独立 Console 连续执行 **8** 次带 revision 的偏好提交及重读，并查询主机列表；空任务 YAML 不变。显式停止后观察进程退出，删除独占根，生产 SHA256 不变。首个夹具漏写必需的 settings，Agent 明确拒绝启动；按共享 loader 的有效空根格式修正后才完成此旅程，没有重发不明意图。 |
| DATA — 共享边界 | [主机定义策略](<tests/SystemAutorun.Core.Tests/RemoteHostDefinitionPolicyTests.cs>) 与 [观测信封](<tests/SystemAutorun.Core.Tests/AgentHostObservationTests.cs>) 新增 **22** 项；[真实 Pipe 主机控制面](<tests/SystemAutorun.IntegrationTests/HostControlPlaneTests.cs>) **10** 项覆盖 CRUD/CAS/回执、禁用/未知、每 ID 单飞、全局最多两槽、取消、错误来源、配置变更与 Agent 协调 remove/re-add ABA。成功资源观测使用合成 provider，不是远端实测。 |
| DATA — SSH / 进程 | [输入 lease](<tests/SystemAutorun.IntegrationTests/SshInputFileLeaseTests.cs>)、[SSH 读取](<tests/SystemAutorun.IntegrationTests/SshRemoteHostReaderTests.cs>) 和 [有界进程](<tests/SystemAutorun.IntegrationTests/HiddenProcessRunnerTests.cs>) 验证本地路径、reparse/硬链接拒绝、真实写/删/祖先重命名阻止、已存在身份旁路件、隔离参数与固定脚本、输出/时间/取消边界及 owned 子进程退出。CRUD 只做词法校验，不探测凭据或网络。 |
| ENG — 并发 / 回复收尾 | 合并回归触发快速偏好保存失败与锁文件 ACCESS_DENIED。独立 [原生 Pipe 收尾 8 项](<tests/SystemAutorun.IntegrationTests/AgentClientReplyTeardownTests.cs>) 在修复前复现完整业务失败回复被 DisposeAsync 的 broken-pipe 覆盖；修复后保持成功/业务错误，坏或丢失回复仍保守处理，不重发意图。[root lease](<tests/SystemAutorun.Core.Tests/RootWriteLeaseTests.cs>) 验证 40 个竞争写者独占及空锁文件复用；移除 DELETE_ON_CLOSE 的 delete-pending 窗口，取消等待者后仍可重新获取。 |
| ENG — 最终回归 | 整解决方案 Debug 构建 **0 警告/错误**；完整 Core **187/187**、Agent/CLI/日志/配置/主机/监测 GUI/单实例的受影响集成 **237/237**，均 0 跳过；修复后相同过滤再执行一轮仍为 **187 + 237** 全通过。git diff --check 通过，只有既存换行提示。不是全量集成、Release、publish/安装或生产部署。 |
| 静态与动态复核 | 安全角色审读命令、输入与观测边界；主线补钉住已存在的 .pub、-cert.pub 与 -cert.pub.pub。真实共享仲裁回归确认 attributes-only 句柄不足，文件改为实际读访问、目录改为 0x81；文档交换和日志目录 pin 同步修正。没有动态验证非管理员身份。 |
| 清理 | 观察测试 Agent **27552 / 23608** 退出，删除独占根、输入、known_hosts 和烟测脚本。PID 发布夹具改为关闭后原子公布文件，并在 finally 取消/等待自己启动的进程；核对失败夹具完整命令行，确认生产者已退出后只删除该测试根。末次审计本仓库 App/Agent/CLI/Elevated、直接 runner 生产者及本轮 fixture 前缀目录均为 **0**。生产三份配置 SHA256 保持不变，保留既有未提交工作及旧诊断资料。 |

边界：已存在的身份旁路件被钉住，但不能阻止非协作写者新建原本不存在的旁路文件，
因此密钥目录必须可信且由用户控制。CLI 断线不会立即取消服务端工作，仍由请求和命令
期限兜底。ABA 检查只覆盖 Agent 协调写入；协作读写均持有 root lease，不承诺裸 store
并发或非协作编辑器的原子性。详情见 [共享主机契约](<docs/architecture.md#2026-11-01--主机-crud-与显式-ssh-状态>)。

## 2026-11-01 — 配置原文、YAML 校验与 CAS 应用

本切片接续 G1-r1，打通共享文档 API 与 Console CLI，没有修改 WPF 布局或交互，因此
G2/VIS 为 N/A。GUI 编辑入口、日志/恢复 UX、其余 CLI parity、发布及 SA01–SA10 总体
仍未完成，不能以本节结果替代真实 GUI 或完整产品验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW — 真 Agent / Console | 在独占空任务根显式启动 Agent **13492**，由独立 Console 读取原文及修订、验证候选文件并执行带确认的 CAS 提交。**157 bytes** 的候选含 UTF-8 BOM、中文注释、CRLF 和未知字段；落盘及原文 stdout 与候选逐字节相同，修订仅写 stderr。没有配置或运行任何业务工作负载。 |
| FLOW — 确认 / 重放 / 冲突 | 有效候选返回 exit 0，坏 YAML stdin 返回 exit 2；缺少确认返回 exit 4，旧 revision 返回 exit 5 / revision_conflict。同一 operation ID 与参数返回 replayed，只有一份原文备份。候选 backup_dir 未被创建，备份固定在本次 root 的 backups/config。 |
| FLOW — 在线修复 | 测试生产者明确将隔离根写成坏 YAML 后，同一在线 Agent 仍能返回原文字节及修订；显式提交有效候选完成修复并保存坏原文备份，changes.available=false，不假造空任务差异。普通共享配置重读仍为零任务。该结果不证明启动前损坏的 root 能自动启动。 |
| DATA | 新增 [文档 Core 20 项](<tests/SystemAutorun.Core.Tests/ConfigurationDocumentTests.cs>) 与 [文档 IPC/CLI 13 项](<tests/SystemAutorun.IntegrationTests/ConfigurationDocumentControlPlaneTests.cs>)。覆盖规范编码与 40 KiB/请求帧预算、坏 UTF-8、BOM/换行、无写入校验、重复键/多文档/深度/alias、保留 override/偏好、并发 CAS、设置变更冲突、固定备份、junction/硬链接拒绝、确认、移除摘要和 no-op。受保护的私有 DACL 在目标及备份上均保持。 |
| DATA — 未知结果 | 真实 Pipe 完成提交并持久化回执后，由测试传输层注入“回复丢失”；CLI 返回 exit 6 / outcome_unknown 及原 operation ID，发送计数为 1，配置已改变。随后查询回执并用同一 ID 重放成功，没有第二份备份。这是提交后的传输故障注入，不是实际断线、崩溃或断电测试。 |
| ENG | 最终整解决方案 Debug 构建 **0 警告/错误**；完整 Core **164/164**、Agent/CLI/日志/配置/监测 GUI/单实例的受影响过滤集成 **197/197**，均为 0 跳过；包含上述文档 **20 + 13** 项。git diff --check 通过，仅有既存换行提示。未运行全部集成、Release、publish/安装或生产部署。 |
| 静态复核 | 安全角色检查规范编码、解析前置门、共享 loader、路径钉扎、确认、同卷交换和 unknown 语义。其“原暂存 ACL 调用必败”结论未获实证支持，此前非 no-op 的真实提交测试已通过；主线仍改为创建文件时携带原 DACL，消除对仅写句柄事后改 ACL 的权限假设，随后复验私有 ACL、字节和备份。未动态验证非管理员身份。 |
| 清理 | 首次烟测在启动后、记录 PID 前出错，初次 finally 又遇到监测锁；原始异常被清理异常遮盖，未确认其根因。核对完整映像、PID、命令行及独占 root 后，仅终止测试 Agent **14848** 并删除该根；脚本抑制 Task.GetResult 管道返回，并补原始异常与 PID=0 时的精确归属清理，再完成上述旅程，显式停止并观察 **13492** 退出。末次本仓库 App/Agent/CLI/Elevated 为 **0**，本轮临时根、junction、备份、编辑文件、状态指针和烟测脚本均已清除。三份生产配置 SHA256 不变，保留既有未提交工作与旧诊断日志。 |

边界：文档交换只支持本机磁盘 root，原文与候选各限 40 KiB。未知 YAML 字段不做语义
校验，独立偏好不由此命令校验或重写；完整替换不会补回候选遗漏的旧内容。外部编辑器
竞态、目录刷盘和进程崩溃窗口仍按 [文档交换契约](<docs/architecture.md#2026-11-01--配置原文验证与-cas-应用>)
保守处理；不能宣称 exactly-once 或断电持久性。

## 2026-11-01 — 有界文件日志与 CLI（当前切片）

本轮沿用 G1-r1 的日志契约，实现共享后端和 Console CLI；没有修改 WPF 布局、视觉或
日志交互，因此本切片 G2/VIS 为 N/A。GUI 日志、恢复 UX、其余 CLI parity 与发布仍未
完成，不能据此把 SA07 或 SA01–SA10 整体标为通过。

| 类别 | 实际结果与边界 |
| --- | --- |
| FLOW — 真 Agent / Console | 在独占空任务根显式启动 Agent PID 21428，使用独立 Console 配置来源并读取合成业务文件。约 2 MiB 输入只扫描 **260096 bytes**，返回最近 2 个事件并明确 truncated；多行异常与未知级别原文保留。没有业务工作负载参与。 |
| FLOW — 续读 / 导出 | 独立 follow Console 返回 2 页，第二页只包含第一次读取后追加的 1 个事件。由测试生产者显式将文件转为归档，再追加旧文件并创建新文件；用原游标读到两份不同文件身份的 2 个新事件，不重读旧页。error + UTC since 的显式导出产生 **1366 bytes / 1 event** 的 NDJSON 信封；不是全历史导出。 |
| FLOW — 失败 / 只读 | WSL 来源返回 exit 1 / `provider_unsupported`。真实 NTFS junction 来源返回 exit 1 / `reparse_or_nonregular_source` 且无条目，没有跟随链接读取目标。静止来源读取前后 SHA256 相同，配置 revision 仍为 `09599408c80b7275c056a5771be39679212dd2328d450f454da69524fc05eccf`。 |
| DATA | 新增 [日志 Core 24 项](<tests/SystemAutorun.Core.Tests/LogPageReaderTests.cs>) 和 [日志 IPC/CLI 20 项](<tests/SystemAutorun.IntegrationTests/LogControlPlaneTests.cs>)：真实 FileLog 格式、UTC/未知级别、多行、末行补全、轮转/丢失/截断、前缀改写、从最早归档分页、字节/事件/JSON 边界、无效 UTF-8、过滤/跨 root 游标、占用/缺失/provider、未知偏好字段与 CAS、非法选项不发 RPC、取消不关后台、导出不覆盖、任务控制类别碰撞及来源列表的序列化字节上限。保留名检查覆盖上标端口号和扩展名前空格，测试不访问实际设备。CLI 的 from-start 与后续 cursor 均经真实 Pipe 复验。 |
| ENG | 整解决方案 Debug 构建 **0 警告/错误**；完整 Core **144/144**、包含 Agent/CLI/配置/监测 GUI/单实例的受影响过滤集成 **184/184**，0 跳过；其中日志集成 **20/20**。未运行全量集成、Release、publish/安装或生产部署。 |
| 静态复核 | 安全角色只读检查路径门、目录钉住、文件身份、游标与预算，未确认可达的路径泄露/越权。主线据此加宽前缀/边界锚点并计入 256 KiB 总读取预算，补未知时间戳原因与从头分页。坏游标由现有 Agent 错误信封以 invalid_argument 拒绝；偏好坏定义在共享 store 验证时明确失败，不自动覆盖。归档不可读则本来源 fail-closed，不静默跳历史。 |
| 清理 | 独立 Console 结束后显式 daemon stop，观察 Agent 21428 退出；临时 junction、约 2 MiB 合成日志、导出、隔离根、状态指针和烟测脚本均已删除。末次本仓库 App/Agent/CLI/Elevated 进程为 0。三份生产配置 SHA256 与实测前一致；未重置、删除或清理既有未提交工作。 |

边界：Windows Event 与 WSL journal 仍无适配器，业务 stdout/stderr 不被接管。时间过滤
排除无法确认时间戳的条目，但报告原因和数量，移除过滤可查看原文。游标锚点不是完整
文件校验，不能保证发现未采样区的原地改写；本轮没有动态验证非管理员或真实系统日志
权限路径。实际契约与使用方式见 [架构接续](<docs/architecture.md#2026-11-01--有界文件日志与-cli-接续>)。

## 2026-11-01 — GUI 共享监测与真实历史（当前阶段）

本切片沿用 G1-r1 与 G2-r4，完成主屏的共享快照、运行时来源和历史读模型。
它不是完整 GUI/CLI 交付，也不将 SA01–SA10 整体标为通过。业务日志、暂停/恢复 UX、
剩余 CLI parity、Release 和发布部署仍需后续分项验收。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS | 已实际查看 Windows / 150% DPI 的 [1120×700 DIP](<docs/visual/sa-20261031/r8-monitor-native-1120x700.png>)、[1320×820 DIP](<docs/visual/sa-20261031/r8-monitor-native-1320x820.png>)、[1620×651 DIP](<docs/visual/sa-20261031/r8-monitor-native-1620x651.png>) 及 [WSL 被动未知、30 天窗口](<docs/visual/sa-20261031/r8-monitor-wsl-unknown-30d.png>)。使用 owned HWND 的 PrintWindow 捕获实际 WPF，不拿被其他窗口遮挡的桌面区域充当界面；实际像素分别为 1680×1050、1980×1230、2430×976。最小尺寸的长名称折行，两个指标行和操作区均保留。 |
| FLOW — 原生来源 | 独占映像 PID 30980、实际开始时间 08:30:03.228+08:00 与 Agent runtime 的 processStartedAt、processIdentity 一致。GUI 显示实际工作集约 24.6–26.2 MB、低 CPU 以及相应分钟数，没有借用主机或 Agent 的启动时间。菜单退出 App 26980/14084 后 Agent 与工作负载仍存活；重启 Agent 为 PID 3032 后连续健康起点变为 09:02:05+08:00，但同一原生 PID 的开始时刻不变。 |
| FLOW — 时间窗 | GUI 实际选择 24 小时、30 天及不同服务；现有 7 天操作的截图仍显示 24 小时，因此不将 7 天 GUI 切换标为已实测。7 天的独立 CLI 查询返回 availability=89.7909%、coverage=0.2405%，绝大部分窗口为 unknown，而不是把短期观测铺满 7 天。WSL systemd 被动观测为 UNKNOWN，uptime/CPU/内存均为 --、可用率 --、覆盖率 0.0%；没有执行其工作负载。资源时间桶保持稀疏点和断线，不补造连续曲线。 |
| FLOW — 离线 | 09:03:44+08:00 显式停止后台，09:05:53+08:00 再查时 Agent 已退出、原生 PID 30980 仍存活且主窗 IsEnabled=true。实际查看 [修复后离线窗口](<docs/visual/sa-20261031/r8-monitor-agent-offline.png>)：当前状态、uptime、资源及历史降为未知，状态区说明后台配置读取不可用，没有模态框，也没有被刷新隐式重启的 Agent。随后在真实菜单暂停/恢复刷新，页脚分别显示“暂停刷新”/“10s”，最后由菜单退出。 |
| DATA | 新增 [监测读模型 14 项](<tests/SystemAutorun.IntegrationTests/DashboardMonitoringTests.cs>)，覆盖 unavailable 缓存、心跳、过期/未来数据、组独立性、目标身份、uptime 来源、空历史、24/7/30 窗口、迟到/取消回复、错误身份、关闭后回执、只读 RPC 不回退探针，以及配置读取失败的非模态降级和历史代际失效。呈现假数据的单测与真实原生工作负载证据严格分开。 |
| ENG | 最新整解决方案 Debug 构建 **0 警告/错误**；Core **120/120**，包含单实例、CLI、Agent、配置与 GUI 的受影响过滤集成 **164/164**，0 跳过。不是全部集成测试；没有运行 Release、publish/安装或生产部署。 |
| 静态复核 | 安全角色只读检查快照/历史消费、身份与版本、可用性/新鲜度、uptime、竞态和窗口生命周期，没有确认范围内可达且有实质影响的新问题。未把静态审读当作生产者、性能或管理员路径的动态证明。 |
| 清理 | 菜单退出最后一轮 GUI 后，显式启动临时 Agent PID 15156，仅为停止本次独占工作负载；task stop 回执为 Stopped 1 process(es)，再显式 shutdown 并观察退出。最终 owned App/Agent/CLI/工作负载为 0，隔离根、状态指针和捕获脚本已删除，仅保留 5 张证据。三份生产配置 SHA256 与实测前一致；旧 scratch 中 2026-08-12 的目录和既有 fatal 日志保持不动。`git diff --check` 通过；未重置或清理既有未提交代码。 |

实测修复两处既有读链问题：默认 AutoRefresh 为 false 且主窗定时器被无条件停止；后台
离线时配置读取错误经通用动作错误处理弹出模态框。现在默认启动 10 秒只读刷新，并以
3 秒预算读取配置；不可用时清空当前观测、取消旧历史代际，在状态区报告原因。控制或
配置写操作不采用这条降级路径，仍保留确认和错误语义。

烟测最初修正了 fixture 的 exec/args 字段与当前交互控制面的 current_user/logon 约束。
对先前的结果不明回执先查同一 operation ID，并确认恢复库尚未初始化、独占进程数为 0，
才用修正后的明确测试配置发起新意图；没有盲换 ID 重放。首张最大化窗口的尺寸不符合
要求，已用 normal 状态及实际 HWND 边界断言重新采集，未作为尺寸矩阵通过证据。

当前接续边界见 [架构读模型说明](<docs/architecture.md#2026-11-01--gui-共享监测与历史读模型>)。

## 2026-11-01 — 独立 Console CLI 与真实协作（已验收基础切片）

沿用 G1-r1 和 G2-r4；本轮完成 CLI 控制面基础和启动器复用，**不是完整 CLI parity 或
SA01–SA10 总体验收**。CLI 没有自定义图形界面，G2/VIS 对该入口为 N/A；受启动器影响的
现有 GUI 仍执行实际截图与旅程检查，没有借纯后端名义免验。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS | 实际查看 Windows / 150% DPI 的 [离树配置根冷启动主屏，1320×820 DIP](<docs/visual/sa-20261031/r7-cli-gui-cold-launch-1320x820.png>) 和 [CLI 写入恢复偏好后的真实菜单](<docs/visual/sa-20261031/r7-cli-preference-reflected-menu.png>)。主屏由 1980×1230 像素缩放输出，screen-region 时确认目标前台无遮挡；菜单单独以 PrintWindow 捕获。只修复已批准的离树启动行为，没有布局/样式变更，不重做未受影响的尺寸矩阵。 |
| FLOW — 真 Console | 在有效空任务隔离根运行独立 Console：显式启动 Agent PID 12432，配置读取、stdin JSON 偏好修改、同 ID 重放成功，旧 revision 修改以 exit 5 拒绝。最初监控组 available=false 被原样返回，没有伪称 healthy。只有 daemon start 启动后台，没有 GUI 或业务工作负载参与。 |
| FLOW — GUI 协作 | 显式 CLI shutdown 后，从原开发输出直接打开离树 root 的 App PID 29976；新共享启动器启动 Agent PID 24356，无需预启动或复制程序。实际菜单显示 CLI 写入的 recovery=true，GUI 点击关闭后独立 CLI 读到 false 且 revision 已变化。菜单退出 GUI 后，CLI watch 返回 2 条有效 JSON 信封，仍为 instanceId `a081277ab06b4c8fb234f808dc659957`，心跳由 07:53:38+08:00 前进到 07:53:48+08:00。 |
| DATA | [CLI 56 项回归](<tests/SystemAutorun.IntegrationTests/CliControlPlaneTests.cs>) 覆盖命令/选项校验、修订与确认、错误退出码、输入边界、未知字段、稳定 operation ID、结果不明、批量部分失败、历史任务身份、watch、取消不启动后台、真实管道与独立 Console、成功及失败回放元数据、派生读取的原始错误与心跳不冒充业务观测。部分路由使用可注入传输，不据此声称真实工作负载已启停。 |
| 后台生命周期 | 新真实 Console/Agent 用例证明 daemon start 在 Agent 继续运行时释放调用方捕获流，旧 shutdown ID 在新实例返回 exit 1 / shutdown_already_consumed，且新 PID 仍可 ping。最终用新的明确意图 shutdown 并等待退出；没有把历史回执当作新停机动作。 |
| ENG | 整解决方案 Debug 构建 **0 警告/错误**；Core **120/120**，12 类受影响过滤集成 **150/150**，含上述 56 项 CLI 用例。不是全量集成；Release、publish/安装和生产部署未运行。 |
| 静态复核 | 安全角色只读复核报 1 条低危旧 shutdown 回放假成功，CLI 已按上行语义修复并补真实跨实例回归；没有新的中高危反馈。完整 RPC 仍共用协议/root/请求/操作 ID 校验，typed 客户端异常契约保持。 |
| 清理 | 两次本轮原生 fixture、owned App/Agent/CLI/testhost、状态指针、烟测脚本及 3 张工具临时截图均清理，只保留两张上述证据。三份生产配置 SHA256 不变，两份既有 fatal 日志保留。共享测试 scratch 中 2026-08-12 的旧目录不属于本轮，未删除。停机后的真实 task list 以 exit 6 / timeout 报不可用，且未重启后台。 |

首轮测试 fixture 缺少 settings/health，修正后全部通过。真实烟测另暴露了 GUI 子进程继承
CLI 捕获管道的问题：仅配置独立标准流还不够，Windows 仍会继承其他带继承位的句柄。
最终 CLI 清除自己重定向标准句柄的继承位，Agent 使用独立标准流；真实 Console 回归
验证 EOF 与后台存活同时成立。失败烟测先独立发明确 shutdown、观察退出，再清理，未
盲重放不确定的启动或业务操作。

当前运行方法见 [README](<README.md#协作-cli当前开发版>)，边界见
[架构实施进展](<docs/architecture.md#2026-11-01--独立-console-cli-基础已实现>)。
此 CLI 基础切片之后的 GUI monitoring-snapshot、runtime 与 monitoring-history 接线见本页顶部。
暂停/恢复、有界业务日志、配置 apply、远程探针、启动项等 CLI parity，以及完整
SA01–SA10、Release 与发布部署仍需分别验收。

## 2026-11-01 — Agent 独占恢复与共享启停

恢复和手动 Start/Stop/Restart 已统一接入 Agent，GUI 旧恢复循环已删除。此节只验收这条
受影响链路，**不把监控/历史/日志绑定、完整 CLI 或 SA01–SA10 总体判为完成**。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS | 实际查看 Windows / 150% DPI 的 [隔离原生工作负载主屏，1320×820 DIP](<docs/visual/sa-20261031/r6-recovery-native-1320x820.png>) 与 [恢复开启菜单](<docs/visual/sa-20261031/r6-recovery-menu-enabled.png>)。主屏由 1980×1230 像素等比输出，PrintWindow 捕获真实 WPF 内容。无布局/样式变更，不重复未受影响的尺寸矩阵；历史区仍为未绑定的明确占位，不声称完成历史可视化。 |
| FLOW — GUI 与恢复 | 实际设置菜单关闭/开启自动恢复并由共享配置 RPC 验证落盘；通过菜单退出 App PID 31168。确认 GUI 消失后仅终止本次独占映像 PID 28808；原 Agent PID 19504、instanceId `ef651293f2b34aad9d909f65dc46b290` 不变，于 23:10:17Z 自动恢复为 PID 24656，持久 attempt 为 Succeeded，23:10:27Z 独立采样为 healthy 且实例身份更新。没有 GUI 计时器或第二控制器参与。 |
| FLOW — 持久 Stop | 独立 IPC Stop 成功并于 23:11:30Z 写入 Paused=true 和新 IntentId；显式 shutdown 后启动同一 root 的 Agent PID 29092。23:13:49Z 的新实例仍保留同一暂停意图，观测为确定 process-not-found、工作负载数 0；距离原恢复 attempt 已 **211.9 秒**，超过两分钟冷却，不能把未重启误归因于冷却。最终再次显式 shutdown 并确认退出。 |
| DATA | Core **120/120**，其中 [恢复库 26 项](<tests/SystemAutorun.Core.Tests/RecoveryControlStoreTests.cs>) 在原真实磁盘/CAS/原子发布覆盖上，补持租约读取、错根/已释放 lease 拒绝和不同显式 IntentId。缺失或损坏状态不授权自动动作，不自动清空。 |
| 后端 FLOW | [控制器及 worker 29 项](<tests/SystemAutorun.IntegrationTests/AgentTaskControllerTests.cs>) 全用内存执行器，覆盖默认关闭、unknown/过期/未来/失败观测、不同配置、禁用/不追踪/SYSTEM/boot、整依赖准入与冷却、暂停、Stop 脱离 RPC 取消、后意图胜出、Restart 停止后再验依赖暂停、等待锁期间配置变化、容量 1 最新队列、操作回执及关闭 drain。它们不构成真实 WSL/SCM 验收。 |
| ENG | 最新整解决方案 Debug 构建 **0 警告/错误**；Core 120/120、上述 11 类受影响过滤集成 **94/94**。发现并修复 Windows 客户端提前断开触发 accept 错误 232、误使 Agent 整体退出的问题；补 5 个错误分类与 64 次断连压力回归。全量集成、Release、发布未运行。 |
| 静态复核 | 安全角色复核未报新的中高危问题；其 worker 异常可能跳过 drain 的顺序反馈已用独立 finally 修复。持久准入与 OS 副作用仍非原子提交，崩溃只转 unknown、不自动重放。 |
| 清理 | 原生 fixture 只对 loopback 127.0.0.1 运行；最终 App/Agent/Elevated/testhost 和本次独占映像均为 0，临时 root、所有本轮测试进程、状态指针及 5 张工具临时截图已删除，仅保留上述两张证据。三份生产配置 SHA256 与实测前相同；既有未提交工作及旧 fatal 日志未改。`git diff --check` 无空白错误。 |

验证过程中修正了 fixture 偏好键的 snake_case，消除了最初三个恢复测试失败；另一次整合
运行捕获上述 pipe accept 竞态，修复后全过滤集通过。首个真实 fixture 错把健康 pattern 当
命令行匹配，随后全部清理并改用临时根内独占映像；现有进程探测按映像名/路径而非参数
匹配，不能用通用 pwsh 名称做安全启停隔离。离树开发 GUI 启动需预先启动对应隔离 Agent；
打包发现/CLI 启动及生产部署旅程仍要单独验收。shutdown 返回 shutdownRequested，不是
CommandResult；测试脚本的错误类型假设已通过先观察实际退出、再正确读取结果解决，未盲重放。

下一步绑定新监控信封、真实历史/日志与暂停/恢复状态，并补全用户和 agent 共用的 CLI。
首次初始化/修复 UX、明确的停止后台入口、容量无 GC 的用户反馈与逐项 SA01–SA10 仍待完成。

## 2026-11-01 — 退出界面保留后台与恢复意图基础

本轮完成了“退出 GUI 不关后台”的路径和恢复状态库基础，**没有上线共享恢复器或手动
Stop 暂停保护**。原 GUI 恢复循环仍保留，CLI/历史绑定与总体 SA01–SA10 继续未完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS | 查看真实运行的 [空任务主屏，1320×820 DIP](<docs/visual/sa-20261031/r5-lifetime-empty-1320x820.png>) 和 [退出菜单](<docs/visual/sa-20261031/r5-lifetime-exit-menu.png>)，Windows / 150% DPI；主屏截图由 1980×1230 像素等比输出，菜单保留物理像素。PrintWindow 捕获了目标 WPF 内容，未把遮挡窗口当作实装。没有布局/样式变更，不重做未受影响的最小/宽屏矩阵，也不宣称完整页面新验收。 |
| FLOW | 真实 empty YAML、补启动/恢复关闭的隔离 App：打开“设置与工具”并通过实际菜单“退出”。App PID 30460 消失后，Agent PID 21168 与同一 instanceId 保持；心跳由 22:11:16Z 前进到 22:11:46Z，历史写入也前进。随后独立发送该 root 的 journaled shutdown，响应成功并确认 Agent 消失。没有运行或启停业务工作负载，未验证真实工作负载跨 shutdown 生存。 |
| DATA | [恢复库 23 项回归](<tests/SystemAutorun.Core.Tests/RecoveryControlStoreTests.cs>) 使用真实目录、JSON、Win32 原子替换、共享占用失败、junction 和并发独立 store；时钟受控且不修改系统时间。覆盖搬迁/CAS、名称级冷却/目标变化/时钟后退、暂停不被完成覆盖、旧完成失效、未决 attempt 不被新自动准入覆盖、大小写 ID、损坏/容量/缺失与未提交临时意图的 fail-closed。库不调用任何工作负载执行接口，也未接入现有 Stop。 |
| 后端 FLOW | [真实启动器用例](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.cs>) 证明 Dispose 两次后仍为同一 Agent，第二启动器只复用不重复拉起；启动进程后取消 GUI 等待也不杀后台。fixture 持有独立进程句柄，最终显式 shutdown/严格清理；不依赖进程级环境变量改写。 |
| ENG | 最新整解决方案 Debug 构建 0 警告/错误；Core **117/117**，与下一节相同 10 类过滤集成 **59/59**。新增库 23 项和启动器 2 项均包含在上述总数中。全量集成、Release、发布仍未运行。 |
| 清理 | 末次 App/Agent/Elevated/testhost 为 0，SystemAutorun 临时 root 为 0。实际 GUI root 与 2 张工具临时截图均按确切路径、归属和进程终止确认后删除，仅保留上述证据。三份生产配置 SHA256 unchanged=True；既有未提交修改保留。`git diff --check` 无空白错误，仅既有换行提示。 |

设计复核指出共享 task/root 取锁顺序、依赖逐节点准入与 Stop 的 Agent 托管收尾要求；这些
尚未实施，不能由状态库通过推断已满足。安全静态复核线索已补回归：未决 admit、规范化
ID、缺失状态及未知临时意图。正常发布现在请求数据与 rename write-through，但没有做
掉电实验；硬件保证和副作用/状态非原子的边界继续保留。

下一步先完成持租约读取/提交入口、共享任务/依赖锁与单恢复 worker，再一并切换手动
Start/Stop 和旧 GUI 恢复。缺失状态的首次初始化/修复必须有明确意图，不能自动清空后
启动；容量上限无 GC 的失败处理、UI 恢复状态与明确的“停止后台”入口也仍待接线。

## 2026-11-01 — Agent 自驱采样与历史 RPC

本节更新后端接线证据，不更新或代替 r4 的 VIS/FLOW 证据。普通 Agent 代码现在启动独立
采样循环；GUI 尚未绑定新信封，退出界面及恢复策略仍待迁移，SA01–SA10 不能整体判完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS / GUI FLOW | N/A：本次不改界面或 UI 交互。没有新增 GUI/CLI 历史旅程截图，也不把后台测试当作前端完成证据。 |
| 后端 FLOW / DATA | [真实 Agent 进程用例](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.cs>) 使用空任务配置，首次获取快照后 12 秒不发 IPC，再连接时同一实例的心跳与历史写入时间均前进；历史 RPC 对不存在任务返回全未知。另用真实文件阻挡历史目录初始化，验证 live 观测、配置读取/写入和显式 history_unavailable 仍工作。没有启动业务工作负载或执行 WSL guest/SSH/SCM 管理操作。 |
| 来源及派生值 | [AgentMonitor 回归](<tests/SystemAutorun.IntegrationTests/AgentMonitorTests.cs>) 的业务健康/进程数据为受控来源，磁盘与配置读取为真实 IO。覆盖首个 CPU 未知/实测零、来源独立失败、过期/未来来源时间、错误 identity/类型、同 PID 新启动时间、SCM 宿主替换但服务 uptime 不借用、配置 generation 竞态、合作取消超时、写失败和无客户端定时循环。桩数值不是机器资源实测。 |
| 被动性 | [被动观测回归](<tests/SystemAutorun.IntegrationTests/PassiveMonitoringSafetyTests.cs>) 新增同周期复用用例：后台平台适配器只调用一次 passive 探针，WSL unknown 不变故障，不读取 guest keepalive PID，也不调用执行接口。 |
| 历史时效 | 可选 ValidUntil 把已发布帧的有效期限制在周期起点后 10 秒内。真实存储与受控慢发布测试证明不会因发布耗时延长观测；Core 另验证期限只能缩短、不能延长，非法期限拒收。 |
| ENG | 最新整解决方案 Debug 构建 0 警告/错误；Core **94/94**，下述过滤集成 **57/57**。全量集成、Release、发布仍未运行。 |
| 清理 | 末次 App/Agent/Elevated/testhost 为 0，SystemAutorun 临时 root 为 0；构造失败留下的 13 个空 fixture 已按确切失败路径和空目录校验删除，并补构造失败清理。生产 YAML/覆盖/共享设置 SHA256 三项 unchanged=True，既有未提交修改保留。`git diff --check` 无空白错误，仅既有换行提示。 |

安全角色只读复核没有确认新越权、跨 root 泄露或读路径执行问题。对服务宿主时间的建议
只采纳“替换身份作为重启线索”，不采纳把共享宿主启动时间冒充服务 uptime；对应回归
保持 ProcessStartedAt 为空。评审是静态线索，以上执行结果才是相应测试证据。

复现本节过滤集：
```powershell
dotnet build SystemAutorun.sln --no-restore --nologo
dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --no-build --no-restore --nologo
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter "FullyQualifiedName~AgentMonitorTests|FullyQualifiedName~AgentProcessContractsTests|FullyQualifiedName~GuiSettingsStoreTests|FullyQualifiedName~ConfigurationControlPlaneTests|FullyQualifiedName~AgentConfigurationClientTests|FullyQualifiedName~GuiConfigurationControlTests|FullyQualifiedName~RemoteHostsWindowTests|FullyQualifiedName~DashboardReadModelTests|FullyQualifiedName~PassiveMonitoringSafetyTests|FullyQualifiedName~ManagedProcessReaderTests"
```

当前 GUI 仍查询旧 RPC，仍停止其拥有的 Agent，自动恢复也仍依赖 GUI。下一步迁移恢复暂停、
后台停止/退出语义与共享变更后的即时重采样/追踪边界，再接 GUI/CLI 的缓存信封及历史。
安全日志适配、完整 CLI parity、真实双端旅程与整体 SA 验收保持未完成。

## 2026-11-01 — 被动观测与有界历史引擎

本节记录独立历史引擎阶段，当时尚未接入 Agent/IPC/GUI；后续接线现状见上节。
此前 r4 实窗证据仍只覆盖其原有范围，不代表 SA01–SA10 已经完成。

| 类别 | 实际结果与边界 |
| --- | --- |
| VIS / FLOW | 本次历史引擎没有 UI 绑定，未新增历史实窗证据。unknown/首个 CPU `--` 状态虽有读模型回归，但其受影响实窗与交互尚待接线后复验，不能由单测替代。 |
| DATA | 使用真实临时目录/JSONL/原子 marker、真实目录移动和 junction；时钟及争用窗口可控，不修改系统时间。覆盖持久重读、单写者、缺口/重启/停止追踪、损坏/残行、异 root、容量/保留期、不删非自有文件、root 移动和未来时钟校正。没有启动业务工作负载，也没有运行 WSL guest/SSH/SCM 管理操作。 |
| ENG | 最新整解决方案 Debug 构建成功，0 警告/错误；Core **89/89**，下述过滤集成 **38/38**。全量集成、Release、发布仍未运行。 |
| 资源审计 | 已完整对照 [App 资源](<src/SystemAutorun.App/App.xaml>) 与 HEAD 的差异：MaterialDesign 基础主题、保留 Primitives→Semantics→Components 的顺序、新增 Dashboard 与筛选转换器均属 r4 预期；没有遗留抽出资源字典的实验。 |
| 清理 | 测试 finally/Dispose 释放自己的 Agent、mklink 和目录。末次进程检查未见 App/Agent/Elevated/testhost。终检另发现一份较早的 owned r4 GUI fixture，核对空任务、非 PE 标记、禁用主机说明与无运行 owner 后按精确路径删除；SystemAutorun 临时 root 现为 0。`git diff --check` 无空白错误，仅既有换行提示。三份保护配置 SHA256 与本文已有记录完全一致；既有未提交修改保留，没有按公共前缀清理不明归属内容。 |

[被动监测回归](<tests/SystemAutorun.IntegrationTests/PassiveMonitoringSafetyTests.cs>) 验证只读路径
不借 WSL keepalive PID、不调用 guest `--exec`，unknown 不被改成 failure；
[资源回归](<tests/SystemAutorun.IntegrationTests/ManagedProcessReaderTests.cs>) 和
[dashboard 回归](<tests/SystemAutorun.IntegrationTests/DashboardReadModelTests.cs>) 验证首个 CPU
样本不可用、后续实测零仍有效及非匹配资源不借用。安全复核仅提供静态线索，不替代执行结果。

[投影回归](<tests/SystemAutorun.Core.Tests/MonitoringHistoryProjectionTests.cs>) 与
[存储回归](<tests/SystemAutorun.Core.Tests/MonitoringHistoryStoreTests.cs>) 共用可查合同：30 天是
最长查询/保留策略，不是最低留存保证；64 MiB 可能提前裁剪，`RetainedFrom` 为查询内首个
有效区间。查询投影移到写锁外、缓冲按查询复用，写锁允许 4 秒等待并再等 1 秒；争用测试
以可控信号量验证等待，不冒充磁盘压力基准。移动 root 后保留旧命名空间档案、另开新历史，
不采用自动删除外来 marker/历史的建议。新采样 epoch 允许时钟校正后的数据继续有效。

曾有一次隔离 Agent 在 ready 前 exit 1，未取得可复现根因；已增加 owned fatal-log 诊断，
随后独立 8 项与多次 38 项联跑通过。该间歇性启动风险保留，不能把诊断改进写成根因修复。

复现最新过滤范围：
```powershell
dotnet build SystemAutorun.sln --no-restore --nologo
dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --no-build --no-restore --nologo
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter "FullyQualifiedName~AgentProcessContractsTests|FullyQualifiedName~GuiSettingsStoreTests|FullyQualifiedName~ConfigurationControlPlaneTests|FullyQualifiedName~AgentConfigurationClientTests|FullyQualifiedName~GuiConfigurationControlTests|FullyQualifiedName~RemoteHostsWindowTests|FullyQualifiedName~DashboardReadModelTests|FullyQualifiedName~PassiveMonitoringSafetyTests|FullyQualifiedName~ManagedProcessReaderTests"
```

下一步为独立 Agent 采样与新鲜度/uptime、历史 RPC 与客户端绑定，接着迁移恢复暂停和退出
生命周期；日志安全适配、完整 CLI parity 与逐项真实旅程仍待完成。

## 2026-11-01 — r4 主屏与共享窗口增量

当前为**局部实装和隔离回归通过，整体交付仍进行中**。复用已批准 G1-r1 / 用户指定
G2-r4，不重新生成图片或扩大范围。以下结果取代旧段落中关于“尚无新主屏实装”的状态。

| 类别 | 本次实际结果与边界 |
| --- | --- |
| VIS | 已查看运行像素：常用 [1320×820](<docs/visual/sa-20261031/r4-implemented-1320x820.png>)、最小 [1120×700](<docs/visual/sa-20261031/r4-implemented-1120x700.png>)、参考 [1620×651](<docs/visual/sa-20261031/r4-implemented-1620x651.png>)；另实测 1499×700、1500×620 的高度断点。环境为 Windows / 150% DPI，截图按 DIP 等比缩小；651 高度物理取整至 977 像素。灰阶、细线图标、16 DIP 服务名、白色内部选中/焦点标记已复验，长名称和操作区未裁切。隔离内容不同于批准图的合成数值，不声称像素级一致或完整历史曲线通过。 |
| FLOW | 实测 Ctrl+F、无匹配搜索与 F5、[清除筛选恢复列表](<docs/visual/sa-20261031/r4-implemented-filter-empty.png>)、类型子菜单、右键选择/菜单、时间窗切换、Ctrl+N 添加表单切换类型并取消、主机窗口打开/Escape/焦点恢复，以及设置菜单退出。共享诊断按钮悬停无外框，程序/键盘聚焦有内部下划线。没有提交占位任务，没有执行启停、真实启动项或远程操作；完整 GUI/CLI 双端旅程仍未完成。 |
| DATA | 任务资源按 TaskName 匹配真实进程快照，30 秒陈旧/缺失/未来样本显示未知；不借用主机 CPU 或服务宿主启动时间。新增可控挂起的远程读桩与真实本机 RPC 回归：切回本机后重取本机/进程观测，远程旧响应不短暂覆盖本机身份。历史、比例与趋势仍是明确未知，未接入真实持久化。 |
| ENG | `dotnet build SystemAutorun.sln --no-restore --nologo`：0 警告/错误；Core **70/70**，过滤集成 **30/30**。包括真实 Agent 进程、共享配置 CAS/客户端、GUI 队列、资源/Loaded/草稿/严格 STA 退出及 dashboard 数据回归。全量集成、Release、发布未运行。 |
| 清理 | 隔离 GUI 三次均从自己的设置菜单退出；精确 root 的 App/Agent 终检为 0，非 PE 占位文件及 owned r4 fixture 已删除。上述 4 张证据保留，当前可确认的 15 份临时截图已删；未按公共前缀扫删归属不明的更早工具缓存。三份生产 YAML/覆盖/共享设置 SHA256 与本轮开始基线一致。 |

STA 修复保留实际 `Show` / `Loaded` / 主题断言及 10 秒严格退出检查。生产 App 默认启动
不变；同一 App 类型的内部资源测试模式不运行参数解析/Agent/原生错误弹框。曾尝试的
资源字典抽出因跨字典静态资源解析失败已撤销，应用资源仍内联在原 App 中。原阻塞的
`OnStartup → MessageBox` 路径为静态根因诊断，未抓取原阻塞栈；修复后的独立与联跑均通过。

过滤命令：
```powershell
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter "FullyQualifiedName~AgentProcessContractsTests|FullyQualifiedName~GuiSettingsStoreTests|FullyQualifiedName~ConfigurationControlPlaneTests|FullyQualifiedName~AgentConfigurationClientTests|FullyQualifiedName~GuiConfigurationControlTests|FullyQualifiedName~RemoteHostsWindowTests|FullyQualifiedName~DashboardReadModelTests"
```

剩余：独立 Agent 采样/生命周期与恢复暂停、真实有界 30 天健康/资源历史、日志查询、完整
CLI parity、双端旅程及其真实适配器失败路径。未把当前局部 VIS/FLOW 证据当作 SA01–SA10
全部验收通过。以下为较早阶段的历史结果。

## 2026-10-31 — SA-20261031 接续状态

当前已进入共享控制面的增量实施；规格、批准范围与接续入口见
[架构方案 G1-r1 / 主屏 G2-r4](<docs/architecture.md#sa-20261031--简化-gui完整-cli-与服务监测>)。
G1-r1 已批准（含最多 30 天历史与覆盖率），用户提供的 G2-r4 主屏已通过「按这个图来」
指令批准并授权实现。已实现 root/v2 隔离、物理目录归一化/生命周期文件锁、共享配置
revision/CAS API 和有界持久化操作回执；GUI 写入迁移、CLI、监测和新主屏尚未交付。
效果图不属于实装 VIS 证据。

| 类别 | 本轮状态与证据 |
| --- | --- |
| VIS | 未验证。用户指定的 [r4 主屏](<docs/visual/sa-20261031/dashboard-approved-r4.png>) 已查看、按原始 PNG 及 SHA256 归档并获批准；r1/r2/r3 未采纳。尚无新主屏实装截图。 |
| FLOW | 共享 API 管道往返已测；尚无完整 GUI/CLI 双端旅程或新界面交互证据。 |
| DATA | 隔离临时 root 的真实 YAML/覆盖/设置、CAS 和真实管道已测。新增实际 Agent 进程验证双根、目录联接、重启回执及断连后查询；任务配置均为空，未执行真实工作负载。原控制面执行器/探针仍为 fail-fast 防护桩，不能据此声称真实服务控制或监测通过。未对生产配置、启动项或远程主机执行管理操作。 |
| ENG | SDK 10.0.302；整解决方案 Debug 构建成功、0 警告/错误；Core 70/70，过滤的集成测试 10/10（设置兼容 2 + 控制面 5 + Agent 实进程 3）。全量集成测试、Release 和发布未运行。 |
| 清理 | 真实 Agent、重复候选、mklink 子进程均在测试 finally/Dispose 内关闭，owned root 删除。终检五类 endpoint/workspace/control/operation/agent-process 临时目录均为 0；App/Agent/Elevated/testhost 目标进程为 0。`git diff --check` 无空白错误，仅既有 LF/CRLF 提示。未启动 App 或真实工作负载，未做发布和真实启动项变更。 |

工作区原有大量未提交修改完整保留。本节不代表旧范围失效，也不把下述历史测试结果
当成本轮通过。主屏 G1/G2 及实施已批准；后续继续按 SA01–SA10 在本文件记录真实结果。

本轮范围：
- [Workspace 回归](<tests/SystemAutorun.Core.Tests/ConfigurationWorkspaceTests.cs>)：两个客户端基于
  同一 revision 并发只成功一个；过期设置不覆盖远程主机；同 mtime 的外部内容变化可被检测；
  添加/监测/禁用/删除、损坏设置不静默重置、未知设置保留、等待锁取消均通过。
- [控制面集成](<tests/SystemAutorun.IntegrationTests/ConfigurationControlPlaneTests.cs>)：真实管道返回
  `revision_conflict` 与当前 revision；缺失 revision/布尔字段拒绝执行；错误 root 的 shutdown
  不生效；4 条未完成请求不会阻塞 ping；Agent 缓存不再只依赖 mtime。
- 发现并修复最后一项任务删除缺陷：[编辑器](<src/SystemAutorun.Core/Configuration/ConfigurationFileEditor.cs>)
  不再把 YamlDotNet 映射起始事件的 End 当作整个映射边界，删除末项输出 `tasks: []`，保留尾部
  注释和未知顶层字段；旧配置文件解析验证仍在原子替换前执行。
- [Root / transport 回归](<tests/SystemAutorun.Core.Tests/EndpointIsolationTests.cs>)：最终路径的
  大小写/尾分隔符一致；真实目录联接得到同一端点并争用同一生命周期锁。错误身份/协议被拒绝，
  写后丢响应携带稳定操作 ID 返回未知；尚未连接的超时不声称已执行。
- [操作回执回归](<tests/SystemAutorun.Core.Tests/OperationJournalTests.cs>)：同 ID 重放、不同参数冲突、
  并发运行中查询、崩溃 Unknown、过期水位/回拨、容量拒绝、大响应省略、损坏失败关闭、UUIDv7
  校验通过。只读审查提出的无删除时前跳误锁和崩溃临时文件残留已修复，并分别用可控时钟与
  残留文件补测；不修改系统时钟。可能发生在副作用后的 IO 错误立即标为 `outcome_unknown`。
- [实进程契约](<tests/SystemAutorun.IntegrationTests/AgentProcessContractsTests.cs>)：两个独立空 root
  真正启动 Agent，联接重复候选退出；关闭一根不影响另一根。重启后以原操作 ID 和已过期 revision
  取得原结果；旧 shutdown 重放后新 PID 仍响应 ping。原始管道写后断连，能够查询完成回执并重放。
  始终设置无新控制台窗口，只操作精确 owned Process；失败时亦有超时/终止和临时目录清理。
- 验证边界：回执测试不是 exactly-once 证明；8.3、映射盘/UNC 别名及安装环境升级未逐项实测。
  操作回执不等于长期监测历史。GUI 配置迁移、旧兼容直写分支、CLI parity、30 天监测/日志与 r4
  实装均未完成；尚未采集本轮生产配置前/后哈希，不能借用下方历史哈希声称本轮比对通过。

复现命令（仅本节过滤范围，不等于整套集成验收）：
```powershell
dotnet build SystemAutorun.sln --no-restore --nologo
dotnet test tests/SystemAutorun.Core.Tests/SystemAutorun.Core.Tests.csproj --no-build --no-restore --nologo
dotnet test tests/SystemAutorun.IntegrationTests/SystemAutorun.IntegrationTests.csproj --no-build --no-restore --nologo --filter "FullyQualifiedName~AgentProcessContractsTests|FullyQualifiedName~GuiSettingsStoreTests|FullyQualifiedName~ConfigurationControlPlaneTests"
```

---

## 2026-08-12 历史验证

Date: 2026-08-12 (Asia/Shanghai)

## Automated Tests

| Configuration | Core | Integration | Result |
| --- | ---: | ---: | --- |
| Debug | 35 | 50 | 85 passed, 0 failed |
| Release | 35 | 50 | 85 passed, 0 failed |

Commands:

```powershell
dotnet test SystemAutorun.sln --configuration Debug --no-restore --nologo
dotnet test SystemAutorun.sln --configuration Release --no-restore --nologo
```

Both final runs completed without build warnings or test failures. Coverage
includes configuration validation and legacy override migration, atomic
overrides, IPC size/time/ID contracts, bounded and rotated log reads,
multi-line exception aggregation, native process launch and timeout cleanup,
Windows SCM status mapping, local host telemetry, managed-process snapshots,
read-only SSH argument and payload contracts, remote-host validation, real STA
rendering of the remote-host window, WSL execution helpers, startup management,
protected boot paths, GUI settings persistence, and task recovery behavior.

## Delivered Product Scope

- The WPF shell is a compact Windows home-server operations console using the
  project's dark neutral, restrained accent, square geometry, and dense
  information hierarchy.
- The local overview shows uptime, CPU, memory, fixed disks, active IPv4
  networks, process count, and thread count.
- Workloads support Windows programs, Windows services, WSL distributions, WSL
  systemd units, and WSL commands.
- A task can be marked `expected_running` and tracked as a user-designated
  long-running process even when it is not a Windows service. The process view
  reports matching instances, PID, start time, CPU, working/private memory, and
  thread counts.
- Exception logs aggregate timestamped multi-line events so nested exceptions
  and stack traces remain attached to one event. Active and rotated category
  logs are read with bounded memory.
- Host tabs include the local Windows machine and configured Windows or Linux
  SSH hosts. Remote probes are fixed, read-only scripts and report host,
  operating system, uptime, CPU, memory, disks, network totals, processes, and
  threads.
- SSH uses `BatchMode=yes`, `StrictHostKeyChecking=yes`, a dedicated
  `known_hosts` file, SSH Agent or private-key authentication, bounded
  connection/command timeouts, and no stored password.
- Remote settings are persisted only after the modal dialog returns an
  accepted save result. Closing the isolated inspection run did not change its
  settings hash.

## Published Artifact

- Output: `artifacts\publish\win-x64`
- Runtime: self-contained `win-x64`
- Manifest `created_utc`: `2026-08-12T19:00:54.7138016+00:00`
- Manifest payload entries: 681
- Actual payload files: 681
- Actual files including the manifest: 682
- Manifest payload bytes: 322,829,241
- Missing files: 0
- Size mismatches: 0
- SHA256 mismatches: 0
- Extra payload files: 0
- PDB files: 0
- Staging entries after publish: 0

The App, Agent, and Elevated entry points are self-contained x64 Windows GUI
executables. The final manifest was rechecked file by file after the test runs.

Publish command:

```powershell
.\scripts\publish-native.ps1 -NoRestore
```

## Runtime And UI Smoke

The final isolated UI run used the published App PID `38624` and its direct
child Agent PID `22000`. The disposable root had zero tasks and one disabled
Linux sample host, so it did not start a workload or attempt SSH.

- Both executables resolved to `artifacts\publish\win-x64`.
- Both processes were responsive and the Agent was a direct child of the App.
- The App title was `SystemAutorun · Windows 家庭服务器控制台`.
- Neither process owned a TCP or UDP endpoint.
- Overview, disabled remote-host state, managed-process navigation, and the
  exception-log page were inspected in the running release.
- The exception page showed one aggregated historical event and retained its
  complete stack trace in the detail view.
- The remote-host form rendered the server list, Windows/Linux platform,
  address, port, user, SSH Agent/private-key choice, dedicated `known_hosts`,
  timeouts, enabled state, and security notice. The STA integration test also
  showed and laid out the real WPF window and verified selected-row field
  population.
- A system error prompt visible in an earlier capture belonged to a previous
  `ProgressBar.Value` binding crash. That binding had already been changed to
  one-way mode. No new `.NET Runtime`, `Application Error`, or Windows Error
  Reporting event appeared from the isolated run's recorded start onward.

Protected real-file hashes remained unchanged during isolated validation:

| File | SHA256 |
| --- | --- |
| `autorun.yaml` | `1E78094F101E46CB90565A52E2DAC614F322AB51BD63C428A6EFC268912ED64C` |
| `autorun.overrides.json` | `473F5D160BD406838CE28B95BE983075992F20C84D9E2CBBB6924544BA1CC65E` |
| `autorun.gui.settings.json` | `4162BAE103AECEA8342092F4239FEEBF809E95DA8FD7A8458FB19F292B06846F` |

## Cleanup

- Agent PID `22000` accepted a named-pipe `shutdown` request and returned the
  exact request ID before exiting.
- The App had an open modal window, so `CloseMainWindow()` was not accepted.
  Its already verified PID `38624`, executable path, and isolated-root command
  line were checked again before exact-PID termination.
- The complete isolated process tree was confirmed exited.
- `.codex\ui-smoke-final` and `.codex\ui-verification-20260813` were checked for
  absolute containment and reparse points, then removed.
- No isolated SystemAutorun process or network endpoint remained.
- `git diff --check` reported only line-ending normalization warnings and no
  whitespace errors.

## Retained Manual Inspection Run

The published application is intentionally still running against the real
project root for user inspection:

- App PID: `40668`
- Direct-child Agent PID: `31076`
- Window: responsive, titled
  `SystemAutorun · Windows 家庭服务器控制台`
- Named-pipe `ping`: `ready`, reporting Agent PID `31076`
- Read-only task list: five enabled tasks, all five healthy
- Host snapshot: `THEBLUETREE`, seven fixed drives, two active networks
- Managed-process rows: zero because no current task has
  `expected_running: true`
- Exception snapshot: 11 aggregated entries from 17 log sources, not truncated
- TCP/UDP endpoints owned by the retained App and Agent: zero
- New `.NET Runtime`, `Application Error`, or WER events since launch: zero

The three real configuration hashes remained unchanged after these probes.
Use **File > Exit** to stop the retained App; its owned Agent is shut down by
the application lifecycle.

## Deliberately Not Performed

- No real configured workload was started, stopped, restarted, enabled, or
  disabled.
- No Windows service, WSL distribution, systemd unit, or WSL command state was
  mutated.
- No real Windows or Linux SSH host was available, so a live SSH connection is
  not claimed.
- No Task Scheduler install/run/uninstall, ProgramData deployment, UAC
  installed-environment test, or reboot-recovery test was performed.
- No Git stage, commit, push, or remote release was performed.

An administrator installed-environment smoke, a reboot test, and live SSH
checks against representative Windows and Linux servers remain required before
unattended production use.
