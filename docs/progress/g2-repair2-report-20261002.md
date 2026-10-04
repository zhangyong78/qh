# G2 第二轮审核返修报告

日期：2026-10-02，Asia/Shanghai  
状态：返修完成，待负责人再次审核；不进入 G3

对应审核：`docs/progress/supervisor-g2-repair-review-20261002.md`。本轮未修改 `docs/04-负责人监督记录.md`。

## 四项修复

### 1. 参数入口与策略选择

- WPF 移除 fast/slow/average 的示例专用编辑控件，只保留当前策略的通用参数 JSON 作为唯一输入源。
- `BuildParametersOrDefaults` 只合并当前 `StrategyDescriptor.Parameters` 的默认值和 JSON 对象，不再无条件注入示例参数。
- 选择路径、手动改路径、文件描述失败都会使旧 descriptor 失效并清空结果上下文；回放前总是重新 Describe 当前文件，重新校验路径、元数据和 SHA-256。
- 加载期间路径变化不会把旧描述应用到新文件；无参数 `preheat_signal.py` 和参数化 `period_signal.py` 均已回归。

### 2. 历史 run 与快照身份

- `DataStore.GetSnapshots()` 复用现有 SQLite 快照清单；WPF 增加本地快照选择入口。
- `OpenRun` 读取 `run.json` 的 `SnapshotId`，先加载绑定快照，再加载只读信号；找不到快照直接报错。
- 信号表和 Canvas 标记都核对当前快照 ID、合约和 bar 时间；没有匹配 K 线时不会把索引默认为 0，也不会报告定位成功。
- 只读结果视图明确显示绑定快照，不改写原 run 的正式区间/参数；点击“开始回放”先退出只读视图，再进入新研究。

### 3. 策略加载生命周期

- 追踪 `_strategyLoadOperation` 与 `_strategyLoadCancellation`。
- 重复加载被拒绝，取消按钮和窗口关闭都会取消/等待 Describe 任务。
- 旧任务完成后不会覆盖当前策略；关闭确认同时覆盖导入、查询、回放、策略加载和已有 G1 Worker。

### 4. Python traceback 与实时 stderr

- Worker 对模块加载、工厂初始化、参数和 `on_bar` 异常发送 `strategy_path` 与 `traceback`。
- stderr 使用 `FileShare.ReadWrite | FileShare.Delete` 持续排空，父进程先完成 Worker 收尾，再读取最终 stderr 尾部。
- `run.json` 的错误详情现在同时包含策略副本路径、Python 文件/行号、bar 起止、traceback、stderr 标记和最终 stderr 尾部。

## 当前源码回归证据

固定快照：`artifacts/g2-e2e-2026100214b/multi/snapshots/snapshot-a762946b720ae114`。  
正式区间使用 `2026-10-01T09:03:00+08:00` ～ `2026-10-01T09:05:00+08:00`。

| 场景 | 结果 | run |
|---|---|---|
| 无参数策略 | `completed`，2 条正式信号 | `sig-20261002062613153-0c3444fb9` |
| 参数化 period=4 | `completed`，1 条正式信号；预热需求按参数值计算 | `sig-20261002062613679-4fcd98f49` |
| 指定行运行异常 + stderr | `failed`；错误含 `strategy.py line 18`、traceback、`STDERR_MARKER_G2_RUNTIME` 和 `stderr_final` | `sig-20261002062754702-8e87f3e98` |
| 标准均线交叉 | `completed`，2 条正式信号；3 根预热不计正式统计 | `sig-20261002061248134-133316ff9` |
| 无覆盖/预热不足 | 均为 `blocked_coverage` | `sig-20261002055654759-0d6d588ed`、`sig-20261002055655120-cb6a1a2b5` |
| Replay 取消/超时/工厂挂起 | 分别为 `cancelled` / `failed` / `failed`，相关 PID 外部复核不存在 | 第二轮审核既有独立复测；本轮链路未放宽 |

stderr 错误 run 的 `run.json` 实际包含：

```text
strategy=...\strategy.py
... line 18, in on_bar
raise RuntimeError("fixture traceback error")
stderr=STDERR_MARKER_G2_RUNTIME
stderr_final=STDERR_MARKER_G2_RUNTIME
```

## 构建与源码摘要

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location) 'tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet build src\Qh.Cli\Qh.Cli.csproj --configuration Release --nologo
& $dotnet build src\QhApp\QhApp.csproj --configuration Release --nologo
python -m py_compile worker\qh_signal_worker.py strategies\stderr_error.py strategies\period_signal.py
```

结果：CLI/WPF 均 0 warning、0 error；Python 语法检查通过。

返修后 SHA-256：

- Worker：`1C6A56F85C4F677042BF67A4EE2F94AF9AE5D0EF6A21DABC02C2A59BCC4C013E`
- Runner：`F7E9A121830A32CE180C1E27DA3E136E6841FE534A243C1E8274C514C6ED7893`
- MainWindow.cs：`F5DAE1134145822792AF336234E32A0E03D99B91D05707453E4C05D2A932E308`
- DataStore.cs：`500123D66CDAFE790B67DDCEC75B5D6935C93D75BBEAA40D96279CEE67E0E57D`

`docs/04-负责人监督记录.md` 当前摘要：`F48244137B1C824104AF53DC006809864FE69046454585FC3ABB096A27E29878`，本轮未修改。

## 仍未验证

- 原生 Windows 实际点击、截图、打包安装仍未验证；源码入口与构建通过不等同于人工 UI 通过。
- P02/P03 规模性能、长期稳定性仍未验证。
- 工厂挂起从请求到结束约 5.47 秒，不能宣称 P06 五秒目标通过。

结论：第二轮返修完成，保持 G2，待负责人再次审核，不进入 G3。
