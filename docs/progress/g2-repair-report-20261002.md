# G2 首轮审核返修报告

日期：2026-10-02，Asia/Shanghai  
状态：返修完成，待负责人再次审核；不进入 G3

## 审核问题与修复范围

本报告对应 `docs/progress/supervisor-g2-review-20261002.md` 的四项问题，未修改 `docs/04-负责人监督记录.md`，未改变 WPF/Canvas 技术方案，也未加入交易、PnL、手续费、行情、通知或新指标。

1. 修正 `Context(... indicator_state=state)` 调用；回放 Worker 现在持续读取 stderr 到 `worker-stderr.log`，错误消息包含策略异常、当前 bar 起止和 stderr 尾部，标准输出写入 `worker.log`。
2. WPF 增加策略加载/描述、参数 JSON、正式起止、严格/探索、配置检查、运行中锁定、已有 run 只读重开、类别/方向/时间筛选、DataGrid 信号表、CSV 导出、Canvas 信号标记和表格点击定位。
3. 增加 `warmup_bars_parameters` 约定；实际预热为 `max(warmup_bars, 参数声明值)`。正式区间无已完成覆盖或预热不足时为 `blocked_coverage`，不再返回“completed/0 signals”。预热信号不写入正式信号文件和正式统计。
4. Describe、模块加载、工厂初始化、Worker 协议等待、on_bar 超时和取消均有期限；取消/失败/超时的 finally 会杀掉本任务进程树、等待退出、排空 stderr，并记录 PID/退出状态。`stderr` 持续异步消费，避免策略大量打印堵塞。

## 当前源码复现证据

固定快照：`artifacts/g2-e2e-2026100214b/multi/snapshots/snapshot-a762946b720ae114`。  
标准正式区间：`2026-10-01T09:03:00+08:00` ～ `2026-10-01T09:05:00+08:00`，左闭右开；前三根为预热，后两根为正式覆盖。

命令模板：

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location) 'tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$cli = Join-Path (Get-Location) 'src\Qh.Cli\bin\Release\net10.0\Qh.Cli.dll'
& $dotnet $cli replay artifacts\g2-e2e-2026100214b\multi snapshot-a762946b720ae114 strategies\sma_ema_cross.py 2026-10-01T09:03:00+08:00 2026-10-01T09:05:00+08:00
```

当前源码输出：`status=completed; signals=2`，run 为 `sig-20261002061248134-133316ff9`。该 run 的 `worker.log` 显示 ready、started、3 根 `formal=false` 预热、2 根正式信号、completed；正式信号为 `cross_down@09:04` 和 `cross_up@09:05`；Worker PID 34736 复核为不存在。

## 覆盖、预热和只读产品入口证据

| 场景 | 结果 | run/证据 |
|---|---|---|
| 正式区间无覆盖：08:00～08:01 | `blocked_coverage`，原因“正式区间没有已完成 K 线覆盖” | `sig-20261002055654759-0d6d588ed` |
| 参数预热不足：fast=3、slow=5，正式从09:00开始 | `blocked_coverage`，需要5根、可用0根 | `sig-20261002055655120-cb6a1a2b5` |
| 预热信号排除 | fixture 产生3根预热信号、2根正式信号；记录为 `WarmupSignalCount=3`、`FormalSignalCount=2` | `sig-20261002060734881-7b65d5748` |
| 历史 run 只读重开 | `SignalRunStore.ReadRecord/ReadSignals` 只读加载，筛选不重跑 | CLI `read-run`；WPF `打开已有 run` |
| 类别/方向/时间筛选 | Core `Filter` 与 WPF DataGrid 筛选；导出使用当前显示集合 | `SignalRunStore.ExportCsv(IEnumerable<SignalEvent>)` |
| 图表联动 | Canvas 绘制方向标记；点击 DataGrid 行移动 viewport 到对应 bar_start | `SignalGrid_SelectionChanged`、`DrawChart` |

## 进程管理实测

| 路径 | 结果 | 耗时/退出证据 |
|---|---|---|
| on_bar 超时 | `failed`，`ErrorKind=timeout`，当前 bar 起止写入错误 | `sig-20261002060735387-3df872c00`，约2531ms；PID 29348 后 `Get-Process -Id 29348` 不存在 |
| 工厂初始化挂起 | `failed`，`ErrorKind=protocol_timeout` | `sig-20261002060737983-d216ab4c2`，约5569ms；PID 19316 后不存在 |
| 模块加载挂起 Describe | `TimeoutException`，策略加载进程被取消/杀掉 | `init_hang.py`，约10.2s；PowerShell `Get-CimInstance Win32_Process` 未发现残留该策略命令行 |
| 大量 stderr | 回放 `completed`，不堵塞；stderr 文件约20KB，Worker 日志保留尾部 | `sig-20261002055834214-c2d770e8f`，`worker-stderr.log` 20575 bytes |
| Replay 取消 | `cancelled`，不标成功；Worker 返回 `cancelled`，PID 后不存在 | `sig-20261002060408505-c9f21286d`，约300ms触发取消，日志 `exit=0` |
| Describe 取消 | API CancellationToken 实测返回 `cancelled` | `G2Probe3` PowerShell probe，约592ms；未留下 `init_hang.py` 进程 |

说明：成功 run 的 Worker 日志包含 `pid=...` 和 `exit=0`；失败/杀进程路径的最终进程是否残留以 PowerShell `Get-Process` 复核为准，不把 Python 的异常退出码误写成业务成功。P06 的正常取消目标约2秒、失联协议目标5秒已分别落实为取消等待约1秒和协议等待5秒；长时间稳定性、规模性能仍未验证。

## 构建与源码摘要

```powershell
& $dotnet build src\Qh.Cli\Qh.Cli.csproj --configuration Release --nologo
& $dotnet build src\QhApp\QhApp.csproj --configuration Release --nologo
python -m py_compile worker\qh_signal_worker.py strategies\sma_ema_cross.py strategies\preheat_signal.py
```

结果：CLI/WPF 均 0 warning、0 error；Python 语法检查通过。返修后源码 SHA-256：

- Worker：`37AF31265BD31DAC6E5BE2A1007F15FFF2E07C006FACD5F399E32D038AFFBBFF`
- Runner：`4AF7CAB2431AA4738BC01B810FB757E943A921083A53C5CA1B4E85DD09B5DC46`
- MainWindow.cs：`AD54F5E57F2633377250501F6A98944B26B2D132C029A8D4FA7FCD65AFB06DDF`

复核命令：

```powershell
Get-FileHash worker\qh_signal_worker.py,src\Qh.Core\SignalReplayRunner.cs,src\QhApp\MainWindow.xaml.cs -Algorithm SHA256
```

## 未验证项与结论

- 原生 Windows UI 实际点击、截图和打包安装仍未验证；本次只完成产品入口源码和 WPF 构建，不能把编译当作 UI 走查通过。
- P02/P03 大规模性能、长时间稳定性、其它用户 Python 进程隔离压力仍未验证。
- 当前 G2 返修范围已完成，结论为“待负责人再次审核”，不进入 G3。
