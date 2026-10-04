# P04 deadline token 候选实验

日期：2026-10-03（Asia/Shanghai）

负责人审核要求的唯一新候选是：在 `TradeReplayRunner` 的逐根响应读取中复用已经建立的整根 `barDeadline.Token`，去掉每条回复额外创建的 linked CTS/`CancelAfter`；初始化、finish、cancel 的独立期限不变，协议、Worker 隔离、模拟账户上下文和撮合语义不变。

结果：候选没有改善，已回退，不进入最终包。A/B/A/B 使用同一最终 CLI、同一快照和同一 Python Worker；Worker 为旧 `644/641ms`、候选 `646/659ms`，协议往返为旧 `519.29/507.54ms`、候选 `517.71/518.14ms`，远低于约25%改善门槛。没有执行百万复测。

回归全部通过：

- 12 项固定数值/费用/撮合结果与批准值一致，见 `artifacts/p04-candidate2-20261003/numeric/numeric-candidate2.json`。
- partial cancel 保留 7120 根权益记录，Worker exit=0；初始化卡住为 `failed/protocol_timeout`、0根且无残留。
- 多回复受控慢发送在旧/候选版本均为 `strategy_timeout`、0根，不能延长整根 2.25s deadline。

旧版守护器的 `ReadToEndAsync` 曾把完整 stdout/stderr 累积在监控 PowerShell 进程，且收尾无界；现已改为流式文件复制、2 秒有界收尾，并以原生 `Get-Process.StartTime.ToUniversalTime().Ticks` 作为唯一创建身份来源，CIM CreationDate 仅作诊断。收尾按累计的已知 PID+ticks 核验；PID 复用、未知身份均不自动当作自有或已退出。结果记录实际 CLI 路径、参数、Core/Worker 摘要、PID 命令行、内存、输出大小、drain、监控输出入口和退出身份。现工具默认 20 秒、1GB 自有进程树私有内存、1MB stdout/stderr、250ms 低频采样；百万如获后续明确授权才显式传入 90 秒。

静态复核纠正既有异常口径：实际加载路径为包内 `cli/Qh.Cli.exe` 和 `cli/worker/qh_signal_worker.py`，临时策略副本内容与声明一致；策略声明的是每根10000或60000条，回放请求1000根。固定读取50%/99%字节位置的完整行后，六处累计 Sequence 均落在对应 BarEnd 的每根预期区间，支持“高密度逐根输出累计导致大文件/内存并被外层守护停止”，不支持“输出重复”。`TradeReplayRunner` 只每1000根或末根写进度，隔离 run 在中止前的 `ProcessedRows=0` 只是起始落盘值，不能推断仍停首根或 deadline 失效；早期49GB来源仍未知。现有大文件不再重跑，也不据此修产品循环。

受限原包验证使用独立 3 根 CSV、每根最多 10 条、每条描述不超过 256 字符，最多两次 replay：2 条 0.8 秒慢回复仍为 `failed/strategy_timeout`、0根；3 条正常多回复为 `completed`、3/3根、Worker exit=0。两次均未触发 20 秒/1GB/1MB 限制，stdout/stderr 均完成 drain；当时记录的 `alive_after=false` 仅作观察，因守护器身份精度 bug 不作为严格的退出身份结论。证据在 `artifacts/p04-deadline-final-20261003`。

守护身份最小验证仅使用两个低内存 PowerShell 辅助进程：2秒自然结束样本为 exit=0、无限制、身份 `not_running/alive_after=false`；1秒全局期限样本为 `global_timeout`，仅终止已知 ticks 匹配对象，未知身份数为0、无残留。证据在 `artifacts/p04-guard-identity-check-20261003`。
