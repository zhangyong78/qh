# G0 阶段报告：环境、语义与最小夹具

日期：2026-10-02，Asia/Shanghai  
阶段状态：已完成实施记录，待负责人审核；已获开发授权，进入 G1

## 1. 环境事实

- 操作系统：Windows 11 教育版，版本 `10.0.26200`，x64。
- PowerShell：`7.6.5`；本轮命令均按 PowerShell 语法执行。
- CPU：Intel(R) Core(TM) Ultra X7 358H，16 核/16 线程。
- 内存：`33,873,752,064` bytes，约 32GB。
- D: 磁盘：总计约 700.6GB，可用约 675.7GB。
- 显示缩放：注册表 `LogPixels=120`，约 125%。
- Python：`C:\Users\31612\AppData\Local\Programs\Python\Python312\python.exe`，`Python 3.12.10`。
- 系统 .NET：`C:\Program Files\dotnet\dotnet.exe`，Host/runtime `8.0.7`，包含 Windows Desktop Runtime，无 SDK。
- 项目隔离 .NET：`D:\mycode\qh\tools\dotnet`，SDK `10.0.401`，Host/runtime `10.0.12`，安装命令为：

  `Invoke-WebRequest -Uri https://dot.net/v1/dotnet-install.ps1 ...; pwsh -NoProfile -ExecutionPolicy Bypass -File ... -Channel 10.0 -InstallDir D:\mycode\qh\tools\dotnet -NoPath`

该 SDK 只放在本项目 `tools/dotnet`，不改系统 PATH，不升级全局 Python。安装包来源为 Microsoft 官方 dotnet-install 脚本下载的 SDK 压缩包；安装完成后由 `tools/dotnet/dotnet.exe --info` 复核。

## 2. 语义冻结

| 主题 | 冻结口径 |
|---|---|
| 显示时间 | 统一 Asia/Shanghai；CSV 无偏移时按 Asia/Shanghai 解释，有偏移时先转为 Asia/Shanghai。 |
| 自然日期/交易日 | 两个字段分开保存；CSV 首轮要求显式 `trading_day`，不从自然日期静默推断。 |
| 夜盘 | 交易日由来源/交易日历提供；23:00 与次日 01:00 可属于同一个交易日。 |
| 休市与缺口 | 休市段不自动算缺口、不造价填充；已知交易时段内缺少中间 K 线才报告疑似缺口。 |
| 完成状态 | 只有 `is_complete=true` 才能进入可研究快照；未完成行导致严格导入不发布。实时完成状态未来必须由接入层依据供应商序列、完成标志和交易时段确认，不按电脑时钟猜测。 |
| 信号 | 只对已完成 K 线执行；预热不进入正式统计；重复确认同一时间边界不得重复执行。 |
| 回放查看 | 只读已保存结果；需要重算时必须创建新的运行记录，不能改写旧结果。 |
| 交易意图 | 完成 K 线生成意图，下一根有效 K 线开盘仅尝试一次；信号、意图、成交分开。 |
| 止盈止损 | 只在完成 K 线上判断，下一根开盘处理；不实现盘中双触发先后或限价排队。 |
| 通知 | 信号账本是权威记录；先持久化信号/通知尝试状态再调用系统通知；采用少重复策略，不承诺恰好一次。断线补缺只记录，不弹窗/播声音。 |
| Python 边界 | 用户策略是可信本地 Python，不宣称安全沙箱；G1 不执行策略。 |

## 3. 指标冻结

- SMA(N)：最近 N 根完成 K 线收盘价的算术平均；不足 N 根为未就绪，不补零。
- EMA(N)：第 N 根以这 N 根收盘均值初始化；之后 `EMA = 前 EMA + 2/(N+1) × (本收盘 - 前 EMA)`。
- ATR(N)：首根 TR 为高低差；其后取高低差、`|高-前收|`、`|低-前收|` 的最大值；初始 ATR 为 N 根 TR 均值，之后使用 Wilder 平滑：`(前 ATR×(N-1)+本 TR)/N`。
- 均线交叉：上穿要求上一根快线不高于慢线且本根快线高于慢线；下穿方向相反。
- 指标跨交易日连续计算；只有新实例或显式重新预热才重置。

## 4. G0 验收项

| 编号 | 结果 | 证据/说明 |
|---|---|---|
| G0-ENV-01～04 | 通过实施记录 | 本报告环境事实与 `current-status.md`；负责人需复核 |
| G0-SEM-01～06 | 通过实施记录 | 本报告语义/指标冻结，与方案及交互文档一致 |
| G0-FIX-01～05 | 通过实施记录 | `fixtures/g0-indicator-expected.md`，包含 F01～F05 及补充边界预期 |

## 5. 仍未验证/不阻断 G1

- TqSdk 账号、历史权限、实时完成事件、速率和 3～5 个品种覆盖：未验证，G1 不依赖账号。
- ScottPlot/WPF 在当前桌面上的人工交互、缩放、十字光标视觉效果：编码后需人工走查，不能由构建结果代替。
- 无开发工具环境、安装包、两交易日稳定性和真实通知：留待后续关卡。

## 6. G1 放行结论

方案已冻结，开发授权来自负责人监督记录的最新授权；不再受方案阶段“只读/不写代码”历史文字限制。G0 记录完成，进入 G1。本报告不修改 `docs/04-负责人监督记录.md`。
