# G1 阶段报告：本地 CSV、数据快照、K 线图表与进程骨架

日期：2026-10-02，Asia/Shanghai  
阶段：G1  
状态：待负责人审核  
范围：离线本地数据链路；不接 TqSdk、不执行策略、不做真实通知/下单

## 1. 交付物

| 交付物 | 路径 |
|---|---|
| 运行说明 | `README.md` |
| G0 环境/语义报告 | `docs/progress/g0-report.md` |
| G0 固定夹具 | `fixtures/g0-indicator-expected.md` |
| 当前状态 | `docs/progress/current-status.md` |
| WPF 应用 | `src/QhApp/`；publish 输出 `artifacts/g1-app/` |
| CSV/Parquet/SQLite 核心 | `src/Qh.Core/` |
| 离线验证 CLI | `src/Qh.Cli/` |
| Python Worker 协议 | `worker/qh_worker.py` |
| 可重复 CSV 夹具 | `fixtures/sample-bars.csv` 及 duplicate/conflict/incomplete/order-gap 变体 |

## 2. 实现内容

1. CSV 严格字段映射：时间、合约、交易所、周期、显式交易日、OHLC、成交量、持仓量、完成状态。
2. 时间统一为 Asia/Shanghai；显式 `trading_day` 不由自然日期静默推断。示例中的 23:00 和次日 01:00 均保留同一交易日字段。
3. 质量检查：空文件/缺字段、时间解析、OHLC 范围、非法量、重复、内容冲突、未完成 K 线、输入乱序和已知短间隔疑似缺口。
4. 通过检查后按规范化内容 SHA-256 生成快照 ID；`bars.csv` 与 `bars.parquet` 在快照目录内原子发布，SQLite `snapshots` 表记录范围、行数、来源、摘要和创建时间。
5. UI 从 DuckDB `read_parquet` 读取快照，显示 K 线、成交量、SMA(3) 点、时间范围和十字光标文本；未导入时保留离线入口。
6. WPF 启动 Python Worker，Worker 只提供 `ready/started/heartbeat/cancelled/stopped`，不加载用户策略。取消后等待确认，退出时发送 stop，超时才清理子进程。

## 3. 构建/发布命令与结果

使用项目隔离 SDK：`D:\mycode\qh\tools\dotnet\dotnet.exe`，版本 .NET SDK `10.0.401`。

```powershell
$env:DOTNET_ROOT = (Join-Path (Get-Location) 'tools\dotnet')
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
& "$env:DOTNET_ROOT\dotnet.exe" build 'src\Qh.Cli\Qh.Cli.csproj' --configuration Release --nologo
& "$env:DOTNET_ROOT\dotnet.exe" build 'src\QhApp\QhApp.csproj' --configuration Release --nologo
& "$env:DOTNET_ROOT\dotnet.exe" publish 'src\QhApp\QhApp.csproj' --configuration Release --no-restore --output 'artifacts\g1-app' --nologo
```

结果：核心/CLI 构建成功，WPF 构建成功，均 `0 warnings / 0 errors`；publish 成功，输出目录为 `D:\mycode\qh\artifacts\g1-app`，包含 17 个文件及 `worker/qh_worker.py`。

## 4. 离线验证证据

### 4.1 正常导入与往返

命令：

```powershell
& "$env:DOTNET_ROOT\dotnet.exe" run --project 'src\Qh.Cli\Qh.Cli.csproj' --configuration Release --no-build -- import 'fixtures\sample-bars.csv' 'artifacts\g1-final-20261002113203\sample'
```

结果：`published=True`；快照 `snapshot-798cd202583749aa`；输入 8 行、发布 8 行、重复/乱序/冲突/非法/未完成均为 0；DuckDB 从 Parquet 读回 8 行；首时间 `2026-09-30T21:00:00+08:00`，末时间 `2026-10-02T01:00:00+08:00`；SMA 就绪点 6。目录中实际存在 `index.sqlite`、`bars.csv`、`bars.parquet`、`source.txt`。

### 4.2 重复、冲突、未完成

`fixtures/sample-bars-duplicate.csv` 两次导入同一数据根目录：两次快照 ID 均为 `snapshot-b8fe797cacba7eb4`，发布候选均为 2 行，重复数为 1，未翻倍。

`fixtures/sample-bars-conflict.csv`：`published=False`、冲突数 1、CLI 退出码 1，无快照发布。  
`fixtures/sample-bars-incomplete.csv`：`published=False`、未完成数 1、CLI 退出码 1，无快照发布。

### 4.3 乱序与疑似缺口

`fixtures/sample-bars-order-gap.csv`：输入乱序数 1，疑似缺口数 1；仍按排序后的 4 行发布，DuckDB 读回首尾顺序正确。休市/跨日大间隔不按短间隔规则静默造价填充。

### 4.4 Worker 协议

使用 PowerShell 启动 `python worker\qh_worker.py`，发送 `run/cancel/stop`，收到：`ready`、`started`、`heartbeat`、`cancelled`、`stopped`，最终 `hasExited=True; exitCode=0`。未发现残留 `qh_worker.py` 进程。

## 5. G1 验收项

| 编号 | 状态 | 证据 |
|---|---|---|
| G1-DATA-01 | 通过（离线） | 目录、CLI、`artifacts/g1-final-20261002113203/sample` |
| G1-DATA-02 | 通过（CLI） | 正常/冲突/未完成/乱序/缺口夹具输出 |
| G1-DATA-03 | 通过（CLI） | Parquet 实际生成、DuckDB 实际读回、SQLite 清单实际生成 |
| G1-DATA-04 | 实现完成，UI 人工视觉未验证 | `src/QhApp/MainWindow.xaml(.cs)`；WPF 进程可启动，但本机 CUA 返回 Native computer APIs disabled，未取得截图/点击证据 |
| G1-DATA-05 | 通过（协议） | Worker 状态时间线与退出码证据 |

## 6. 产品验收映射

| 编号 | 状态 | 说明 |
|---|---|---|
| A01 离线入口 | 实现；人工 UI 未验证 | CLI 和 WPF 入口存在；无账号不阻断本地导入 |
| A03 重复导入 | 通过离线验证 | 内容相同重复不增加行数，冲突拒绝发布 |
| A04 时间/休市口径 | 部分通过，待负责人走查 | 显式交易日、时区往返和跨午夜夹具已验证；真实品种交易日历尚未接入 |
| A05 图表与联动 | 未验证 | K 线/成交量/SMA/十字光标代码已实现；因原生 UI 接口禁用未人工确认视觉和点击 |
| A16 进程及退出清理 | 通过协议/进程检查 | Worker stop/exit 和取消确认已验证；WPF 关闭确认流程待人工 UI 走查 |

P02 本地 1 万根首次显示、P03 后台交互不卡顿：未测量。当前数据是小型合成 CSV，不代表真实行情或性能达标。

## 7. 未验证事项与不阻断项

- WPF 视觉布局、鼠标滚轮缩放、拖动平移、十字光标在真实桌面上的人工体验：未验证，原因是当前 Computer Use 原生窗口接口被环境禁用；WPF 进程本身已成功启动并响应。
- TqSdk 账号/权限/覆盖/实时完成状态：未验证，G1 设计上不依赖账号。
- 真实交易数据授权、真实通知、无开发工具安装环境、长时间运行、P01/P02/P03/P04/P05/P06/P07/P08：未验证。
- ScottPlot 尚未引入；G1 使用轻量 WPF Canvas 完成首轮图表原型，避免在无法人工验收的情况下扩大依赖。若负责人要求严格按 ScottPlot 视觉实现，应在 UI 可走查后单独安排，不在本轮扩展。

## 8. 关卡结论

G1 的可离线数据和 Worker 协议闭环已实现并有命令证据；UI 视觉和交互人工证据受环境限制未补齐。按文档要求，本阶段标为“待负责人审核”，不宣称 A05 完整通过，不进入 G2。负责人可复核报告、源码和 `artifacts` 证据后决定放行或返修。
