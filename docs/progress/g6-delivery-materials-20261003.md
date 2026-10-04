# G6 独立离线交付材料（2026-10-03）

## 0. 本次现场研究候选收口（2026-10-04）

本节是当前用户任务的最新状态；下方通用 G6 诊断包材料继续作为历史技术基线保留。

- 研究对象：真实 TqSdk 3.8.7 历史数据，`CZCE.SR701` / `SR2701`，15 分钟。
- 数据结果：3,893 根K线、171 个交易日，其中 166 天各 23 根、5 个节后首日各 15 根；白糖夜盘 21:00–23:00，日盘 09:00–10:15、10:30–11:30、13:30–15:00，时段质量问题 0 个。
- 已修复问题：区间末 2026-09-30 14:45–15:00 K线没有后继K线时，旧逻辑错误地把它排除；修复后纳入回测。
- 回测结果：SMA20/SMA60 双向策略，62 次信号、123 笔成交、61 笔平仓；净收益 7,478 元，期末权益 107,478 元，最大回撤 3,366 元，期末空头浮盈 200 元；Decimal 独立复算通过。
- 当前入口：桌面“白糖SR2701回测工作台”，或运行 `tools/open-sr2701-backtest.ps1`。详细报告：[文华财经时段对齐报告](../../artifacts/sr2701-15m/文华财经时段对齐报告.md)。
- 状态边界：这是“真实数据离线研究候选”，不是实时交易成品。真实账号持续权限、实时行情、人工 UI 完整验收、P04 百万性能目标、G5 提醒/实时链路仍未通过或未验证；保持不进入 G5，不接入下单链路，不索要密码。

## 1. 当前唯一交付入口

本材料对应的 Windows x64 离线包为：

`artifacts/g6-offline-package-20261003-tqsdk-diagnostics-r2.zip`

这是在已通过负责人复核的 P04 输出保护基础上，为真实现场 TqSdk 连接失败新增脱敏诊断分类后的运行包。旧的 P04 输出保护副本仍保留：`artifacts/g6-offline-package-20261003-p04-outputguard-r3.zip`。

| 项目 | TqSdk diagnostics r2 交付值 |
|---|---|
| ZIP | `artifacts/g6-offline-package-20261003-tqsdk-diagnostics-r2.zip` |
| SHA256 | `E4F8AA32910EFD28D9FE5B40829C9FE8564A33CB1BE220A150DA16BA81F893A3` |
| ZIP 大小 | 269,921,789 bytes |
| manifest 文件数 | 10,015 |
| 严格核对 | manifest/目录/ZIP 缺失、未声明、大小或哈希错误均为 0 |
| 二进制 | `Qh.Core.dll` `AA8BC3A90712B2EEFC1AED8F8AA2C0DF7F0D00F5E709B2093B9BBE9AEBA999F7`；`Qh.Cli.dll` `986EED5ED58C15C8AB5E732D1A96C4288F4A160C6A0D1AF868E7809A6AE2038E` |

r2 负责人实际复核的有限结果继续作为输出保护基线：固定 40,000 根零动作 `completed/40000/40000`、0 intent/0 fill，`equity.jsonl` 40,000 行；worker.log 诊断保留约 4MiB、实际 64 个分段文件。负责人已独立复核本次连接诊断：bridge `op=probe`（无账号）返回 Authentication，`op=runtime` 可导入 TqSdk 3.8.7/DataDownloader，`g4-gateway-check` 与 `g4-fixture-check` 均 exit=0/all_passed。此次新包仅增加 TqSdk 连接错误分类与 WPF 安全提示，未使用真实凭据；输出保护细节见 `docs/progress/p04-output-protection-20261003.md` 和 `docs/progress/supervisor-outputguard-r2-review-20261003.md`。真实账号、权限、行情覆盖仍未验证。

配套源码包：`artifacts/g6-source-archive-20261003-r5.zip`，SHA256 `E4610D405A737A64CD4A73F3BE6B1C4CC1D2909068C2C166E15F6BCBF5518E7F`，417,136 bytes，清单 150 项。源码包保留原始目录/文件名并使用 UTF-8 ZIP 条目；只含显式选定的 `src/`、`worker/`、`strategies/`、`fixtures/`、项目文件、requirements、工具脚本和 docs 交付/验收文档；主交付文档因记录本源码包外部 SHA 而保留在工作区，并在归档内提供外部位置说明。严格核对无 `bin/`、`obj/`、`__pycache__/`、`artifacts/`、`tools/dotnet`、下载 runtime、用户数据或凭据；Markdown 63 个文件的 8 个相对链接和 43 个 `docs/progress/*.md` 引用均无缺失。源码与当前工作区逐项比较 148 个文件，差异为 0。源码包自身清单不自引用，ZIP 内保留清单文件；独立外部核验摘要为 `artifacts/g6-source-archive-20261003-r5-external-manifest.json`。

## 2. Windows 双击入口与首次离线操作

1. 将 ZIP 解压到新目录，不直接从压缩包运行，也不要覆盖旧包；首次人工验收建议使用没有开发工具的 Windows 11 x64 机器。
2. 双击 `<解压目录>\app\QhApp.exe`。CLI 入口是 `<解压目录>\cli\Qh.Cli.exe`；包内自带 .NET 和 Python，不要求用户安装 IDE 或系统 Python。
3. 在应用中选择导入 CSV，使用包内 `fixtures\sample-bars.csv` 或用户自己的合成/历史 CSV。默认用户数据与结果目录为 `%LOCALAPPDATA%\QhResearch\data`；先观察导入行数、质量报告、快照 ID 和失败提示。
4. 信号验证：选择一个已导入快照和 `strategies\period_signal.py`，使用默认参数运行。预期是预热阶段不产生正式信号，正式阶段产生结构化 signal 列表；信号回放不应伪造 fills、equity 或交易收益。
5. 交易回测验证：选择 `fixtures\g3-bars-f01.csv` 和 `strategies\trade_sequence.py`，参数 `scenario=f01`。预期为两条意图、两笔成交、一笔已平交易，结果页能看到权益、订单、成交和运行摘要；固定手算值以 `docs/01-交付标准与系统方案.md` 为准。
6. 结果重开：从历史运行列表打开刚完成的 run，核对快照、策略副本、参数、起止区间、信号/订单/成交/权益和错误状态与原 run 一致；只读重开不能修改原 run。

## 3. 备份、升级和恢复

升级前关闭应用，复制整个 `%LOCALAPPDATA%\QhResearch\data` 或需要保留的 run 目录到独立备份位置。将新包解压到新的目录，保留旧包可回退，再让新包指向同一用户数据目录；不要覆盖旧包、策略文件或结果目录。升级后先打开旧 run，再运行一个小型 F01，确认历史记录未被改写。

## 4. Python 策略接口

策略文件必须提供：

- `STRATEGY = {name, version, kind, warmup_bars, parameters}`；`kind` 为 `signal` 或 `trade`。
- `create_strategy(parameters)`，返回具有 `on_bar(context)` 的对象。
- `context.bar`、`bar_end`、`is_formal`、`parameters`、`bar_index`、只读 `simulation`；指标方法为 `sma(period)`、`ema(period)`、`atr(period)`。
- signal 策略返回 `null`、一个 signal 或 signal 列表；signal 必须含 `category`、`direction`、`description`，可选 `reference_price`。
- trade 策略可返回 signal 和 intent；intent 的 `action` 为 `open/close/reverse`，`direction` 为 `long/short`，`quantity` 为正整数。

策略不得读取未来 K 线、访问网络/账号或调用真实下单链路。Python 是可信本地程序边界，不是安全沙箱。正式结果与 `worker.log` 诊断副本分离；诊断保留停止追加不等于正式 signal/intent/fill/equity 丢失。异常、非法返回、超时、取消和真实 stderr/协议超限会失败并保留适用 partial 状态。

## 5. CSV 数据规范

CSV 必须包含以下列：

`timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete`

`timestamp` 必须是带时区偏移的 ISO-8601；`period` 决定 `bar_end`；`trading_day` 使用供应商字段优先的 `yyyy-MM-dd` 交易所口径；只有 `is_complete=true` 的 K 线可进入回放。一次导入只允许一个 symbol/exchange/period。严格导入会检查排序、重复、缺失值、OHLC、时间连续性和快照摘要；示例 fixture 和费用参数均为合成数据，不代表真实行情或真实费率。

## 6. A/P 逐项验收表

状态含义：通过仅表示适用范围内有证据；“离线部分通过”不等于人工 UI、目标机器或真实权限通过；“未验证”不做推断。

| 编号 | 本次可观察结果与证据 | 状态 |
|---|---|---|
| A01 | 无账号可导入和离线研究；diagnostics r2 包入口已具备；人工首页需用户本机点击确认 | 离线部分通过，人工未验证 |
| A02 | 真实权限不足/空下载链路未接入 | 未验证 |
| A03 | 重复导入复用 snapshot，旧 run 哈希保持 | 离线通过 |
| A04 | CSV 时区、period、交易日和质量门禁已验证；真实夜盘/休市日历未接入 | 离线部分通过，真实日历未验证 |
| A05 | WPF 实际可视、信号点击跳转、缩放和人工交互未通过当前开发环境证据；旧透明黑图不算 UI 通过 | 未通过，待人工/现场 |
| A06 | `bad_syntax.py` 等加载失败保留 exit=5/SyntaxError，超时错误含 bar/strategy/phase | 离线通过，人工 UI 未验证 |
| A07 | warmup、正式区间、完成 K 线和无未来输入有证据 | 离线通过 |
| A08 | 信号研究不产生交易收益伪造，零交易胜率为 N/A | 离线通过 |
| A09 | F01/short/reverse 与费用、撮合、权益固定矩阵有证据 | 离线通过 |
| A10 | 零交易、末根持仓和 `no_next_valid_bar` 有证据 | 离线通过 |
| A11 | 只读重开/复制配置保持原 run 不变 | 离线通过 |
| A12 | 取消、Worker 隔离、partial 账本和再次运行有证据；人工关闭分支未点验 | 离线部分通过，人工未验证 |
| A13 | 实时通知去重不在本次离线交付范围 | 未验证 |
| A14 | 真实断线恢复不在本次离线交付范围 | 未验证 |
| A15 | 实时静音/暂停/停止不在本次离线交付范围 | 未验证 |
| A16 | 内部取消/退出/PID 收尾有证据；人工窗口关闭需本机确认 | 离线部分通过，人工未验证 |
| A17 | 合约到期暂停不在本次离线交付范围 | 未验证 |
| A18 | diagnostics r2 self-contained 包、10015 项清单/ZIP 严格核对通过；新机器人工运行未执行 | 包闭环通过，新机器未验证 |
| P01 | 有开发机启动阶段记录；人工首页和冷启动需用户本机测量 | 适用测量通过，人工未验证 |
| P02 | 有限 10,000 根适用测量记录 | 适用测量通过，不作冷启动声明 |
| P03 | 内部 Dispatcher/回放路径有证据；人工缩放、点击、取消未验证 | 适用内部通过，人工未验证 |
| P04 | 固定百万根已测失败：r20 原始记录 `128443.1954ms/completed/exit0`，超过 30 秒；buf64 `42460.8ms`/Worker `40278ms`仅为开发报告值，原始外层/资源文件缺失 | 未通过，目标口径不降低 |
| P05 | 实时/通知性能未做 | 未验证 |
| P06 | timeout/cancel、整根 deadline、Worker 收尾和 PID 收尾的离线部分有证据 | 离线部分通过 |
| P07 | 新机器两完整交易日长时稳定性未做 | 未验证 |
| P08 | 有限窗口 100k 研究循环记录；不外推两完整交易日 | 有限窗口通过，不等于长时通过 |

## 7. 用户本机人工验收清单

在本机逐项记录“观察到的事实”，不要用 self-check PNG 或模型重绘替代人工：

1. 双击 `app\QhApp.exe`，观察首页实际可见、控件文字、导入入口和无账号提示。
2. 导入 `sample-bars.csv`，观察质量报告、快照和错误信息；重复导入一次，确认不翻倍。
3. 运行 `period_signal.py`，点击一条信号，确认图表跳到正确合约/周期/K 线；确认没有伪造成交收益。
4. 运行 F01 交易回测，查看订单、成交、权益、费用和结果重开；确认末根意图不被默默平仓。
5. 在运行中点击取消，关闭窗口，观察“正在取消”到终态、无残留受管理 Worker，并再次运行 F01。
6. 将同一数据备份后解压新目录升级，确认旧 run、策略副本和配置仍可重开。

人工验收仍需明确记录：A05 实际可视/人工点击、P01 人工首页、新机器启动；真实权限、P05/P07、两完整交易日、实时/通知/下单链路和 G5 均不由本材料宣称通过。

## 8. 交付边界

这是 G6 独立离线候选的交付材料，不是完整成品验收，也不是 G4 现场通过。P04 固定百万目标失败且 30 秒目标未降低；真实账号/权限、现场覆盖、两完整交易日、A05 人工可视和 G5 均保持未通过或未验证。不得在聊天中发送密码，不购买或催促账号，不把合成数据当作市场事实。
