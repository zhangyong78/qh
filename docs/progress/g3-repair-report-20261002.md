# G3 首轮审核返修报告（第二轮最小返修补充）

日期：2026-10-02，Asia/Shanghai  
范围：仅修复 `docs/progress/supervisor-g3-review-20261002.md` 指出的 G3 问题；不修改 `docs/04-负责人监督记录.md`，不接入账号、行情、通知、CTP 或实盘。

## 1. 五组返修

### 1.1 手数、手续费、价格和保证金

- 固定手续费现在按实际成交数量计算；比例手续费按成交名义金额和数量计算。
- `TradingConfig.Lots` 表示每个策略数量单位对应的实际手数；策略平仓必须按该语义提交，首版只支持全仓平仓，数量不一致返回 `close_quantity_mismatch_full_position_only`，不静默忽略。
- 开仓/平仓滑点后的尝试价必须大于0；否则记录 `invalid_fill_price` 风险事件和未成交原因，不写入成交、费用或持仓。
- 保证金超限同时记录 `margin_exceeded` 与 `insufficient_funds`，不模拟强平、不追价。

证据：

| 案例 | 结果 | 证据 |
|---|---|---|
| Lots=2 | 毛40、费用10、净30、权益10030，成交数量2/2 | `artifacts/g3-f01/trade-runs/trd-20261002082929350-8f049eb9c` |
| 无效负价 | 0成交/0费用/权益10000，`invalid_fill_price` | `artifacts/g3-short/trade-runs/trd-20261002083019553-bf909a575` |
| 资金不足 | 0成交且 risk-events 留存保证金超限/资金不足 | `artifacts/g3-insufficient/trade-runs/trd-20261002073351512-5c01f08f7` |

### 1.2 下一根撮合、覆盖门禁、模拟上下文和示例策略

- 待成交项改为同一下一根有效 K 线的有序集合；同根多个意图全部在该开盘依序尝试，不跨 bar 排队。
- 交易回放复用 G2 `EvaluateCoverage`：warmup、参数相关预热、正式完成 K 线覆盖不足时为 `blocked_coverage`。
- Worker Context 新增只读 `simulation.position` 与 `simulation.account`，由 C# 撮合器提供，策略不能写回账户。
- 新增 `strategies/sma_ema_cross_trade.py`，按既有 SMA/EMA 交叉信号示例输出 open/reverse 意图；没有新增策略体系。

证据：

| 案例 | 结果 | 证据 |
|---|---|---|
| 同根 open+close | 同一 bar_end 102/102、毛0、费5、净-5、2成交 | `artifacts/g3-f01/trade-runs/trd-20261002082940016-904b887e2` |
| warmup=10 | `blocked_coverage`，需要10、可用0 | `artifacts/g3-f01/trade-runs/trd-20261002083006564-8dd623132` |
| SMA/EMA 交易示例 | 预热4/3、正式1，completed；策略通过只读模拟上下文 | `artifacts/g2-e2e-2026100214b/multi/trade-runs/trd-20261002083743240-d931b3682` |

### 1.3 取消、异常和进程收尾

- 取消/Worker 异常/超时现在统一从内存撮合状态更新 `run.json`：已成交数量、手续费、已实现/浮动权益、回撤和最后处理根数不会回落为零。
- 尚未尝试的 pending intent 记录 `cancelled_not_attempted` 或 `failed_not_attempted`。
- 取消向 Worker 发送 `cancel` 并等待确认，超时才杀进程；run 目录始终独立，不覆盖旧 run。

证据：

`artifacts/supervisor-g3-20261002/cases/perf-1000/trade-runs/trd-20261002090838521-c9c97ff2d` 为 `cancelled`，已有1成交、费用2、最终权益9998；Worker 日志与 PID 收尾均可查。此前同一 run 目录下的审核旧证据保留，不被覆盖。

### 1.4 Windows 结果交互

- WPF 使用 `EquityCanvas` 绘制权益曲线，最多采样1200个点；完整 `equity.jsonl` 仍保存，打开百万结果不会拼接全部数字。
- 信号使用圆形标记，成交使用橙/紫方形标记；信号表和成交表分别点击定位到对应 K 线。
- 交易 run 重开先加载其 `SnapshotId`，再加载只读成交、信号和权益采样；缺失绑定快照时拒绝打开。
- 增加交易明细读取：订单、持仓、未成交、风险事件和 Worker 日志最多展示200行；结果保存配置、策略副本、质量、假设和版本。
- 增加复制配置到新 run、成交 CSV、权益 CSV、摘要 JSON 导出；信号与交易按钮共享任务锁定与窗口关闭等待。
- UI 人工原生点击仍未执行；上述路径已通过 WPF 编译和离屏结构/事件绑定检查，不能替代人工走查。

### 1.5 剩余夹具和性能

| 案例 | 结果 | 证据 |
|---|---|---|
| 期末未平仓 | 浮盈80、费用2、净78、权益10078、无强平 | `artifacts/g3-end-open/trade-runs/trd-20261002083853539-a63bc79f1` |
| 跳空不利滑点 | 买111、浮亏-10、费用2、权益9988 | `artifacts/g3-gap/trade-runs/trd-20261002083854105-223f13e65` |
| 完成线止盈 | 毛10、费5、净5、权益10005 | `artifacts/g3-take/trade-runs/trd-20261002083854646-ae1207de2` |
| 严格缺口 | `blocked_quality`，gaps=1 | `artifacts/g2-e2e-2026100214b/gap/trade-runs/trd-20261002083855210-4f701249e` |
| 探索缺口 | completed，成交只使用实际下一根，run 保留 `DataIncomplete=true` | `artifacts/g2-e2e-2026100214b/gap/trade-runs/trd-20261002083855614-fbcfad638` |

### P04 百万根实测

输入：`artifacts/supervisor-g3-repair-20261002/perf-1000000-daily.csv`，1,000,000 根完整合法合成日线；导入快照 `snapshot-e7d5b1de50f8d24b`，0 重复/乱序/非法/疑似缺口。固定 Python 策略：`strategies/perf_zero_trade.py`，零意图但每根仍经 Python Worker；首次导入不计入回放目标。

| 阶段 | 实测 |
|---|---:|
| 首次导入 | 6573 ms（不计入 P04） |
| 原实现百万回放 | 1066345 ms，Worker 完成 exit=0；外层初始包装器因输出排空被手工结束，run 已完整 completed |
| 最小优化后最佳回放 | 74422 ms，exit=0 |
| 最小优化后最终对照 | 84455 ms，exit=0 |
| 最佳峰值外层工作集 | 约 544,395,264 bytes |

根因是逐根 Python JSON IPC、每根策略超时隔离线程、逐记录文件打开/关闭及每根运行摘要重写。已做最小优化：复用 Worker 单线程执行器、交易 JSONL 持久写入、run 摘要每1000根写入、进度降频、Worker 日志不再每行同步 flush；没有缩小样本、跳过 Python 或更换框架。最佳74.4秒仍超过30秒，P04 标记未通过，性能根因和数据证据完整保留。

### P06

使用 `strategies/factory_hang.py` 的受控 Worker 回放：

- `artifacts/supervisor-g3-repair-20261002/p06-replay-timing.txt`：约5640ms，`protocol_timeout` 后退出，5秒目标未达。
- 当前无残留本次 Worker PID；其他 Python 进程属于外部 `D:\qqokx` 工具，不在本项目任务范围内，未擅自终止。

## 2. 最终构建与静态检查

```powershell
& 'tools\dotnet\dotnet.exe' build src\Qh.Cli\Qh.Cli.csproj --configuration Release --nologo
& 'tools\dotnet\dotnet.exe' build src\QhApp\QhApp.csproj --configuration Release --nologo
```

两项均为 0 warning / 0 error；Worker 和新增 Python 策略 `py_compile` 通过。G2 `09:04～09:05` 回归仍为 completed、1 条正式信号：`sig-20261002090920715-79cd05a6e`。

## 3. 结论

首轮返修阶段的结论为：G3 首轮审核指出的五组功能问题已完成返修并有独立证据；当时 P04 30秒与 P06 5秒数字目标仍未通过，原生人工 UI 走查仍未执行。随后按二次复核继续执行第4节最小返修，当前结论见第5节。

## 4. 二次复核新增问题的最小返修

负责人二次复核文档：`docs/progress/supervisor-g3-repair-review-20261002.md`。

### 4.1 模拟上下文、保证金和止损冲突

- `simulation.account` 改为在当前完成 K 线收盘价上做纯估值，再交给策略；不写权益账本、不重复扣费，且携带当前 `valuation_bar_end`/`last_processed_bar_end`。复现 run `trd-20261002092902727-369477725` 中，开仓后返回 equity=10018、floating=20、fees=2；平仓后返回 equity=10035、realized_gross=40、fees=5、floating=0，满足同一状态恒等关系。
- 完成 K 线收盘估值低于持仓初始保证金时，记录一次 `margin_exceeded`，不模拟强平或动态结算。`trd-20261002092903304-5aeea8e8c` 最终权益 -882，风险事件已留存，保证金口径为开仓价×乘数×数量×保证金率。
- 止损/止盈判断改为只检查当前 bar 的有效平仓/反向意图；同方向重复开仓不能取消风控平仓。`trd-20261002092903851-c71757e1a` 产生 threshold close，95 开盘平仓，毛利 -50、费用5、净利 -55、权益9945。

### 4.2 Windows 任务和结果入口

- 信号与交易入口共用 `SemaphoreSlim` 互斥闸门，并在 `SetReplayControlsLocked` 中同时锁住两个启动按钮；加载、启动、运行、结果读取期间不能依赖按钮状态绕过互斥。
- 交易只读重开恢复原 `StartInclusive`、`EndExclusive`、`Explore`、参数、配置和绑定快照；复制配置只清理结果视图，不改写原 run 的输入。
- 交易 run 的信号绑定到当前交易 run 后可从 `SignalGrid` 导出；明细入口补充 `run.json`、`quality.json`、`assumptions.json` 和策略副本，假设明确拒绝主连/指数交易回放、允许显式标记的合成验收序列，不猜测未知真实合约属性。

### 4.3 P04 同百万根样本二次测量

样本仍为 `artifacts/supervisor-g3-repair-20261002/perf-1000000-daily.csv`，1,000,000 根完整合法日线；未计首次导入，固定 Python 零动作策略，run `trd-20261002094104504-2c7859659` completed，InputRows/ProcessedRows=1,000,000，exit=0。

| 指标 | 实测 |
|---|---:|
| 外层端到端 | 69,631 ms |
| C# 回放 Worker elapsed | 66,835 ms |
| 协议/序列化往返累计（包含其中的策略调用） | 63,274.35 ms |
| Python 策略执行累计 | 22,413.97 ms |
| 撮合/账本累计 | 2,755.39 ms |
| 父进程峰值工作集 | 564,121,600 bytes |
| 直接 Python 子进程峰值工作集 | 228,188,160 bytes |

阶段累计存在包含关系，不做简单相加。证据文件为 `artifacts/supervisor-g3-repair-20261002/p04-measured-timing.txt`、`p04-measured-output.txt` 及该 run 的 `replay-metrics.json`。另以 `strategies/high_frequency_trade.py` 在 1,000 根样本验证 1,000 个意图、1 个实际成交、`max_pending=1`，确认当前 bar 局部标记替代历史 `intents.Any` 扫描后没有功能回归。P04 仍未达到30秒，继续标记未通过。

### 4.4 P06 真实 signal/trade 进程树证据

所有测量使用 `tools/measure-process-tree.ps1`，记录父 PID、直接子 PID、工作集峰值、CLI exit code；取消 run 的 Worker 日志均有 `cancelled` 后 `exit=0`，不涉及外部 `D:\qqokx` Python 进程。

| 场景 | signal | trade |
|---|---:|---:|
| 模块加载/初始化卡住完整返回 | 4,195 ms，exit=5 | 3,979 ms，exit=1 |
| on_bar 超时完整失败 | 2,769 ms，exit=1 | 2,917 ms，exit=1 |
| 正常取消、状态落盘、Worker 退出 | 3,598 ms，cancelled | 3,432 ms，cancelled |

详细文件在 `artifacts/supervisor-g3-repair-20261002/p06-round3/`；初始化场景为 `init_hang.py`/`trade_init_hang.py`，超时场景为 `timeout.py`/`trade_timeout.py`，取消场景使用百万根快照和标准 signal/high-frequency trade 策略。旧的 5,640ms signal-only 记录保留为历史证据，不再代替 trade P06。

## 5. 二次返修结论

二次复核指出的模拟状态、保证金风险、止损冲突、Windows 互斥/复制/导出/详情和 P06 trade 测量遗漏已按最小范围修复并有独立证据。P04 仍为 69.631 秒，未达到30秒；原生人工 UI 点击仍未执行。继续停在 G3，等待负责人再次审核，不进入 G4/G5/G6、打包或实时链路。
