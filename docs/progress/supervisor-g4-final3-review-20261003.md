# G4 final3 负责人独立审核

2026-10-03，Asia/Shanghai。本次未登录账号、未消费真实行情、未触发通知或交易。

结论：单份 SDK 部署与已有基础修复成立，G4 独立候选仍需四组最小返修；不放行 G5，不宣称完整成品。保留既有离线业务、运行中取消与去重的通过证据；A05 实际像素、P04、真实权限/现场及两完整交易日继续开放。

## 实际核验

- 独立直读 ZIP 全部 10,011 清单项，零不匹配、排除清单自身；268,834,764 bytes，SHA256 `C251E96FF6E632CD945638057C6DD285B3616E1AA2B8EA0E1A40C193AAF845CC`。安全解压到负责人审核目录，不使用开发包目录。
- 独立包 `g4-fixture-check` exit=0、all_passed=true；其范围是明确模拟夹具，不代表真实 bridge 的初始序列、专业 CSV 或恢复时序通过。
- 独立包首次 `g4-gateway-check` 返回 Success=false/Cancelled，进程仍 exit=0。直接使用包内 Python/shared tqsdk-site、stdin `op=runtime`，无账号导入实际 3.8.7 成功，37,342 ms。随后 CLI 热启动成功。冷启动取消与热启动成功均须保留，不能只保留后一条。
- 对最终包真实 bridge 函数运行 SDK 格式替身；直接引用最终包 Qh.Core.dll 编译最小审核程序，未引用待编译源码替代交付程序集。
- 源码已补 argv、受控生命周期、模式入口、上下文闸门和失败状态消费；共享 SDK 解析成立。损坏账本阻断、错误合约拒绝方向成立。

证据：`artifacts/supervisor-g4-final3-review-20261003/`，含 zip-manifest-check.json、fixture-report.json、runtime-probe.json/timing、cli-runtime-probe.json、bridge-audit.json、core-audit.json 与可重复审核脚本。首次 CLI 冷启动输出见负责人本轮工具输出，第二次热启动写入 cli-runtime-probe.json。

## 1. 历史完成证据与覆盖门禁

`professional_history` 使用 `index < len(rows)-1` 判完成：两行前缀 CSV 的末行位于请求区间时恒为 false，coverage=false。不是所有下载都会失败：SDK 可能写入请求 end 处的额外行，它被过滤后可作为后继证据；问题是没有该额外行时（例如序列/交易时段边界或非对齐截止），合法历史末根也一律未完成。需利用可靠的供应商同步/后继完成证据处理区间末根；不能一律改 true，也不能只凭本机 now。

显式 CSV `is_complete="false"` 目前 `bool("false")` 得 true。应严格解析完成值，非法或未知值不能猜真。

最终包 Core 对09:00至09:04请求、只含09:02一根且provider complete=true的结果，TryValidate 返回 true；同一审核中的四根正常结果通过、错误合约正确拒绝。父级须独立核对请求覆盖与Coverage字段一致性，不只相信 Complete 标志。有效时段缺口与休市须遵循已有日历规则，不能机械按所有自然分钟计数。

补实际 bridge→gateway→publisher 的成功/不足覆盖/未确认末根/显式 false 证据，不能只增加 FixtureGateway 布尔标签。

## 2. 供应商字段及真实权限错误映射

标准历史 row 的 trading_day 被构建 values 字典时丢弃：输入2026-10-09，最终输出2026-09-29。live 两处 values 同样丢弃该字段。直接 bar_from_values 能保留字段，不代表接入路径能保留。修正标准/专业/实时端到端映射与验证。

包内3.8.7 datetime.py的18点/周末规则确实与当前 fallback 一致，但没有节假日日历。可陈述SDK有限规则，不得称已验证交易所节假日归属；缺字段时按冻结方案明确来源/可验证程度，既有节假日夹具与供应商值优先。

包内 DataDownloader 在 `_has_feature("tq_dl")` 不满足时的实际中文异常，被 classify_exception 判 Network，而非 Permission。准确映射并确认没有网络重试、没有成功覆盖、无敏感诊断。

## 3. 恢复就绪、缺口与重启边界

离线替身依照真实 `get_kline_serial` 初始占位再由 wait_update 填充的时序：初次正常记录09:00→断线→新session序列仍占位时已发recovered并置recovering=false→第一次同步后只发09:02、is_recovery=false；09:01完全缺失。WPF因此会将旧行情恢复为Connected/普通输入。这不是仅内部去重夹具能验证的路径。

先确认真实序列就绪，再完成应补区间并静默消费；未补齐不得恢复正常输入。固定data_length=5不足以保证较长断线完整补缺；超出可取范围/权限不足须保持明确暂停或未完整状态。以持久化最后确认边界处理进程重启，不只依靠本Python进程emitted集合。正常SDK自恢复与异常重建路径都须有真实bridge协议层状态证据；随后真正新增完成bar才恢复正常。

最小验证：首次占位→就绪、多根恢复、超过5根的缺口、补齐受限、重启边界去重、后续新增普通bar。均无账号可用可控SDK替身；不触发通知，不改变策略合同。

## 4. 运行时诊断的期限与结果

CLI 3秒取消不足以覆盖本次新解压首次SDK导入，且Cancelled返回exit0。修正运行时依赖检查的可取消期限与退出判据，所有Success=false均失败；不无界等待。区分依赖导入准备与真实网络握手，避免慢冷导入被报为已连接/网络故障。记录冷启动/热启动、实际版本/模块可导入及取消退出确认。无需重复百万根性能测试。

同步当前阶段/header、报告与包README的已验证/未验证口径，不把本次补丁称完整G4或离线成品，不修改04。以上修复后重打包、核验清单，提交新最终包与集成证据。
