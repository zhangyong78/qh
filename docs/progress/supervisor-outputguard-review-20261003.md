# 输出保护候选实际审核：正常研究被诊断预算阻断

2026-10-03。证据 `artifacts/supervisor-outputguard-review-20261003`。

最终ZIP独立流式校验10015项大小/摘要全一致，无缺失/漏项/自身/运行数据污染。269809933 bytes，SHA `C4BDF9BC58DCD85A0EFA7D170548EF539C2354BEBE6B919E9B9264F943EF8490`。有界stdout/describe/stderr、有限尾部读取及分段方向正确，但候选不通过功能验收。

原协议每根都有bar_done，ReadResponseAsync把所有正常协议行写入worker.log；4MiB累积预算被当作用户策略异常并杀Worker。因此正常固定百万任务也必然提前失败。负责人实际只跑固定40000根零动作、同一快照/策略、20秒/1GB/1MB限制：run `trd-20261003052048541-d665b7210` 在24436/40000根返回failed/output_limit，诊断stream=worker_log、4194304 bytes预算、观测4194308 bytes，策略0意图/0成交，根本没有乱打印。外层7.945秒、Worker3.217秒、日志实际4194136 bytes、自有树峰645873664 bytes，已记录5个PID全部not_running，drain完成、外层限制未触发。不是P04百万重测；旧P04性能失败保持，新增正常功能回归必须修复。

另ReadResponseAsync虽然传入barContext，却在output_limit包装中丢弃，最终ErrorMessage只有phase/stream预算，没有当前bar/策略。run有策略元数据不等于错误位置字段完整。

## 唯一最小返修

1. 区分受控正常协议/诊断保留和用户异常输出。worker.log是诊断副本，采用有界滚动/采样/合理保留策略；达到诊断保留阈值不能把正常bar_done、正常信号/意图当异常杀回放，也不能为放行把总上限无限增大。正式signals/intents/fills/equity账本保持完整且不能静默丢弃。stderr、describe、异常超长协议行的真实输出保护继续生效，说明哪些输出会失败、哪些诊断被采样/轮换。
2. output_limit诊断在适用bar阶段保留现有barContext（起止时间、strategy、阶段）；加载/初始化明确其阶段。保持绝对deadline、取消与partial数据语义。
3. 有限验证一次40k零动作必须completed/40000根、诊断留存仍受限；再仅原有3根F01/9signals与超长stdout/stderr/describe/partial适用回归。20秒1GB1MB、独立目录/字节上限/串行，禁止百万、海量信号、长期采样和新性能候选。诊断保留边界可用内部小预算验证，不引入额外用户功能或日志平台。
4. 避免诊断每条同步/异步强制flush造成无必要成本，按既有缓冲/分段/收尾安全地写；不是性能架构改造。不得合并失败AutoFlush/token候选。
5. 生成严格新包、用最终包做适用验证、同步报告和current-status最终包。当前status仍只列旧r39/r3b/buf64提交，需真实反映本次拒绝/返修/新包，不用旧路径冒充最终候选。旧百万外层/资源原始文件若无，仍明确缺失；不重测来补造。G4既有功能保留，G5/现场/账号不推进。

负责人不编辑产品源码。此正常研究阻断可自行修复，暂无用户行动要求；不宣称完整离线或成品验收通过。
