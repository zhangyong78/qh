# P04 性能有界实验记录

日期：2026-10-03（Asia/Shanghai）  
范围：固定百万快照的 1,000 / 10,000 根子区间、交易回放 Worker；不修改百万基准、不进入 G5、不触碰 `docs/04-负责人监督记录.md`。

## 结论

P04 仍未达标。原固定百万实测为 `128443.1954ms`，Worker `125796ms`，协议/序列化 `115655.58ms`；buf64 开发测量的唯一百万 run 为 `42460.8ms`，Worker `40278ms`，协议往返 `37390.3293ms`，会计/日志 `2138.3587ms`，同步策略 `263.4642ms`，1,000,000 根且 exit=0。负责人同环境冷热交替 A/B/A/B 复核的 buf64 稳定改善约 7.3%，因此不能把跨运行的 66.9% 历史差异全归因于缓冲；无论采用哪一口径，结果都高于 30 秒，结论保持失败/未达标。本轮没有重复 PNG 或扩大性能架构。

## 现有热点与基线

已核对 `SignalReplayRunner`、`TradeReplayRunner`、`worker/qh_signal_worker.py` 和 `TradeRunStore`：

- 主要耗时在逐根 JSON stdin/stdout 往返；最新百万结果的同步策略执行仅 `706.38ms`，不能把策略时间当作总耗时。
- `TradeRunStore` 已按 run 持有 JSONL `StreamWriter`，并非逐记录开关文件；当前每 4096 行刷新。因此本轮没有重复实现“持久 writer”或改动协议/撮合语义。
- 固定源版本 self-contained CLI 的串行基线，使用快照 `snapshot-e7d5b1de50f8d24b` 和零动作策略 `perf_zero_trade.py`：

| 样本 | 外层墙钟 | Worker elapsed | 协议往返 | 会计/日志 | processed | exit |
|---:|---:|---:|---:|---:|---:|---:|
| 1,000 | 3258.0ms | 169ms | 73.8064ms | 10.9514ms | 1,000 | 0 |
| 10,000 | 3454.2ms | 1177ms | 945.9758ms | 132.2507ms | 10,000 | 0 |

证据：`artifacts/p04-source-baseline-1k-20261003.txt`、`artifacts/p04-source-baseline-10k-20261003.txt`，以及对应 run 的 `replay-metrics.json`：

- `trd-20261003012709376-2af24fa39`
- `trd-20261003012712122-5d2c4fa6c`

## 候选与结果

在 `src/Qh.Core/TradeRunStore.cs` 仅将现有 JSONL journal 的 `FileStream` 和 `StreamWriter` 缓冲设为 64KiB。它不改变记录内容、撮合顺序、取消、Worker 隔离或整根 deadline。项目隔离 SDK `tools/dotnet/dotnet.exe` 编译通过，候选版固定小样本结果如下：

| 样本 | 版本 | Worker elapsed | 协议往返 | 会计/日志 | processed | exit |
|---:|---|---:|---:|---:|---:|---:|
| 1,000 | 基线 / 候选 | 169ms / 268ms | 73.8064ms / 154.5282ms | 10.9514ms / 29.3265ms | 1,000 / 1,000 | 0 / 0 |
| 10,000 | 基线 / 候选 | 1177ms / 804ms | 945.9758ms / 650.205ms | 132.2507ms / 66.7888ms | 10,000 / 10,000 | 0 / 0 |

这组早期开发基线不是负责人同环境因果 A/B，只用于定位热点；1k 受启动/抖动影响未改善，10k 的初步差异也不能直接作为 buf64 因果收益。负责人随后用同一最终 CLI 串行 A/B/A/B 得出 buf64 约 7.3% 稳定改善，详见下节；没有把任一小样本或跨运行百万差异外推为通过。

## 取消与整根 deadline 回归

使用候选版 self-contained 离线 CLI，串行完成两项语义检查：

- 取消：`trd-20261003013343524-b4abe9917`，10,000 根请求在已进入回放后取消，状态 `cancelled`，`ProcessedRows=4575`，`replay-metrics.json` 的 Worker `exit_code=0`；`equity.jsonl` 有 4575 行，检查时活动 `qh_signal_worker.py` 数为 0。
- 整根 deadline：`trd-20261003013346231-b637d4a6f`，1,000 根固定超时策略首根睡眠超过 deadline，状态 `failed`、`ErrorKind=strategy_timeout`、`ProcessedRows=0`，诊断包含 bar 起止时间/策略/阶段，预算 `2.25s`，Worker 被终止并无后续根被接受。

百万证据：`artifacts/p04-candidate-million-20261003.txt` 及 run `trd-20261003013442390-e5339821b` 的 `run.json`、`replay-metrics.json`；run 为 completed、1,000,000/1,000,000、0 intents、0 fills，检查时活动 Worker 为 0。

小样本/语义证据：`artifacts/p04-candidate-1k-20261003.txt`、`artifacts/p04-candidate-10k-20261003.txt`、`artifacts/p04-candidate-cancel-10k-20261003.txt`、`artifacts/p04-candidate-deadline-1k-20261003.txt` 及对应 run 目录下的 `run.json`、`replay-metrics.json`、`equity.jsonl`、`worker.log`。

包证据：`artifacts/g6-offline-package-20261003-p04-buf64.zip`，SHA256 `CB7F47611867DB60E0B432A0921A52DD2BFD185C053DADFCC084F2E3A89851C2`，269,652,080 bytes；manifest 声明 10013 项，ZIP 排除 manifest 后 10013 项，缺失/未声明/哈希失败均为 0。

包内 CLI smoke：`artifacts/p04-package-smoke-1k-20261003.txt`，使用包内 `cli/python/python.exe`、`cli/worker/qh_signal_worker.py` 和 `strategies/perf_zero_trade.py` 完成 1,000/1,000、completed、exit=0。

## 后续边界

P04、A05、真实账号/权限、两完整交易日和负责人审核仍开放。下一轮若继续性能工作，应先由负责人决定是否接受“42.46s 的有限改善仍不达标”，再针对逐根 JSON IPC 做新的受控候选；本轮不继续扩大架构、不进入 G5。

## 审核后唯一候选：复用整根 deadline token

负责人复核指出：buf64 的同环境旧 final9-r3 / 新 buf64 A/B/A/B 10k Worker 为 `734/663/693/660ms`，均值约改善 7.3%；因此不能把历史 `128443ms → 42460.8ms` 全部归因于 64KiB 缓冲。本节按审核要求只验证一个新的现有热点：`TradeReplayRunner` 已有整根 `barDeadline`，而 `ReadResponseAsync` 每条回复还创建 linked CTS/`CancelAfter`。候选仅尝试复用整根 token，初始化、finish、cancel 的独立期限保持不变；候选源码随后已回退。

同一数据根、同一 Python Worker、同一最终 CLI 运行时，在旧版守护器的 20 秒全局上限、4GB 自有进程树私有内存上限、250ms 采样下串行 A/B/A/B；该 A/B 数字有效，但旧守护器后来被修正为流式落盘和 1GB/1MB 小实验限制：

| 次序 | 版本 | Worker elapsed | 协议往返 | 会计/日志 | 外层 guarded wall | exit |
|---:|---|---:|---:|---:|---:|---:|
| A1 | buf64 | 644ms | 519.29ms | 48.83ms | 2939.7ms | 0 |
| B1 | deadline-token 候选 | 646ms | 517.71ms | 49.93ms | 2875.5ms | 0 |
| A2 | buf64 | 641ms | 507.54ms | 49.34ms | 2852.5ms | 0 |
| B2 | deadline-token 候选 | 659ms | 518.14ms | 52.33ms | 2822.5ms | 0 |

候选没有改善，更没有接近约 25% 门槛；因此未执行授权的百万复测，未把该候选打入最终包，源码已恢复为 buf64 版本。

## 候选语义回归

- 12 项固定数值/费用/撮合矩阵全部通过：`artifacts/p04-candidate2-20261003/numeric/numeric-candidate2.json`，F01、空头、反向、多手、止损、止盈、比例费、资金不足、期末持仓、末根意图和零交易结果与批准值一致。
- 取消 partial：`trd-20261003023416110-7425045c4` 为 `cancelled`、7120 根、Worker exit=0，权益行数核对为7120；初始化卡住：`trd-20261003023419299-6c28f66f3` 为 `failed/protocol_timeout`、0根，守护进程无残留。
- 受控多回复慢发送：旧 buf64 与候选均为 `failed/strategy_timeout`、0根，Worker elapsed 约2370/2336ms，说明多条回复不能把整根 2.25s deadline 延长。证据为 `artifacts/p04-candidate2-20261003/multi-old-controlled-*` 和 `multi-new-controlled-*`。
- 大量回复压力夹具曾分别触发旧守护器的 20 秒 global timeout（峰值约2.32/2.47GB）和一次 4GB private-memory limit（峰值约4.41GB）；静态核对确认策略声明是“每根”10000或60000条、请求1000根，50%/99%位置的累计 Sequence 落在对应 BarEnd 的预期区间。`run.json` 只在每1000根或末根更新，外层中止前为0不能证明停在首根；49GB 早期来源仍未知。临时 run 已移入 `artifacts/p04-candidate2-20261003/quarantine`，仅作资源异常证据，不据此归因输出重复或产品 deadline。

最终可交付包仍是 `artifacts/g6-offline-package-20261003-p04-buf64.zip`，没有因未通过的 deadline-token 候选重新打包。现守护器默认 20 秒、1GB、1MB 输出上限，使用原生 StartTime UTC ticks 累计核验自有身份，未知身份不自动视为已退出，并记录实际 Core/Worker 摘要和 stdout/stderr/timing 路径；两次低内存辅助验证见 `artifacts/p04-guard-identity-check-20261003`。本轮候选未达到约25%门槛，故没有再次消耗百万运行预算。

## 唯一 AutoFlush 候选（2026-10-03）

先用最终包的实际 CLI runtime（`Microsoft.NETCore.App 10.0.12`）探针确认 `Process.StandardInput.AutoFlush=True`。候选只在 `TradeReplayRunner` 启动 Worker 后设置 `StandardInput.AutoFlush=false`；`SendAsync` 仍对每条完整 JSON 行显式 `FlushAsync(token)`，没有合并多根、改变协议或 deadline。候选构建通过，实际 Core/CLI/Worker 哈希冻结在 `artifacts/p04-autoflush-candidate-20261003/frozen-hashes.json`。

同一 `snapshot-e7d5b1de50f8d24b`、同一零动作策略、同一数据根，使用流式守护器 20 秒/1GB/1MB 限制串行完成 1k A/B 与 10k A/B/A/B；6 次均 `completed`、处理根数正确、未触发限制，stdout/stderr drain 完成且无记录自有 PID 残留：

| 样本 | 基线/候选 Worker | 基线/候选协议 | 基线/候选外层墙钟 |
|---:|---:|---:|---:|
| 1,000 | 184 / 126ms | 92.1987 / 54.1913ms | 2469.1 / 4628.8ms |
| 10,000 A/B-1 | 1003 / 925ms | 820.8388 / 763.5038ms | 3424.7 / 3475.2ms |
| 10,000 A/B-2 | 970 / 1009ms | 791.2766 / 826.1745ms | 3465.0 / 3432.5ms |

10k 均值为 Worker `986.5ms → 967.0ms`（约 `1.98%`）、协议 `806.0577ms → 794.8392ms`（约 `1.39%`），外层墙钟 `3444.85ms → 3453.85ms`（约 `+0.26%`，候选略慢）。未达到约25%门槛，候选已回退，未执行多手/止损/取消/慢回复回归，不重跑百万，不重建最终包。完整证据为 `artifacts/p04-autoflush-candidate-20261003/autoflush-candidate-evidence.json`。负责人实读的 r20 原始百万外层记录 `artifacts/负责人 r20 独立复核-20261002/million-timing.json` 只有 `128443.1954ms/exit0`（package_sha 前缀 `05FE3E85…`），不能支撑 buf64 的 `42460.8ms`。buf64 的 `42460.8ms`、Worker `40278ms` 保留为开发报告及原始 run/replay-metrics 中已复核的分项值；buf64 原始外层计时和独立资源文件缺失，不补造、不重跑替代。

## 策略输出保护与有限回归（2026-10-03）

本轮按监督记录只处理现有策略加载、描述、Signal/Trade Worker 的 stdout/stderr 和日志路径，不改变协议字段、状态机、bar deadline、取消语义或 Worker 隔离边界。保护预算如下：单条协议 stdout 行最多 `1 MiB`；每个 Worker 的 `worker.log` 诊断保留预算 `4 MiB`，按 `64 KiB` 写入 `worker.log`、`.part-0001` 等分段，达到阈值后停止追加而不失败；Worker stderr 总预算 `256 KiB`，同样按 `64 KiB` 分段；describe stdout+stderr 共用 `256 KiB`；诊断 tail 只反向读取最多 `16 KiB`，最终消息最多 `4000` 字符。

协议行、stderr、describe 输出超出预算时报告 `output_limit`，包含阶段/流/限额/观测值，受管 Worker 终止并将本次 run 标为失败；worker.log 诊断保留达到预算后只停止追加，不把正常 `bar_done` 当作用户异常。正式 signal/intent 不静默丢弃。已有会计结果保留在失败 run 中，例如部分输出夹具在第 2 根触发限制时仍保留 `1 intent / 1 fill`；修复后的同包重跑完成 3 根。bounded line reader 保留缓冲区中下一行，避免一次读取多行时丢失正式响应。

有限 3 根夹具证据位于 `artifacts/p04-output-guard-tests-20261003`，新包回归位于 `artifacts/p04-package-output-guard-20261003/package-regression-summary.json`。正常 F01、9 个正式 signal 和失败后重跑通过；stdout 超长行、stderr 超预算、describe 超预算和部分输出均按预期触发 `output_limit`。各项外层均使用 20 秒、1GB 私有内存、1MB stdout/stderr 守护，drain 完成且无自有进程残留；未进行百万级或压力测试。

首个输出保护包 `artifacts/g6-offline-package-20261003-p04-outputguard.zip` 已被监督复核拒绝：固定 40,000 根零动作在 24,436 根因 `worker_log` 4MiB 诊断预算误触发 `output_limit`，0 意图/0 成交。返修将 worker.log 改为非致命有界诊断保留，达到阈值后停止追加，不再终止正常回放；stderr、describe 和协议超长行仍保持致命保护，并补全 bar 起止时间、策略和阶段上下文。

返修后最终候选包为 `artifacts/g6-offline-package-20261003-p04-outputguard-r2.zip`，SHA256 `6591857DB65A8DBDB82F1E4BB57B726BA4CAB1C385379818140D7A373CC641E7`，269,877,764 bytes。最终包 40,000 根固定零动作完成 `40000/40000`、0 意图/0 成交，worker.log 仍受限约4MiB；原 3 根 F01、9 signals、超长 stdout/stderr、describe 超限、partial 和失败后重跑回归符合预期。清单声明/目录实际/ZIP 均为 10,015 个文件，缺失、未声明、哈希错误均为 0。该包仍只是输出保护返修候选，P04 百万根性能结论保持失败，不进入 G5。
