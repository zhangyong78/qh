# G1 返修报告：负责人审核问题修复

日期：2026-10-02，Asia/Shanghai  
返修范围：负责人 `supervisor-g1-review-20261002.md` 列出的 9 项问题  
阶段状态：待负责人审核；不进入 G2

## 1. 修复摘要

| 问题 | 修复 |
|---|---|
| SMA 使用未来数据 | `Indicators.Calculate` 改为滚动和，窗口只读取已到达的 N 根；增量 O(n)，不再对 Skip 后的全部剩余行 Average。 |
| 混合合约/周期 | `CsvImporter` 固定首条有效记录的 symbol/exchange/period；后续不一致记录生成 `mixed_series`，严格拒绝发布。 |
| 中断目录假成功 | `DataStore.PublishAsync` 使用唯一临时目录生成 CSV、Parquet、source 和 `snapshot.meta`，完整后目录提交；既有不完整目录先隔离为 `.incomplete-*` 再重建；完整目录须有摘要和非空 Parquet 才复用。 |
| 缺口误报/漏报 | 引入版本化测试时段表 `cn-futures-g1-test-v1`：09:00–10:15、10:30–11:30、13:30–15:00、21:00–23:00、23:00–02:30。只在同一显式时段内按周期报告任意长度缺口，时段边界不报告。 |
| UI 线程占用 | 导入、快照查询、指标计算移入 `Task.Run`；UI 线程只更新状态和绘图。移除每行 `bars.Keys.Max()`，改为增量最大时间。 |
| 图表交互缺失 | 仅绘制可见窗口（初始最多 240 根）；增加滚轮缩放、左键拖动平移、价轴标签、十字竖线/横线、OHLC/价轴提示。人工体验仍不冒充通过。 |
| Worker 生命周期 | 捕获启动异常；输出/退出事件使用 `BeginInvoke`；停止/关闭异步等待，超时清理进程树；异常退出显示退出码；Worker stdin EOF 主动退出，避免父进程异常时孤儿。 |
| G0 下穿样例错误 | 改为收盘 `12,14,13,9`：第 3 根快 13.5/慢 13，第 4 根快 11/慢 12，形成首次下穿。 |
| 无偏移时间 | 仅带 `Z` 或 `±HH:mm/±HHmm` 后缀时按显式偏移解析；其它时间先按 `DateTime` 未指定 Kind，再强制按 Asia/Shanghai +08:00。 |

## 2. 关键回归证据

### 2.1 指标前缀不变性

命令：

```powershell
& "$env:DOTNET_ROOT\dotnet.exe" run --project 'src\Qh.Cli\Qh.Cli.csproj' --configuration Release --no-build -- indicator-check
```

结果：

```text
sma_full_bar3=11; sma_prefix_bar3=11; prefix_invariance=True
sma=NA,NA,11,11.333333333333333333333333333,11,10
indicator_exit=0
```

这与 `10,11,12,11,10,9`、N=3 的预期一致。

### 2.2 数据正确性夹具

构建/运行根目录：`D:\mycode\qh\artifacts\g1-repair-20261002115719`。

| 夹具 | 实际结果 |
|---|---|
| `sample-bars-mixed-series.csv` | `published=False`，`mixed_series=1`，退出码 1；不再把 au2601 当 rb2601 发布。 |
| `sample-bars-no-offset.csv` | 发布成功；DuckDB 往返首尾均为 `2026-10-01T09:00:00+08:00`。 |
| `sample-bars-gap-30m.csv` | `suspected_gaps=1`，09:00→09:30 报约 29 根缺口。 |
| `sample-bars-scheduled-rest-5m.csv` | `suspected_gaps=0`，10:10→10:30 被识别为时段边界。 |
| 中断目录重试 | 预先只放 `bars.csv`；重试成功后查询读回 8 行，目标目录包含 `bars.csv`、`bars.parquet`、`source.txt`、`snapshot.meta`，旧目录被隔离为 `.incomplete-*`。 |

原有正常、重复、冲突、未完成夹具继续适用；完整快照复用必须通过 `snapshot.meta` 摘要和非空 Parquet 检查。

### 2.3 Worker 生命周期

PowerShell 实测消息序列：

```text
ready → started → heartbeat → cancelled → stopped(reason=stdin_closed)
hasExited=True; exitCode=0
worker_processes_remaining=0
```

stdio JSON 保留为 G1 本机最小协议，取舍是实现简单、可审计、无需额外消息服务；后续如需跨进程高可靠传输再按原方案评估 Named Pipes，不在本轮扩大范围。

## 3. 构建验证

```powershell
& "$env:DOTNET_ROOT\dotnet.exe" build 'src\Qh.Cli\Qh.Cli.csproj' --configuration Release --nologo
& "$env:DOTNET_ROOT\dotnet.exe" build 'src\QhApp\QhApp.csproj' --configuration Release --nologo
& "$env:DOTNET_ROOT\dotnet.exe" publish 'src\QhApp\QhApp.csproj' --configuration Release --no-restore --output 'artifacts\g1-repair-app-20261002115803' --nologo
```

结果：CLI/WPF build 均 0 warnings、0 errors；publish 成功。Python Worker 使用 AST 语法检查通过。

## 4. 验收状态

| 编号 | 状态 | 说明 |
|---|---|---|
| G1-DATA-01 | 通过实施验证，待负责人复核 | 目录、CSV 最低字段和 Worker/数据根目录均明确。 |
| G1-DATA-02 | 通过实施验证，待负责人复核 | 混合序列、时区、乱序、缺口/休市和重复/冲突有回归输出。 |
| G1-DATA-03 | 通过实施验证，待负责人复核 | 完整临时目录提交、中断目录隔离重建、Parquet/DuckDB/SQLite 可复核。 |
| G1-DATA-04 | 代码实现，人工 UI 未验证 | 后台计算、可见窗口绘制、缩放/平移/十字光标/价轴已实现；当前环境原生 UI 工具不可用，未声称人工体验通过。 |
| G1-DATA-05 | 通过协议验证，待负责人复核 | 取消、stop、EOF、异常退出路径已处理；WPF 关闭需人工走查。 |

## 5. 未验证事项

- 当前环境仍无法提供原生 WPF 截图/点击，因此人工布局、滚轮手感、拖动手感和关闭确认对话框未验证。
- 真实交易日历、TqSdk、真实行情、长时间运行、打包后无开发工具环境、P02/P03 性能目标未验证。
- 不进入 Python 策略、TqSdk、通知和交易回测关卡。

## 6. 返修结论

负责人指出的 9 项问题已按 G1 范围修复并有对应回归证据。当前状态保持“待负责人审核”；不修改 `docs/04-负责人监督记录.md`，不自行放行 G2。
