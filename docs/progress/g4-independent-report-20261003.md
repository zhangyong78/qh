# G4 独立实现与模拟夹具报告

日期：2026-10-03，Asia/Shanghai。范围：完成不依赖真实账号的本机入口、集中接入层、历史/实时真实调用路径、状态持久化和明确标记的模拟分支；真实账号、权限、覆盖和长时间恢复仍未宣称通过。

## 官方能力基线

- TqSdk 目标版本按官方文档页面 3.8.7 设计。
- 普通 `get_kline_serial` 每个 K 线序列最多获取最后 8000 根；本机能力报告固定记录 `kline_serial_max_rows=8000`。
- `DataDownloader` 是专业版历史下载能力；没有专业权限时不得把普通末端 8000 根窗口写成任意区间覆盖。
- 实时完成状态由供应商更新/序列和完成标志确认，接入层使用完成 K 线去重；不使用墙上时钟单独确认完成。

## 已实现

| 范围 | 实现 |
|---|---|
| 凭据 | WPF 账号/密码表单；密码框遮挡；可选使用 Windows DPAPI `CurrentUser` 保存；不进入命令行、策略进程、普通配置或明文日志 |
| 集中接入 | `Qh.Core.G4Market.TqSdkGateway` 统一管理 bridge、能力探测、历史请求和错误分类；策略只继续收到现有标准化回放输入 |
| 真实 bridge | `worker/qh_tqsdk_bridge.py` 通过包内项目隔离 Python 接入 `TqApi/TqAuth`；运行时探针读取实际安装值且不登录；标准历史走 `get_kline_serial` 末端 8000，专业历史走 `DataDownloader`；缺 SDK/权限/Unsupported 时返回明确错误，不使用合成数据冒充真实结果 |
| 历史发布 | WPF 显式选择 standard/professional、具体合约/周期/时间范围；通过 `calendar.json`/`QH_TQSDK_CALENDAR_PATH` 加载带来源、`valid_from/valid_to`、合约范围和实际自然日区间的结构化交易日/时段元数据；实际供应商 CSV 前缀字段解析、尾行未完成语义、符号/周期/范围/完成状态校验、供应商交易日与适用时段一致性、有效末边界、跨午夜归属、覆盖检查、取消、有限重试、质量检查和内容寻址快照发布均在发布前执行 |
| 实时状态 | WPF 真实实时会话入口；bridge 输出 Connected/Paused/Recovered/Failed 和完成 bar；恢复期只补未见的完成 bar 并静默，正常新 bar 恢复可见状态；完成 bar 按流持久化去重，`live-session.json` 与 `live-bars.json` 支持重启恢复；TqSdk 3.8.7 `rtn_data.notify` 仅接受 `conn_id=md`，按通知顺序处理，重复通知去重，零缺口可恢复到 normal，无法填补的缺口保持暂停；实时连续性使用显式日历时段，跨周末不会误报，未知时段不会放行；损坏账本会阻断而不自动清空 |
| 覆盖元数据 | 历史完整覆盖只接受结构化供应商交易日集合和适用实际区间；备注关键词不再构成证据，分钟期望集合覆盖整个请求范围，不使用三日上限，未知日历明确受限；供应商返回的交易日字段不被重写，和适用日历冲突时拒绝发布 |
| 本机入口 | CLI `g4-fixture-check [data-root] [report.json]`、`g4-gateway-check`；WPF 连接、历史、实时入口均有独立取消和窗口关闭收尾 |

最终包：`artifacts/g6-offline-package-20261003-g4-final9-r3b.zip`。包内含共享的项目隔离 `tqsdk-site`（目标版本 3.8.7），清单核对 10013 项，严格 ZIP 校验逐项 bytes/SHA 通过，清单排除自身，包内无 `g4-data` 运行产物。大小 269,722,916 bytes；SHA256：`19CDFDE0A7C6EDE5E3FC93F77F50E262369D391FF5C4E61F3291ED0C8F377C86`。包内运行时探针成功，且不登录账号。

## 模拟夹具结果

证据：`artifacts/g4-final9-r3-evidence-20261003/package-fixture-report.json`，最终发布包 CLI exit=0，`all_passed=true`。

已验证：认证失败、权限不足、普通末端 8000 路径标记、专业下载路径标记、实际前缀 CSV 解析、供应商交易日保留、18:00/周末有限 fallback、后继行确认末根、显式字符串 false、空结果、历史校验发布、请求/结果合约不一致拒绝、有效时段覆盖不足阻断、相同内容幂等、临时目录清理、一次有限重试、取消、完成 K 线去重、暂停/就绪/恢复事件、受限缺口保持暂停、恢复静默、重启账本和新 bar 恢复正常输入。

集成证据：`artifacts/g4-final9-r3-evidence-20261003/`。其中 `calendar-audit.json` 验证结构化交易日/实际时段、备注关键词阻断、午休边界、09:00–10:20 的 75 根有效末边界、周五日线、跨午夜夜盘归属、完整十日范围和分钟缺口；`notify-audit.json` 另验证已知日历最后一根消费、未确认尾根等待、未知新根暂停、跨周末后继和零缺口恢复；`bridge-gateway-publisher-audit.json` 验证有效期、实际 Gateway 到 Publisher 成功发布、长请求阻断、稀疏结果阻断和供应商交易日冲突阻断；`strict-package-verification.json` 验证压缩包逐项 bytes/SHA。

另外，发布包 `g4-gateway-check` 是不带账号的有界依赖准备探针，只验证包内 TqSdk 导入和专业下载模块可导入；失败返回非零退出码，不代表真实网络连接。夹具账号和数据均明确标记为 fixture，不代表真实供应商能力。

## 未验证与边界

- 真实账号/密码、真实权限、真实小区间历史覆盖和真实 `DataDownloader` 结果未验证；包内已随附项目隔离 TqSdk 运行时，但未包含账号，也未进行登录。权限分类和恢复证据均为 SDK 格式替身/无账号协议证据。
- 实时真实账号连接、真实供应商自动恢复和两完整交易日未执行；G5 通知、声音和下单链路未实现/未放行。
- 当前 WPF 实际像素捕获诊断仍失败；这不阻断本报告中的非 UI G4 夹具结论，也不改变 G6 A05 未通过状态。

## 现场步骤

1. 在目标机解压 final9-r3b 包，先运行 `cli\Qh.Cli.exe g4-gateway-check` 和临时目录中的 `g4-fixture-check`；运行时检查只代表依赖准备成功，不把运行数据写入便携包。
2. 需要真实连接时仅在 WPF G4 表单本机输入账号和密码；不把密码发送到聊天、命令行或策略文件。
3. 先点击“测试连接”，记录脱敏的实际版本/权限结果；再由用户明确选择具体合约和小区间历史任务，检查覆盖后再发布。
4. 只有真实覆盖、断线恢复和现场报告复核后，才评估 G4；不自动进入 G5。

官方参考：

- https://tqsdk-python.readthedocs.io/en/stable/version.html
- https://tqsdk-python.readthedocs.io/en/latest/usage/mddatas.html
- https://tqsdk-python.readthedocs.io/en/latest/reference/tqsdk.tools.download.html
- https://learn.microsoft.com/en-us/dotnet/standard/security/how-to-use-data-protection
