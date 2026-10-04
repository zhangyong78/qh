# G4 final5 负责人独立审核

2026-10-03，Asia/Shanghai。结论：上一轮恢复逐根消费/WPF入账与分阶段准备修复通过其明确范围；日历证据判断与SDK内部自动重连仍阻断G4独立候选。不登录账号、不发通知、不下单、不放行G5。

## 包与已通过范围

ZIP独立直读10011项零差异、排除清单自身；268846886 bytes，SHA256 `B00CA2452B030B287C34FC9C5B3E9BF947CB7692D3EA2D677D3A7400E8826CB1`。安全解压到新审核目录。9172项shared SDK文件哈希与负责人已测final4完全一致；app/cli Core及bridge各自一致，因此保留此前3.8.7实际导入证据，不重复冷SDK导入。

实际包基础fixture exit0/all_passed。对最终包bridge独立复现：重启09:01边界补09:01/02；正常多根更新09:01/02/03不再漏根；有内部09:02缺口的恢复不再recovered。午休起点10:15～10:32、10:30/31两有效根现在可以通过样例时段校验。后继证据已改反向一遍扫描，移除原O(N²)后继列表。

引用最终包Core/Python/bridge：普通四根进程发布成功；WPF同一共享AcceptEvent入口接受1个恢复bar、账本Recovering/RecoverySilent=true/lastEnd09:02，重复接受0。最终Core共享准备流程面对12秒无网络慢bridge，先dependency_prepare再network_handshake:surrogate，12169ms到Authentication模拟握手拒绝；100ms取消正确Cancelled。WPF源码实际调用这些共享入口，未声称人工UI通过。

证据：`artifacts/supervisor-g4-final5-review-20261003/`：ZIP/SDK等价、fixture、bridge-boundary、calendar-notify、core-publisher-consumer-prepare JSON及可重复审核程序。

## 阻断1：日历文字不能替代期望日期与时段

bridge的known_calendar和Core的HasCalendarEvidence仅检查描述含calendar/weekday/交易日/fixture。三天请求仅提供最后一天一根完整日线：Note="calendar unavailable"、"weekday calendar not verified"、"fixture with missing days"均被bridge判complete=true。最终Core同样接受calendar unavailable；通过真实MarketHistoryPublisher发布为snapshot-0b43944571cdbe65，独立审核库由正常快照1变为2。不是仅helper标签失败。

相同缺失输入、给出结构化ProviderTradingDays三天集合后，bridge/Core正确拒绝。修复应使用可核对的结构化期望集合及来源/适用范围；不能读说明文本关键词来证明覆盖，也不能用实际返回日集合反推缺失日不存在。未知、未验证、fixture三个字均不应放行。明确fixture需真实完整的预期元数据；保留正常四根发布与已通过的午休边界。

分钟仍只有通用钟点表，没有具体合约/日期的结构化时段输入；SDK真实日线请求的日历来源也尚未落地。按冻结方案接入已有/供应商可核实的元数据，无法核实明确受限/未验证，不把样例时段当真实全市场日历。不要增加新页面/付费服务/泛化框架。

另有新截断：coverage_payload分钟expected只枚举前三天。独立10天请求，提供前三天全部样例有效分钟加最终末根（1668根），中间数天完全缺失，bridge仍complete=true。不能到循环上限后宣布完整；应完整检查请求有效范围，或明确范围受限。上下游同一日历/首尾/缺口语义。针对性长范围与缺失日/休市/节假日反例即可，不重跑百万回测。

## 阻断2：实际SDK重连通知被忽略，恢复旧bar仍普通输入

包内3.8.7 connect.py明确向notify发2019112911断开与2019112902恢复；内部TqReconnect继续缓存/重建数据，api.py的_notify_watcher消费_data.notify并打印。普通断线并非必然对调用者抛异常。

独立SDK格式替身沿同一API：第一次wait_update只添加行情连接断开通知、不改变最后bar时间；下一次添加恢复通知并提供09:01/02/03旧完成bar。真实bridge没有paused事件，三根全部is_recovery=false。已修复“不漏根”不能代替“恢复历史静默”，现代码仍只靠异常或持久重启边界启动recovering。

在集中SDK适配路径按目标版本实际连接通知识别行情断开/恢复、一次消费通知并绑定相关行情通道，不受无关连接通知影响；正确衔接现有暂停/就绪/逐根静默补缺/确认完整后恢复新bar。诊断只需脱敏状态，不打印账号/原始敏感URL或异常。保留异常重建和重启已通过分支，不扩展通知功能。

最小验证：不抛异常的断开→只通知无bar→恢复通知+多根补缺→新根；同批通知、重复通知、无关连接；缺口受限不恢复。走真实bridge→Core事件→共享消费者账本，不仅模拟状态标签。

修复以上两组、同步报告真实口径并重打最终包/清单后提交；不改04/负责人证据、不催账号、不进入G5。A05/P04/真实权限/现场与两完整交易日仍开放。
