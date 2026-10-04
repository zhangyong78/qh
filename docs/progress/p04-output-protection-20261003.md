# P04 策略输出保护记录（2026-10-03）

## 范围

只保护现有策略描述、Signal/Trade Worker 的 stdout/stderr、协议行和运行日志；不修改协议字段、撮合/账本语义、bar deadline、取消语义或 Worker 隔离边界。不修改 `docs/04-负责人监督记录.md`，不进入 G5。

## 限额与行为

| 输出面 | 限额 | 超限行为 |
|---|---:|---|
| 单条协议 stdout 行 | 1 MiB 字符 | `output_limit`，带阶段和观测值 |
| Worker `worker.log` 诊断保留 | 4 MiB | 64 KiB 分段写入，达到阈值后停止追加，不失败 |
| Worker stderr 总量 | 256 KiB | 64 KiB 分段写入，超限失败 |
| describe stdout+stderr | 256 KiB | describe 失败，不创建 run |
| 诊断 tail | 反向最多 16 KiB；消息最多 4000 字符 | 有界诊断，不使用 `ReadToEnd` |

stderr、describe 或单条协议行超限时受管 Worker 被终止，run 标为失败；worker.log 仅是诊断副本，达到保留阈值后停止追加，不终止正常回放。正式 signal/intent 不静默丢弃；部分输出产生的既有 intent/fill 保留在失败 run 中。日志段文件使用 `worker.log`、`worker.log.part-0001` 等名称。

## 有限证据

夹具和汇总：`artifacts/p04-output-guard-tests-20261003/finite-summary.json`。

- 正常 F01：3 根完成，2 intents / 2 fills。
- 9 signals：3 根完成，9 个正式 signal，`signals.jsonl` 为 9 行。
- stdout 超长行：`output_limit`，单行超过 1 MiB。
- stderr 超预算：`output_limit`，生成 4 个 64 KiB stderr 段。
- describe 超预算：在加载/描述阶段以 `output_limit` 失败。
- 部分输出：处理 1 根后触发限制，保留 1 intent / 1 fill。
- 失败后重跑：3 根完成，2 intents / 2 fills。

所有有限测试均由 20 秒、1GB 私有内存、1MB stdout/stderr 外层守护执行，drain 完成且无自有 PID 残留；没有进行百万级或大压力测试。

## 包级回归与交付包

包级汇总：`artifacts/p04-package-output-guard-20261003/package-regression-summary.json`。新包通过正常 F01、9 signals、四类输出限制、部分结果保留及失败后重跑回归；根清单、目录和 ZIP 严格核对均为 10,015 个文件，缺失/未声明/哈希错误为 0。

## 监督复核与返修

首个包 `artifacts/g6-offline-package-20261003-p04-outputguard.zip` 被拒绝：固定 40,000 根零动作在 24,436 根因 `worker_log` 4MiB 诊断预算失败，策略本身为 0 intent / 0 fill。原因是正常 `bar_done` 协议诊断被错误当作用户异常输出。

返修后，`worker.log` 使用非致命有界保留：达到 4MiB 后停止追加，不终止正常回放，也不无限提高上限；stderr、describe 和单条协议超长行仍触发 `output_limit` 并终止受管 Worker。日志写入不再逐条强制 flush。超限错误在适用 bar 阶段保留 `bar_start`、`bar_end`、`strategy` 和 `phase`。

最终返修包：`artifacts/g6-offline-package-20261003-p04-outputguard-r2.zip`  
SHA256：`6591857DB65A8DBDB82F1E4BB57B726BA4CAB1C385379818140D7A373CC641E7`  
大小：269,877,764 bytes

最终包验证：固定 40,000 根零动作 `completed/40000/40000`、0 intent、0 fill；worker.log 受限约 4MiB；原 3 根 F01、9 signals、stdout/stderr/describe 超限、partial 及失败后重跑均符合预期。清单、目录和 ZIP 严格核对均为 10,015 个文件，缺失/未声明/哈希错误为 0。证据位于 `artifacts/p04-outputguard-r2-validation-20261003`。

该包只包含输出保护返修候选。P04 的百万根性能目标仍未达到，不能据此改写失败结论，也不触发 G5。
