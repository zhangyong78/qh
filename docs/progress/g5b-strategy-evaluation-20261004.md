# G5-B 完成 K 线策略求值（合成验证）

日期：2026-10-04（Asia/Shanghai）

## 范围

本阶段只补齐“LiveSessionCoordinator 接受完成 bar → 独立 Python 策略 Worker → LiveReminderEngine → 本机账本”的最小闭环。不接 CTP、不下单、不读取密码，不宣称真实 TqSdk 持续权限、断线现场或正式 G5 已通过。

## 实现

- `live_strategy_configs` 持久化策略路径、名称、版本、文件 SHA256、参数 JSON/摘要、预热需求/进度、有限历史、运行状态和最后错误。
- `live_strategy_evaluations` 以实例和 `bar_end` 为唯一键，保存求值阶段与候选，保证重复 bar 和重启后不会重复执行/重复入账。
- 每个提醒实例启动独立 `qh_signal_worker.py`；启动时校验策略描述、版本、哈希、参数和预热需求，并用已保存的有限历史恢复指标状态。
- 只处理 `LiveSessionCoordinator` 已接受的完成 bar。预热候选丢弃；正式候选交给 G5-A 引擎；恢复候选交给引擎但使用 `Recovery` 阶段，软件内保留、桌面/声音抑制。
- Worker 超时、退出或策略异常会把当前实例标为 `failed`，不阻塞其他实例；当前 UI 仍只有一个实时桥接会话入口。

## 合成验收

`g5b-fixture-check` 使用包内 `sma_ema_cross.py`、`qh_signal_worker.py` 和合成完成 K 线，覆盖：

- 预热不写信号、不通知；
- 正式均线交叉从完成 bar 产生候选并进入信号/通知尝试账本；
- 同一 bar 重复和新会话重启后去重；
- 恢复 bar 进入软件账本但通知被抑制；
- `runtime_error.py` 只使错误实例进入策略故障，主实例保持可用。

源码与发布包均需以退出码 0、`all_passed=true` 为准；真实 TqSdk 现场和正式 G5 仍待后续人工验证。

## 发布包完整性修复

独立复核发现包内 Python Worker 会生成 `__pycache__/*.pyc`，使 manifest 在正常运行后漂移。已对所有受管理 Python 进程设置 `PYTHONDONTWRITEBYTECODE=1`，并从发布包内清理缓存后重新生成 manifest。

最终发布包包含 6,729 项。G5-B、G5-A、G4 分别运行后，每次都核对实际文件集合和 manifest：6,729/6,729、缺失 0、额外 0、哈希失败 0、`__pycache__` 0、`.pyc` 0。正式快捷方式同参数启动 PID 33316，观察 12 秒仍响应，stderr 0 字节。真实现场与正式 G5 仍未通过。
