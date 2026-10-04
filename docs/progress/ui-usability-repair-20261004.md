# UI 可用性最小返修记录（2026-10-04）

## 目标

围绕日常主路径“选择已有周期数据或下载 → 选择策略 → 回测 → 看结果”降低操作负担，不改底层行情、回测算法、实时提醒、CTP 或真实下单链路。

## 本次改动

- 数据页第一块改为“选择已下载/已导入的数据”，快照条目显示具体合约、中文周期、日期范围和根数；内部仍以 `SnapshotId` 作为键。
- 快照加载后，数据页和策略页都显示当前合约、周期、覆盖区间和根数；策略页提供“去数据页切换合约/周期”。
- 下载入口保留 15/30/60 分钟等用户可读周期；CSV 降到“CSV / 高级”区域。
- 均线参数标签改为“快线根数/慢线根数”，并说明根数随当前 K 线周期换算，不与下载周期混淆。
- 信号策略和交易策略卡片、主回测按钮及结果含义明确区分：信号只产生研究信号；交易策略才做本地模拟成交和盈亏计算。
- 删除数据页末尾空的“CSV / 高级”占位框；主按钮会随策略卡片和已加载 descriptor 实时显示“开始信号回放（无盈亏）”或“开始模拟交易回测（有盈亏）”。
- 策略页当前数据区域增加周期下拉框：按当前合约筛选并按周期去重，选择已有 15/30/60 分钟快照后自动加载最新对应数据；回测任务运行时锁定并防止重复切换。
- 数据页下载表单增加具体合约选择下拉框：从当前数据根已发布快照去重生成，白糖显示为“白糖 · CZCE.SR701”；保留手动输入其他具体合约的折叠入口。
- 下载日期改为 Windows `DatePicker`；开始日从 00:00 开始，结束日自动转换为次日 00:00，保持现有北京时间和左闭右开语义；精确 ISO 时间只留在高级入口。
- 加载数据或策略后自动填充回测区间，并提示北京时间、左闭右开，减少 ISO 时间和内部术语负担。

## 验证

- `QhApp` Release：0 警告、0 错误。
- `Qh.Cli` Release：0 警告、0 错误。
- G5-A/G5-B fixture：均 `exit=0 / all_passed=true`。
- 正式发布包 G4/G5-A/G5-B fixture：均 `exit=0 / all_passed=true`。
- 正式包 `D:\mycode\qh\artifacts\sr2701-backtest-20261003`：manifest 6,729 项；缺失 0、额外 0、哈希错误 0、`__pycache__` 0、`.pyc` 0。
- 桌面快捷方式启动后窗口标题为“期货研究与回测工作台”，进程响应正常；启动命令仍使用原有数据根和交易 run 参数。
- 最小 UX 二次返修后，正式快捷方式 PID `15076`，`Responding=True`；manifest SHA256：`C2DBFA621BF6B276D227861E6A428EF654A10716445A44B8C340202E6A6A8913`。
- 二次返修后的正式包 G4/G5-A/G5-B fixture 仍全部 `exit=0 / all_passed=true`。
- 周期切换返修后的正式快捷方式 PID `27824`，`Responding=True`；manifest 仍为 6,729 项，缺失/额外/哈希错误/缓存文件均为 0；manifest SHA256：`10D3EA5562B69EC34F5BAAEB4AAD97B348274C09B142FC8CE2A24C623402C05D`。

## 下载表单返修（负责人复核后）

- “具体合约”主入口改为从当前数据根已发布快照生成的可读下拉列表；白糖显示为“白糖 · CZCE.SR701”，并保留手动输入其他具体合约的高级入口。
- 下载日期改为 Windows `DatePicker`；开始日按北京时间 00:00，结束日转换为次日 00:00，保持现有左闭右开请求；精确 ISO 仅在高级入口。
- 无 `QH_TQSDK_CALENDAR_PATH` 且无包内 `calendar.json` 时，仍由现有日历门禁阻止请求并给出配置提示；没有虚假下载。
- Release 构建：QhApp/Qh.Cli 均 0 警告、0 错误；正式包 G4/G5-A/G5-B fixture 均 `exit=0 / all_passed=true`。
- 正式包离屏自检确认新控件初始化：`required_controls=True`、`startup_ready=True`；整体 `passed=False` 仍仅包含既有 `running_close_drain=False`、`result_rendered=False`。
- 最终快捷方式 PID `32364`，`Responding=True`；manifest 6,729 项，缺失/额外/哈希错误/`__pycache__`/`.pyc` 均为 0；manifest SHA256：`4999D88779F3DA0A0F777DED28B235E22EF32234BEE0150985F2D0027C7F0930`。

## 选择值覆盖修复

- 下拉选择、手动输入现在是显式互斥模式：选择下拉（包括重新选择同一项并关闭下拉框）会清空手动值并使用选中的具体合约；输入手动值会进入手动模式并使用手动代码。
- `download-form-check` 离线检查已验证：手输 A → 选择白糖请求代码为 `CZCE.SR701`；手动输入 B → 请求代码为 `SHFE.rb2410`；整日日期范围转换正确；`DateTime.MaxValue` 结束日期明确拒绝。
- 修复后的正式包 `download-form-check` 和 G4 fixture 均 `exit=0`；桌面快捷方式 PID `24372`，`Responding=True`；manifest 6,729 项，缺失/额外/哈希错误/缓存文件均为 0；manifest SHA256：`86002E35D6E64E97B2CCEFA100D6A60CF5C5F11AE783C01B7F4FE268B5DBFCE7`。
- 离屏渲染证据：[self-check.png](../../artifacts/ui-repair-package-selfcheck-20261004/self-check.png) 已显示数据页第一入口、当前数据摘要和下载周期入口。

## 限制

当前 Computer Use 原生 WPF 绑定接口不可用，因此未完成真实人工鼠标点击的 15→30→60 分钟切换验收。离屏自检已覆盖启动、控件存在、数据加载、信号回放、交易回放、结果渲染和导出；其中现有自检的 `running_close_drain` 与 `result_rendered` 条件仍报告未通过，属于既有自检时序问题，不作为本次 UI 返修通过依据。正式 G5、真实 TqSdk 持续权限和现场长时间验证仍保持未通过状态。
