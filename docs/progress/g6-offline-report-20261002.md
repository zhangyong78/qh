# G6 适用离线阶段报告

日期：2026-10-02，Asia/Shanghai  
范围：仅离线包、包内运行时、应用自检和适用性能；不进入 G4/G5，不接入 TqSdk/CTP/账号/通知/实盘。

## 1. 应用上下文保护与完整自检

- WPF 将导入、快照加载、策略选择/加载、配置检查、信号/交易回放、旧 run 打开和交易配置复制纳入同一个 `_contextGate`；取消入口仍可用，关闭时等待任务收尾。
- `QhApp.exe --self-check <path>` 使用真实 `MainWindow` 和现有 UI 请求/结果路径，检查关键控件、Canvas、包内 Worker、包内 Python 和闸门非重入，并保存离屏 PNG。该入口不替代人工鼠标点击。
- r38 的 PNG 仅是后续撤回的控件模型重绘证据，不作为实际 WPF 像素验收。清理后的 r39 真实 WPF 诊断保存在 `artifacts/g6-wpf-render-diagnostic/result/self-check.json`：控件绑定、业务和严格取消路径可到达，但 `non_transparent_pixels=0`、`non_white_pixels=0`、三个业务区域均不可见，因此 `result_rendered=false`、总 `passed=false`。现场人工验收仍开放。
- P02：沿用 r29 已核验的同一隔离数据根 10,000 根五次载入/绘图，`load_and_draw_ms` 为 133.5475、144.0962、174.0150、144.2153、115.8261 ms；完成实际 10,000 根缩放路径。OS 文件缓存未清空，因此不宣称冷启动。
- P03：沿用 r29 已核验的真实信号回放 Dispatcher probe：281 次 tick，最大间隔 179.4572 ms、任务运行 9,034.6387 ms，`replay_during_probe=true`，并在回放期间执行实际缩放；成交定位、成交/权益 Canvas、信号/成交表、只读重开和配置复制结论保留。
- 业务断言：F01 通过 UI 请求路径完成 2 intent/2 fills/1 closed，gross=20、fees=5、net=15、equity=10015；信号/成交定位有效；图表 20 个 bar children、权益 2 个 children、信号行 2、成交行 2；原快照切换/重开保持只读且未改区间；复制配置生成新 run 且原 run 文件哈希不变；末根记录 `no_next_valid_bar`；零交易胜率为 N/A；UI 取消返回 `cancelled`；回放/策略加载收尾任务均清理。
- 最后退出关卡：r29 已核验内部夹具先完成首根开仓并落盘 1 fill/fee=2/equity=9998，再让下一根策略卡住；调用与窗口关闭共用的取消、等待、StopWorker 收尾函数，Stop 前实际 Task/run/包内 Python Worker/第二根阻塞/simulation 均成立，Stop 后为 `cancelled/cancelled`、Worker 消失且未复用、对象清理完成，随后再次 F01 completed。该业务/退出结论保留；A05 实际像素和人工退出确认仍保留待验。

## 2. 具体合约识别

`TradeReplayRunner.ValidateTradeSeries` 识别并阻断：`KQ.m@...`、`KQ.i@...`、分隔后的 `888/999` 连续合约，以及主连/连续/指数/index/continuous 文字标识。TEST/显式合成验收序列仍允许；未知真实合约属性不猜测。

最终包中的 `fixtures/g6-kq-main.csv` 实测返回 `blocked_contract`、0 成交。普通 TEST F01 在同一包中正常完成。

## 3. 发布包

| 项目 | 结果 |
|---|---|
| 解压目录 | `artifacts/g6-offline-package-20261002-r39/` |
| Windows x64 压缩包 | `artifacts/g6-offline-package-20261002-r39.zip` |
| 压缩包 SHA256 | `7A81F068B0D56148B2B486C033990736D0448249315ED6358300FB82D1DC57AD` |
| 压缩包大小 | 150,113,546 bytes |
| .NET | self-contained `win-x64`，未裁剪，WPF/CLI/native 依赖随包 |
| Python | 3.12.10 Windows x64 embeddable；WPF/CLI 各自使用目录内 `python/python.exe` |
| 清单 | 包内 `manifest.json`，含 SHA256、字节数和版本摘要 |
| 说明/授权 | 包内 `README-offline.md`、`THIRD-PARTY-NOTICES.txt`、`licenses/` 实际授权文本和依赖版本清单 |

源码中的 `PythonRuntime.ResolveExecutable` 在发布目录优先解析 `AppContext.BaseDirectory/python/python.exe`，找不到时才使用 `QH_PYTHON_PATH`，最后回退到 `python`。发布验证未设置 `QH_PYTHON_PATH`，受限 PATH 只保留 Windows 系统目录。

## 4. 独立目录离线闭环

最终教程证据目录为 `artifacts/负责人 r22 最终包复核-20261002/`，包含中文/空格路径和独立用户数据，未修改全局 PATH；r39 修正包清单并移除自检模型重绘层，保留 r29 已核验的业务/退出结论；真实 WPF 像素诊断见 `artifacts/g6-wpf-render-diagnostic/result/`。

- 包内 CLI 导入 `fixtures/g3-bars-f01.csv`：发布 `snapshot-9adf9f76d098cb26`；质量检查为 3 根、0 gaps、0 issues；重复导入仍复用同一 snapshot，旧 run JSON SHA256 前后均为 `EB51AA3778B9E49DEAEF8DFCE1B8BE64891DBC86421555FD849A46F183F21302`。
- 包内 `trade_sequence.py` 的最终包 F01 CLI 路径为 `trd-20261002152410965-71ebd9b22`：completed、2 意图、2 成交、1 已平，gross=20、fees=5、net=15、equity=10015。
- 最终包矩阵短测：`g3-bars-short.csv` + `scenario=short` 得 gross=20、fees=5、net=15；`g3-bars-reverse.csv` + `scenario=reverse` 得 gross=40、fees=10、net=30、4 fills/2 closed。
- 最终包故意加载 `strategies/bad_syntax.py` 返回 `describe_failed`、exit=5，错误定位到 `SyntaxError: '(' was never closed`；这是策略加载失败证据，不是超时替代。
- 包内 CLI 成交读取、信号 CSV 和成交 CSV 导出均完成；最终包 README 固定了 `2026-10-01` 教程、策略 metadata/`on_bar`、CSV 时间戳/时区/`bar_end`/交易日/完成标记，以及严格/探索、费用/保证金/滑点/止损止盈和无未来数据约束。
- WPF r39 清理后诊断明确失败于真实像素捕获：控件绑定/业务路径通过，但实际根/Canvas/结果控件均为透明黑图；人工点击、拖拽、窗口关闭和全新机器仍需负责人验收。

## 5. 适用性能、资源和清理

### P01/P02/P03

本轮没有把离屏 `first_layout_ms` 当作 P01 冷启动或人工首页指标；P01 仅宣称适用启动阶段通过。P02/P03 采用 r29 已核验证据，未宣称长时间趋势或人工原生 UI 通过。

### P04 百万根

使用 r20 独立复核最终包 `cli/Qh.Cli.exe`、包内 `strategies/perf_zero_trade.py`、固定百万快照和受限 PATH，证据在 `artifacts/负责人 r20 独立复核-20261002/`：

- run `trd-20261002144736102-cef68cd0f`，1,000,000 根，completed，exit=0。
- 外层直接计时 `128,443.1954 ms`；C# Worker `125,796 ms`。
- 协议/序列化往返累计 `115,655.58 ms`；同步 `on_bar` 执行累计 `706.38 ms`；其余为撮合/账本与日志路径。
- `processed_rows=1,000,000`、`produced_intents=0`、`max_pending=0`、`duplicate_bars=0`。
- 本轮改为 Worker 内同步调用 `strategy.on_bar(context)`；父进程从每根 bar 发送时建立整根绝对 deadline，多条输出扣减剩余预算，超时诊断包含当前 bar 起止、策略副本和阶段，并终止隔离 Worker。未跳过策略、未提供未来序列、未改变撮合/协议语义，也未使用 future submit/result 伪装策略耗时。r19 的 10k 协议拆分只是受限预算诊断，未把约 44 ms 诊断开销外推到百万规模；同步阻塞管道候选因无法在不增加不可取消阻塞的情况下保持 deadline，未合并。

P04 目标仍未达到 30 秒。当前主要耗时在逐根协议往返和账本/日志路径，而不是 `on_bar` 函数体；因此本轮只记录同步执行改进，不宣称 P04 通过。

### P06/P08

- r29 最终包关键回归：`trade_timeout.py` 返回失败/部分结果，诊断含当前 bar 起止、策略副本和阶段；r29 自检的 UI cancel 返回 `cancelled`，并完成严格运行中 WPF 共享收尾关卡。
- 严格运行中收尾证据保留在 `artifacts/G6 离线验证 空格-20261002-r29/running-close-evidence/`；r39 诊断只用于确认清理后的真实像素结果为失败，不覆盖 r29 业务/退出结论。
- P08 r22：热身 15.035 秒后完成三轮 100,000 根 trade replay（均 exit=0、completed、processed=100000），另有一轮 timeout partial（exit=1、partial=true）；40 个低频样本、14 个 Python 样本，应用工作集 103.828–105.824 MB、Python 峰值 36.758 MB，正常关闭 exit=0 且剩余自有 PID 为 0。该窗口仅作适用性证据，不外推长期稳定性。

## 6. 交付与未验清单

发布脚本：`tools/package-offline.ps1`；进程树测量：`tools/measure-process-tree.ps1`。升级采用新解压目录，不覆盖用户数据；备份复制用户数据目录或导出 run 目录。包内策略契约、CSV 规范、配置/费用/保证金/风险假设见 README、`assumptions.json` 和现有 G3 报告。

## 7. A/P 逐项交付矩阵

| 编号 | 操作与实际结果 | 证据 | 状态 |
|---|---|---|---|
| A01 | r39 CLI/WPF 均可在无账号、受限 PATH、独立目录运行；人工首页未点验 | `artifacts/负责人 r22 最终包复核-20261002/`、r29 self-check | 离线部分通过，人工未验证 |
| A02 | 未接真实权限/空下载链路 | 本阶段范围声明 | 未验证 |
| A03 | 最终包重复导入复用同一 snapshot，旧 run JSON 哈希不变 | `artifacts/负责人 r22 最终包复核-20261002/duplicate-oldrun-summary.json` | 离线通过 |
| A04 | CSV 显式时区/交易日/period，教程日期固定；真实夜盘休市未接入 | r39 README、教程 import/quality | 离线部分通过，真实日历未验证 |
| A05 | r38 PNG 为控件模型重绘，不是实际 WPF 像素；r39 真实捕获诊断为透明黑图，控件绑定/定位/缩放断言仍保留 | r38 `self-check*.png/json`；`artifacts/g6-wpf-render-diagnostic/result/self-check.json` | 未通过，待现场人工/环境核验 |
| A06 | 最终包故意加载 `bad_syntax.py` 返回 exit=5 的 `describe_failed`/SyntaxError；timeout 也保留 bar/策略/阶段诊断 | `artifacts/负责人 r22 最终包复核-20261002/strategy-load-failure-summary.json`、r22 P08 | 离线通过，人工 UI 未验证 |
| A07 | warmup/正式区间、完成 K 线和无未来输入保持；2 条正式信号 | r29 replay、r29 self-check | 离线通过 |
| A08 | 信号无交易收益伪造；零交易胜率为 N/A | r29 replay、r29 self-check | 离线通过 |
| A09 | 最终包 F01、short、reverse 短测分别得到净15、净15、净30，成交/权益符合固定矩阵 | `artifacts/负责人 r22 最终包复核-20261002/numeric-package-summary.json`、trade 输出 | 离线通过 |
| A10 | 零交易 0 fills/N/A，末根不强平并记录 `no_next_valid_bar` | r29 self-check | 离线通过 |
| A11 | 只读重开字段与 run 一致；复制直接使用实际 UI 字段生成新 run，原 run 哈希不变 | r29 self-check `readonly_reopen/copy_config` | 离线通过 |
| A12 | 运行中真实入口被 gate 阻断，严格 WPF 共享收尾为 cancelled/cancelled，保留 1 fill/fee/equity，随后可再次 completed | r29 self-check `running_close_drain` 及其原始证据 | 离线通过，人工确认分支未验证 |
| A16 | 受控取消、共享收尾、正常窗口关闭和应用 PID 清理通过；修复异步 Closing 后直接 `Close()` 的 WPF 收尾异常 | r29 self-check；`G6 P08 r22-final-20261002` | 适用内部通过，人工关闭确认未验证 |
| A18 | r39 self-contained 包、清单排除自身且 ZIP 直读核对 840 项 0 mismatch、7 授权文本、中文/空格目录闭环 | r39 ZIP/manifest、r22 教程 | 包闭环通过，新机器未验证 |
| P01 | r22 五次外部启动到 `first_dispatcher_business_ready`：642.2456–676.6520 ms；全部 exit=0、passed=true | `artifacts/G6 P01 r22-clean-20261002/summary.json` | 适用启动通过，非人工首页 |
| P02 | 10,000 根五次 115.8261–174.0150 ms | r29 self-check | 适用测量通过，非冷启动声明 |
| P03 | Dispatcher 最大间隔 179.4572 ms，回放期间实际缩放/定位/取消路径通过 | r29 self-check | 适用内部通过，人工交互未验证 |
| P04 | 百万根 128443.1954 ms，completed/exit0，仍超 30 秒；10k 协议预算已拆分 | r20 独立百万原始证据；r19 10k breakdown run | 未通过，保留最小建议 |
| P05 | 实时/通知性能不在 G6 离线范围 | 范围声明 | 未验证 |
| P06 | 最终包 timeout/cancel、父级整根 deadline、运行中 WPF 共享收尾与 PID 收尾通过 | r29 self-check、r22 P08 | 适用离线部分通过 |
| P07 | 未做目标机器长时间实时稳定性 | 范围声明 | 未验证 |
| P08 | 热身 15.035 秒后三轮 100k 研究循环成功，另有一轮 timeout partial；40 个样本、14 个 Python 样本；工作集 103.828–105.824 MB，Python 峰值 36.758 MB，正常关闭 PID 退出 | `artifacts/G6 P08 r22-final-20261002/summary.json` | 有限窗口通过，不外推两交易日 |

离线阶段仍未验证：人工原生 UI、P01 人工首页、P04 目标、P05/P07、真实账号/权限、两完整交易日和任何实时/通知/下单链路。当前停在“G6 离线包待负责人验收”，不修改 `docs/04-负责人监督记录.md`。
