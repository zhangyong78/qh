# G4 final4 负责人独立审核

2026-10-03，Asia/Shanghai。结论：基础历史发布与 CLI 依赖准备修复通过其明确范围；恢复消费、日历覆盖和连接准备仍需最小返修。未登录账号、无真实行情、无通知或交易，不放行G5。

## 已验证

独立 ZIP 逐项10011项零差异、清单排除自身；268838975 bytes，SHA256 `1391D948250F8ECE1A1E92BBF32961C5CC3F942E886DCA84F72365471C958279`。安全解压到负责人新目录。

实际包无账号 CLI 冷依赖准备成功，40886.1ms，3.8.7、exit0；独立包临时移走单个bridge文件后正确Success=false/Unsupported/exit1，并finally原位恢复。基础fixture exit0/all_passed。

对实际包bridge函数复验：后继行确认末根、显式字符串false、供应商trading_day保留和实际tq_dl中文权限分类通过。没有后继/明确关闭证据的末根继续保守未完成，不把它冒充通过。

直接引用最终包Core/Python/bridge、SDK格式替身的完整进程调用链，专业四根发布成功snapshot-b7ac31078337bacf；不足覆盖阻断、只有09:02一根却provider complete=true被拒绝，快照数保持1。审核程序复制包dll导致本审核apphost初次找不到框架，改用项目.NET10直接执行审核DLL后成功；这是审核环境修正，不是产品失败。

证据：`artifacts/supervisor-g4-final4-review-20261003/`，zip-manifest-check、runtime-probe、runtime-missing-bridge/exit、cli-exits、fixture-report、history-package-audit、bridge-boundary-audit、core-publisher-audit及可重复脚本。以下恢复序列是无账号可控SDK替身；WPF问题是源码静态路径核查，未称人工UI通过。

## 1. 完成bar的连续消费与WPF恢复消费

真实包bridge三条独立序列：

- 重启带last_complete_bar_end=09:01，已就绪frame[09:01,09:02,09:03]：recovering初始仍false，直接connected，仅发送09:02普通bar；应补09:01未发送。
- 正常wait_update不抛异常，一次从frame[09:00,09:01]跳到[09:01,09:02,09:03,09:04]：仅输出09:00、09:03，09:01/09:02丢失且没有暂停。SDK内部自动恢复/多更新合并不能依赖异常重建分支才补缺。
- 异常重建后恢复frame[09:01,09:03,09:04]：只检查首根是否晚于边界，内部09:02缺口仍被放过，发09:01/09:03恢复bar并宣告recovered。

最小修复：按同一流的已持久确认边界消费所有新增确认完成bar，覆盖首次恢复/正常多根更新/异常重建，逐段核对完整性。数据不足或内部缺口未补齐保持明确暂停/未完整；旧恢复bar静默，真正新增完成bar才回到普通输入，不扩展提醒/策略合同。

另有WPF回归：TryParseEvent对恢复bar返回Kind=Recovered、RecoverySilent=true且Bar非空；MainWindow的Recovered分支先if(RecoverySilent)只SaveState，AcceptAsync只在后续else里，恢复bar因此从未进入持久完成bar账本。应将状态事件与有bar的消费正确衔接。验证必须走实际WPF事件消费/协调器入口，不能以直接调用coordinator.Accept的基础fixture替代。

## 2. 按适用日历核对覆盖，保留有效边界

bridge与Core现在把仅按钟点的通用样例时段表作为所有日期/合约的期望全集；并要求首根<=原始start。独立复现：

- 请求10:15～10:32，休市后仅10:30/10:31，全部有效分钟已提供，bridge与Core仍判不完整（首根晚于休市中的start）。应以第一个/最后一个有效边界判断。
- 明确供应商夹具为工作日日线，请求周五09:00～下周二09:00，提供周五和周一两根完整日线；bridge/Core仍拒绝，因为把周六/周日09:00也当必需数据。

按docs/01冻结口径，对日线和分钟分别使用有来源/有效范围的交易日与具体合约时段；不能把样例Clock sessions称真实交易日历，不把休市/周末/节假日制造成缺口。无法核实时明确未验证/受限原因，不伪称完整或猜测；仍需保留连续有效分钟缺失的阻断能力。补休市起止、跨周末/节假日、不同供应商时段、日线与真实有效缺口的无账号元数据夹具，以及bridge→gateway→publisher成功/阻断证据。不引入新页面/服务或改撮合合同。

同一历史完成修复引入每根 `timestamps[index+1:]` 加列表推导并any扫描，长历史为O(N²)；在本函数最小修改为有界/线性后继证据查找，别引入大框架或重跑百万回测。对较长历史做针对性检查即可，保留无后继未完成规则。

## 3. 连接/live入口准备期限仍未衔接

CLI RuntimeCheck有60/90秒有界准备已通过，但WPF测试连接仍直接ProbeAsync总10秒、live首行握手仍10秒；没有单独准备状态或准备步骤。独立无网络SDK替身仅import延迟12秒，真实包Probe在10082ms报Timeout，尚未到模拟API初始化。此路径会把依赖准备截断；本包实际冷导入已测40秒。

衔接本机连接/live入口的有界可取消依赖准备与真正网络/序列握手，界面和诊断准确表示阶段；不让用户依靠先跑CLI碰巧预热才成功，不简单无限延长网络等待。无账号慢导入/依赖失败/取消/关闭与后续真实连接阶段的协议夹具验证，不触发实际网络登录。

完成以上最小修复后更新报告、状态及最终包；不改04/负责人证据，不进入G5、不购买、不催账号、不重跑透明PNG或大型性能。A05/P04/现场/真实权限与两完整交易日仍开放。
