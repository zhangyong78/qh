# G2 第二轮返修复核：两项桌面流程收尾

时间：2026-10-02 14:43 起，Asia/Shanghai。WPF 独立构建0 warning/error。

独立证据：`artifacts/supervisor-g2-20261002/repair2-review-output.txt` 与 `repair2-extras-output.txt`。核心多/无信号、预热/覆盖门禁、确定性重复、旧run全部文件不变、取消、超时和进程退出回归继续通过。指定行异常含 strategy.py line18、traceback、STDERR_MARKER_G2_RUNTIME及stderr_final；无参数策略与period=4策略完成。工厂挂起约5447ms，P06五秒目标仍待调整；人工UI、规模性能未验证。

源码确认通用JSON唯一参数源、当前文件重新校验、已有快照选择、历史run加载绑定快照、匹配身份检查、加载关窗取消/等待、traceback及日志共享读取已经补齐。只剩两项明确控制流错误需要修正：

1. `ReplayButton_Click` 回放前调用 `LoadStrategyAsync(resetParameters:false)`，该方法仍无条件 `SetDefaultReplayRange()`，因此用户编辑的正式起止区间会在开始前被默认范围覆盖。重新校验当前文件应保留用户已确认的区间和参数；仅显式加载/新快照时初始化默认范围。补一个用户选择09:04～09:05，检查运行保存区间完全相同且只有09:05上穿信号的回归，以及自定义参数/区间一起保留的检查。
2. `LoadStrategyAsync` 的“已有加载任务，return false”保护位于try内部，其finally仍执行 Dispose/清空共享令牌和 `_strategyLoadOperation`，从而第二次点击会丢失第一项任务管理句柄。无所有权的提前返回必须在清理范围之外；清理只作用于本次创建的任务和令牌。补挂起加载期间重复加载，确认第一次任务仍可取消且关窗仍追踪等待，任务不存在旧结果覆盖和进程残留。

这是两个现有方法的最小修复，不需要增加功能或调整架构。修后保留本轮证据，仍不把源码/自检写成人工UI通过。

可同时准备G3的手算夹具说明，但暂不实现G3引擎：按 docs/03 第6节提交F01、空头、反向、资金不足、零交易、跨交易日普通平仓、比例费用、期末未平仓、跳空、缺口/取消的假设、输入与逐项预期。预期先交负责人审查，禁止用未来实现生成预期。G3完成后优先独立离线打包，不等待账号。
