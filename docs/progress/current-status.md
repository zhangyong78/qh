# 当前开发状态

更新时间：2026-10-04，Asia/Shanghai  
执行阶段：G5-B 完成 K 线策略求值（合成验证）；G7/G8/G9 与 G5-A 已完成，等待负责人复核 G5-B 包和界面  
状态：白糖 SR2701 15 分钟真实历史数据研究候选已生成并通过独立复算；实时提醒现在具备合成完成线求值闭环，但真实 TqSdk 持续行情/断线现场/正式 G5 仍未通过

- 启动修复：首轮负责人复现桌面快捷方式启动崩溃，原因是 `StrategyPresetCombo_SelectionChanged` 在 `InitializeComponent` 期间过早访问尚未创建的策略卡片控件，证据见 `artifacts/supervisor-shortcut-diagnosis-20261004/launch.stderr.txt`。已在 `src/QhApp/MainWindow.xaml.cs` 增加初始化状态/控件空值防护，不改策略、白糖数据或回测逻辑。
- 启动验证：按正式桌面快捷方式同一目标和参数启动当前发布包，子进程 PID 3300 运行满 12 秒、`Responding=True`、stderr 0 字节；证据见 `artifacts/supervisor-shortcut-diagnosis-20261004-final`。随后继续完成包同步和 manifest 重算，等待负责人复核。
- G5-B 合成验证：新增每实例策略配置和求值账本（策略路径/版本/哈希、参数摘要、预热需求/进度、有限历史、状态/错误），独立 Worker 在完成 bar 后求值；源码 fixture `g5b-fixture-check` 覆盖预热静默、正式信号入账、重复/重启去重、恢复期抑制通知和策略异常隔离，退出码 0、`all_passed=true`。真实 TqSdk 现场尚未宣称通过。
- G5-B 最终包复核：包内 `g5b-fixture-check`、`g5a-fixture-check`、`g4-fixture-check` 均为 `exit=0 / all_passed=true`；正式快捷方式同目标/参数启动子进程 PID 16852，观察 12 秒仍 `Responding=True`，stderr 0 字节。发布包 manifest 10,254 项、缺失/哈希失败 0。
- 发布包完整性返修：负责人发现 Python 正常运行会在包内生成 `__pycache__/*.pyc`，导致 manifest 漂移。已为策略描述、signal/trade replay、G5-B 实时策略、TqSdk bridge 和普通 Worker 统一设置 `PYTHONDONTWRITEBYTECODE=1`；从包内清理 400 个 `__pycache__` 目录后重发。最终包 6,729 项；G5-B/G5-A/G4 每次运行后均 `expected=6729 / actual=6729 / missing=0 / extra=0 / hash_failures=0 / pycache=0 / pyc=0`。快捷方式 PID 33316 观察 12 秒仍响应，stderr 0 字节；证据见 `artifacts/supervisor-shortcut-diagnosis-20261004-bytecode`。
- 历史下载日历门禁：数据页现在按环境变量、包目录、当前数据根依次发现本机日历，并在下载按钮附近明确显示“未就绪时不会发起请求”。新增显式操作可从当前已验证的 `CZCE.SR701` 15 分钟快照生成研究日历及审计证据；本地无账号验证得到 171 个交易日、679 个实际时段，其他合约和越界日期均拒绝。该日历来源于本机快照，不是供应商官方日历。

## 本次收口：白糖 SR2701 真实数据离线研究候选

- 数据源：真实 TqSdk 3.8.7 历史数据，`CZCE.SR701`（显示为 `SR2701`），15 分钟，2026-01-16 21:00 至 2026-09-30 15:00（北京时间）。共 3,893 根、171 个交易日，其中 166 天为 23 根、5 个节后首日为 15 根；时段质量问题 0 个。
- 时段对齐：夜盘 21:00–23:00；日盘 09:00–10:15、10:30–11:30、13:30–15:00；夜盘按下一交易日归属。已修复区间末 14:45 K 线因无后继而被误删的问题。
- 回测结果：SMA20/SMA60 双均线、双向、下一根有效K线开盘成交；62 次信号、123 笔成交、61 笔平仓，净收益 7,478 元，期末权益 107,478 元，最大回撤 3,366 元，期末空头浮盈 200 元。Decimal 独立审计通过。
- 交付入口：桌面“期货研究与回测工作台”；脚本为 `tools/open-sr2701-backtest.ps1`；报告为 `artifacts/sr2701-15m/文华财经时段对齐报告.md`；使用说明为 `docs/使用说明.md`。旧诊断程序文件保留，但桌面不再提供入口，需要时由开发/诊断流程启动。
- 明确边界：真实账号持续权限、实时行情、人工 UI 完整验收、P04 百万性能目标、正式 G5 提醒/实时链路均未通过或未验证；本候选只用于离线历史研究和 G5-A 基础验证，不代表实盘可用，不接 CTP/真实下单，不索要或记录密码。

## 当前任务

- 已完成 G0 环境事实、指标/完成状态语义和最小手算夹具记录。
- 已准备项目隔离的 .NET 10.0.401 SDK，未修改系统级 .NET 或 Python。
- 已实现并返修离线 CSV 导入、质量报告、内容寻址数据快照、SQLite 清单、Parquet/DuckDB 查询、WPF K 线页面和受管理 Worker 的心跳/取消/退出协议。
- 已修复 SMA 未来数据、混合序列、断目录假成功、缺口/休市识别、无偏移时区、UI 线程计算和 Worker 关闭风险。
- 已补充 CSV/Parquet/质量 JSON 实际摘要和结构校验、跨时段有效缺口枚举、质量报告持久化重载、查询取消和退出确认/超时诊断。
- 已修复重复行/逆序任务诊断导致合法快照被错误重建的问题；本次 ImportResult 与既有快照质量/来源保持分离。
- 已落地 G2 Python 策略契约、参数/版本/文件摘要、逐根 Worker 回放、固定指标口径、正式区间和 bar_end 语义。
- 已落地独立 run 目录、策略副本、质量/参数/软件/指标版本/日志、只读读取/筛选/CSV 导出；严格缺口阻断，探索模式显式标记不完整。
- 已实现取消、运行时错误、超时、未完成 K 线、重复 bar_end 和 Worker 退出处理；不接 TqSdk，不做真实通知或真实下单。
- 已修复首轮审核发现的 Worker Context 实参错误；stderr 并发排空、协议/加载/工厂期限、取消/异常 finally 收尾均已补齐。
- 已补齐 WPF 策略描述/参数/区间/探索/配置检查、历史 run 只读重开、表格筛选/导出、Canvas 标记和点击定位；标准回放当前源码复现为 2 条信号。
- 已补齐正式区间无覆盖和参数相关预热不足的 `blocked_coverage` 门禁；预热信号独立计数且不进入正式结果。
- 已修复通用参数 JSON 对无参数策略的误注入、策略切换旧 descriptor、只读 run 快照绑定、策略加载并发/关闭收尾，以及 Python traceback/stderr 最终尾部记录。
- 已修复回放前重新校验策略时重置用户区间，以及重复点击加载误清理首个任务令牌的问题；09:04～09:05 区间回归保存一致且仅产生 09:05 上穿信号。
- 已按负责人批准值同步 `docs/03-Luna实施任务与验收关卡.md` 第 6.5 节；未修改 `docs/04-负责人监督记录.md`。
- 已实现 G3 `kind=trade` 策略协议：完成 K 线产生独立 signal/intent，下一根有效 K 线开盘仅尝试一次；支持开多/平多/开空/平空、反向先平后开、固定/比例手续费、滑点、保证金、资金不足、完成线止损止盈、浮盈亏、权益曲线、最大回撤、未成交和风险事件。
- 已新增独立 `trade-runs` 账本、策略副本、配置/假设/质量记录、订单/成交/持仓/意图/权益/未成交 JSONL 与成交 CSV 导出；G3 WPF 入口支持交易配置 JSON、取消、只读重开和成交表。
- 已用批准夹具核对：F01 净15/权益10015；空头净15；反向毛40/费10/净30/4成交/2已平；资金不足 0 成交且不重试；末根意图 `no_next_valid_bar`；零交易胜率为不适用；比例费用净97.9；完成线止损净-55/权益9945。
- 已完成 G3 首轮返修：固定费用按实际成交手数计算；平仓明确为全仓数量语义并校验；同一完成 K 线的多个意图在同一下一根开盘依序尝试；复用 G2 warmup/正式覆盖门禁；策略收到只读模拟持仓/账户上下文；新增 SMA/EMA 交叉交易示例。
- 已修复取消/失败摘要，保留已成交/手续费/浮盈权益和未尝试意图；无效负价拒绝并记录风险/未成交；保证金超限单独记录风险事件；交易 Worker 使用可确认的取消/退出收尾。
- 已补齐 WPF 交易权益曲线 Canvas、信号/成交不同标记、成交点击定位、交易快照绑定只读重开、订单/持仓/未成交/风险/Worker 明细读取、配置复制新 run、成交/权益/摘要导出；大结果使用权益采样，不拼接百万数字文本。
- P04 已完成同一百万根合法合成日线、固定 Python 零动作策略的端到端测量：未优化约1066345ms；此前最小优化最佳74422ms、exit=0、峰值工作集约544MB，仍未达30秒，根因和优化记录见返修报告。旧P06的5640ms仅为signal fixture历史记录，已由下方signal/trade分路径测量替代。
- 已完成 G3 二次复核最小返修：模拟上下文改为当前完成K线收盘价纯估值；补持仓保证金超限风险事件；同向新增开仓不再取消止损/止盈；信号/交易回放共用异步互斥；交易只读重开恢复原区间/探索模式并支持交易信号导出；补充配置/质量/假设/策略详情入口和具体合约限制。
- P04 同一百万根样本最终复测：端到端69631ms，C# Worker 66835ms，协议/序列化往返累计63274.35ms（含其中策略调用），策略执行22413.97ms，撮合/账本2755.39ms；父进程峰值564121600字节，直接 Python 子进程峰值228188160字节，1,000,000根/exit=0，仍未达30秒。
- P06 已改用真实 trade Worker 并分离 signal/trade：初始化卡住 signal 4195ms、trade 3979ms；on_bar 超时 signal 2769ms、trade 2917ms；正常取消 signal 3598ms、trade 3432ms，均有父子 PID、最终状态和 Worker exit=0证据。旧5640ms signal-only记录不再替代trade证据。
- 已进入 G6 适用离线阶段：统一解析包内 Python，补齐导入/快照/策略/配置/旧run/复制入口共同上下文闸门，补 KQ.m@/KQ.i@ 与 888/999 主连识别；新增 WPF `--self-check` 离屏 PNG/JSON 自检。
- 已生成清理后的 self-contained Windows x64 候选包：`artifacts/g6-offline-package-20261002-r39.zip`，SHA256 为 `7A81F068B0D56148B2B486C033990736D0448249315ED6358300FB82D1DC57AD`，大小 150,113,546 bytes；清单排除自身，ZIP 直读核对 840 项无不匹配，包含 .NET、Python 3.12.10、Worker、策略、夹具和授权。
- G6 r29 已核验的业务与严格运行中取消结论保留。r39 清理后真实 WPF 诊断中，10,000 根/Dispatcher/业务/取消路径可到达，但实际根/Canvas/结果捕获为全透明黑图，`result_rendered=false`、总自检不通过；r38 模型重绘 PNG 不再作为 A05 实际可视证据。该证据不替代人工点击。
- G6 r22 最终包复核已在 `artifacts/负责人 r22 最终包复核-20261002` 完成：重复导入复用 snapshot 且旧 run 哈希不变；F01/short/reverse 固定矩阵通过；`bad_syntax.py` 加载失败返回 exit=5 并保留 SyntaxError 诊断。r39 仅做最小渲染清理和清单修正。
- G6 百万根最终运行时复测：`artifacts/负责人 r20 独立复核-20261002`，端到端128443.1954ms，Worker125796ms，协议/序列化115655.58ms，同步 `on_bar` 执行706.38ms，1,000,000根/exit=0；P04仍未达30秒。父进程现按每根发送时建立整根绝对 deadline，超时诊断带当前 bar/策略/阶段并终止隔离 Worker，未改变策略/撮合/协议语义。
- 已补充 r19 10,000 根协议预算实验：默认649ms、协议往返518.16ms；诊断模式693ms，序列化63.04ms、写/flush81.15ms、读等待303.13ms、解析23.64ms、日志10.75ms。诊断开关本身有约44ms开销；同步阻塞管道无法在不增加不可取消阻塞的情况下保持 deadline，未合并架构候选。
- 已完成 G7/G8/G9 UX 候选：通用策略卡片与参数表单、统一“开始回测”路由、回测概览首屏、常用下一步按钮、记录页高级设置和通用桌面入口；Release 构建与包清单通过，等待用户人工确认新版概览布局、统一回测和四个概览按钮。
- G5-A 首轮包复核发现 manifest 虽自洽，但包内 Qh.Core/Qh.Cli 为旧二进制，包内 `g5a-fixture-check` 返回 usage/exit=2；本轮已全量同步当前 Release 输出，补齐停止/失败状态拒绝、活跃状态名额判断、同向冷却/反向放行和软件内信号查询列表。源码与包内 G5-A/G4 fixture 均退出码 0，源码/包内核心二进制哈希已核对一致，manifest 已重新生成。
- G5-B 已解除“空候选”限制：`RunTqLiveAsync` 现在对协调器接受的完成 bar 调用独立策略 Worker，并将候选交给 `LiveReminderEngine`；预热不写信号，恢复候选只写账本，策略故障只影响当前实例。账本最多保留 5 条活跃实例记录，但 UI 的实时桥接入口一次只运行一个会话，不宣称并发 5 会话或实盘提醒。
- P04 2026-10-03 有界性能实验仍按“未达标”记录：开发测量曾观察到 buf64 的 10k Worker 1177ms→804ms、协议往返 945.9758ms→650.205ms、会计/日志132.2507ms→66.7888ms，但负责人同环境 A/B/A/B 仅复核出约7.3%稳定改善，不能把跨运行差异全归因缓冲。开发报告另记有 1,000,000根/completed/exit=0、端到端42460.8ms、Worker40278ms、协议37390.3293ms、会计/日志2138.3587ms、策略263.4642ms，仍超过30秒；该 buf64 原始外层/资源文件缺失。负责人实读的 r20 原始百万记录为端到端128443.1954ms、exit=0，同样未达30秒。候选版取消 `trd-20261003013343524-b4abe9917` 为cancelled/4575根/Worker exit=0；整根 deadline `trd-20261003013346231-b637d4a6f` 为failed/strategy_timeout/0根/2.25s。已重建并严格核对包 `artifacts/g6-offline-package-20261003-p04-buf64.zip`，SHA256 `CB7F47611867DB60E0B432A0921A52DD2BFD185C053DADFCC084F2E3A89851C2`，manifest 10013项与ZIP（排除manifest）一致。详见 `docs/progress/p04-performance-experiment-20261003.md`。
- P04 下一候选“复用整根 `barDeadline.Token`、去掉 `ReadResponseAsync` 每条回复的 linked CTS”已按审核要求完成 guarded A/B/A/B、12项固定数值、取消 partial、初始化卡住和受控多回复 deadline 回归；旧/新 Worker 为644/641ms与646/659ms，未达到约25%改善，因此已回退，不重跑百万、不改最终包。最终包仍为 buf64；守护工具已修正为流式落盘、2秒有界收尾、1GB内存/1MB输出/20秒限制，并改用原生 `Get-Process.StartTime` UTC ticks 做同源身份核验，未知身份显式保留为 `unknown`，补充 Core/Worker 摘要和监控输出入口。两次低内存辅助验证见 `artifacts/p04-guard-identity-check-20261003`；P04 证据在 `artifacts/p04-candidate2-20261003` 与 `artifacts/p04-deadline-final-20261003`。详见 `docs/progress/p04-deadline-token-experiment-20261003.md`。
- P04 唯一 AutoFlush 候选已完成实际 .NET 10.0.12 CLI runtime 探针、哈希冻结、1k A/B 和 10k A/B/A/B：候选仅在 Worker 启动后关闭 `StandardInput.AutoFlush`，保留逐条显式 `FlushAsync(token)`；10k Worker 均值 `986.5→967.0ms`、约1.98%，外层墙钟约慢0.26%，未达约25%门槛，已回退。证据在 `artifacts/p04-autoflush-candidate-20261003`；最终 buf64 包与 SHA 不变，不做有限回归或百万，不进入 G5。
- P04 输出保护首包已被监督复核拒绝：固定40,000根零动作在24,436根因正常 `bar_done` 诊断计入4MiB worker.log预算而误报 `output_limit`，0 intent/0 fill。已做最小返修：worker.log达到4MiB后仅停止诊断追加、不终止正常回放、不无限提高上限；stderr、describe、单条协议超长行仍为致命保护，bar阶段错误保留bar起止时间/strategy/phase，日志不再逐条强制flush。r2 二进制包已通过负责人实际复核；P04 说明副本仍为 `artifacts/g6-offline-package-20261003-p04-outputguard-r3.zip`。本次现场阻塞的 TqSdk 修复交付为 `artifacts/g6-offline-package-20261003-tqsdk-diagnostics-r2.zip`，SHA256 `E4F8AA32910EFD28D9FE5B40829C9FE8564A33CB1BE220A150DA16BA81F893A3`，269,921,789 bytes；manifest/目录/ZIP严格核对为10015项、哈希错误0，负责人复核确认 bridge `op=probe`（无账号）返回 Authentication、`op=runtime` 可导入 TqSdk 3.8.7/DataDownloader，`g4-gateway-check` 与 `g4-fixture-check` 均 exit=0/all_passed。连接失败不再笼统回退为 Network：bridge 分类为认证、网络/DNS、超时、SDK缺失/初始化、权限或未知，WPF 仅显示脱敏类型和下一步，不显示原始异常/凭据。真实账号、权限、行情覆盖仍未验证；P04性能结论仍失败，不进入G5。
- 已完成有限 P08：`artifacts/G6 P08 r22-final-20261002` 热身15.035秒后三轮100k研究循环成功，另有一轮timeout partial；40个样本、14个Python样本，应用工作集103.828–105.824MB、Python峰值36.758MB，正常关闭且剩余自有PID为0。已修正 P08 脚本为累计记录本次自有PID身份并逐个检查退出；不因该脚本修正重跑三轮100k。已补 P01 五次启动阶段标记和 A01～A12/A16/A18、P01～P08逐项矩阵。

## 修改文件（完成后持续更新）

- `docs/progress/g0-report.md`
- `docs/progress/g1-report.md`
- `docs/progress/current-status.md`
- `docs/progress/g2-contract.md`
- `docs/progress/g2-report-20261002.md`
- `docs/progress/g2-repair-report-20261002.md`
- `docs/progress/g2-repair2-report-20261002.md`
- `docs/03-Luna实施任务与验收关卡.md`（仅新增 G3 手算夹具说明）
- `worker/qh_signal_worker.py`
- `strategies/`
- `fixtures/`
- `src/`
- `tools/`（项目隔离 .NET SDK）
- `strategies/trade_sequence.py`
- `strategies/sma_ema_cross_trade.py`
- `strategies/same_bar_open_close.py`
- `strategies/warmup10_trade.py`
- `strategies/perf_zero_trade.py`
- `fixtures/g3-bars-*.csv`
- `src/Qh.Core/TradeModels.cs`
- `src/Qh.Core/TradeRunStore.cs`
- `src/Qh.Core/TradeReplayRunner.cs`
- `docs/progress/g3-report-20261002.md`
- `docs/progress/g3-repair-report-20261002.md`
- `docs/progress/p04-deadline-token-experiment-20261003.md`
- `tools/measure-process-tree.ps1`
- `tools/measure-p04-guarded.ps1`
- `strategies/trade_init_hang.py`
- `strategies/trade_timeout.py`
- `strategies/high_frequency_trade.py`
- `fixtures/g6-kq-main.csv`
- `src/Qh.Core/PythonRuntime.cs`
- `tools/package-offline.ps1`
- `docs/progress/g6-offline-report-20261002.md`

## 已执行环境命令与结果

| 命令/检查 | 结果 |
|---|---|
| `$PSVersionTable` | PowerShell 7.6.5 |
| `Get-CimInstance Win32_OperatingSystem` | Windows 11 教育版，10.0.26200，x64 |
| `Get-CimInstance Win32_Processor` | Intel Core Ultra X7 358H，16 核/16 线程 |
| `Get-CimInstance Win32_ComputerSystem` | 32GB 物理内存 |
| `Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='D:'"` | D: 约 675GB 可用 |
| `Get-ItemProperty HKCU:\Control Panel\Desktop LogPixels` | 120，约 125% 缩放 |
| `python --version` | Python 3.12.10 |
| `dotnet --info`（系统） | 仅 .NET 8.0.7 runtime，无 SDK |
| 项目隔离 SDK | `tools/dotnet`，.NET SDK 10.0.401，安装成功 |

## 验收证据状态

| 项目 | 状态 | 证据 |
|---|---|---|
| G0-ENV-01～04 | 已完成，待负责人复核 | `docs/progress/g0-report.md` |
| G0-SEM-01～06 | 已完成，待负责人复核 | `docs/progress/g0-report.md` |
| G0-FIX-01～05 | 已完成，待负责人复核 | `fixtures/g0-indicator-expected.md`、`docs/progress/g0-report.md` |
| G1-DATA-01～03、05 | 已完成实施验证 | `docs/progress/g1-report.md` 与 `artifacts/g1-final-*` |
| G1-DATA-04 | 返修完成，UI 人工未验证 | `docs/progress/g1-repair-report-20261002.md`；当前 Computer Use 原生窗口接口禁用 |
| G1 返修 | 已完成，待负责人审核 | `docs/progress/g1-repair-report-20261002.md` |
| G1 第二轮返修 | 已完成，待负责人审核 | `docs/progress/g1-second-repair-report-20261002.md` |
| G1 第三轮返修 | 已完成，待负责人审核 | `docs/progress/g1-third-repair-report-20261002.md` |
| G2 首轮审核 | 未通过，已按4项返修 | `docs/progress/supervisor-g2-review-20261002.md` |
| G2 返修 | 已完成实施验证，待负责人再次审核 | `docs/progress/g2-repair-report-20261002.md` |
| G2 第二轮返修 | 已完成实施验证，待负责人再次审核 | `docs/progress/g2-repair2-report-20261002.md` |
| G2 第三轮返修 | 已完成实施验证，待负责人再次审核 | `docs/progress/g2-repair3-report-20261002.md` |
| G3 首轮审核 | 未通过，已完成五组返修并复测 | `docs/progress/supervisor-g3-review-20261002.md` |
| G3 首轮返修 | 已完成实施验证，待负责人再次审核 | `docs/progress/g3-repair-report-20261002.md`；`artifacts/supervisor-g3-repair-20261002` |
| G3 二次最小返修 | 已完成实施验证，待负责人再次审核 | `docs/progress/g3-repair-report-20261002.md` 第4～5节；`artifacts/supervisor-g3-repair-20261002/p06-round3` |
| G6 适用离线包 | 已生成清理候选并完成包清单直读核验；真实 WPF 像素诊断未通过，待负责人验收 | `docs/progress/g6-offline-report-20261002.md`；`artifacts/g6-offline-package-20261002-r39.zip`；`artifacts/g6-wpf-render-diagnostic/result` |
| G4 独立实现/夹具 | 已完成项目隔离 TqSdk 3.8.7 部署、有限依赖准备探针、带有效期的结构化本机日历接线、standard/professional 历史前缀/交易日/完成状态和覆盖门禁、bridge→gateway→publisher 集成、完整恢复消费/WPF 事件入账、有效末边界/跨午夜日历、SDK 内部 md 断线通知、显式实时连续性、已知末根消费和秒级实时验证窗口；真实账号/权限/现场覆盖未验证；新增连接失败脱敏分类并已用合成异常验证 | `docs/progress/g4-independent-report-20261003.md`；`artifacts/g6-offline-package-20261003-tqsdk-diagnostics-r2.zip`；`artifacts/g4-final9-r3-evidence-20261003/package-fixture-report.json` |
| 真实 TqSdk 账号/权限 | 未验证 | G1 不依赖账号 |
| 人工 UI 操作、长时间运行 | 未验证 | r39 包已完成；真实 WPF 像素诊断失败，不以模型重绘代替人工和长时间验收 |

## 下一步

当前只等待用户打开桌面“期货研究与回测工作台”确认实际可视结果、统一回测流程、四个概览按钮和 G5-A 提醒界面；如出现现场故障，仅做最小修复。TqSdk 真实持续权限、真实行情覆盖、Windows 弹窗可见性、断线现场恢复、两个完整交易日、P04 性能目标、正式 G5、CTP、真实下单和实盘提醒均未通过或未做。保持不进入正式 G5，不接入下单或远程通知链路，不索要密码。
