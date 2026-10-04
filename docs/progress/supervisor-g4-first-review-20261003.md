# G4 独立候选首轮审核

时间：2026-10-03 03:13，Asia/Shanghai。

结论：包清单与部分独立基础组件通过；G4 独立实施尚未完成，当前不能提供真实历史/实时可用入口，因此不请求用户账号、不进入G5。

## 实际通过与证据

先读04/current-status，cursor51取得完成快照52。直接读取ZIP逐文件核验：r39 SHA `7A81F068B0D56148B2B486C033990736D0448249315ED6358300FB82D1DC57AD`、150113546字节、840项零差异；G4候选SHA `2E2AF88306FAE3F37B2B4467C6A1E5B70929A86C2FB50735B9FBFF51C7DF440D`、150172631字节、851项零差异，两包清单均排除自身。GDI替代重绘/强制激活已撤除；真实WPF图像失败状态如实保留。通用package-offline脚本仍未排除遗留manifest，当前包手工修正不能代替复用脚本修正。

负责人实际运行最终包 `g4-fixture-check` 到独立 `artifacts/supervisor-g4-review-20261003/fixture-data`：exit0、all_passed=true。这些只验证FixtureTqSdkGateway与发布组件，不能证明TqSdkGateway真实进程/SDK代码已可用。DPAPI源码使用CurrentUser，密码通过stdin而非argv的设计方向正确。

独立审核程序引用最终包 `Qh.Core.dll`，直接调用实际 `TqSdkGateway.ProbeAsync`，使用明确假的占位凭据及3秒截止，无真实账号：得到Protocol/“TqSdk bridge没有返回结果”。文件 `gateway-result.json`、审核小程序 `GatewayCheck/`。不是登录失败；源码证实启动Python时未将 `_bridgePath` 加入 ArgumentList，JSON被送入Python解释器而非bridge。

## 修复及完成顺序

### 一、真实进程接入和本机任务生命周期

- 启动参数补实际bridge路径；用无SDK/明确假凭据、可控bridge测试来验证真实Gateway，而不是仅测FixtureGateway。包内真实运行时当前没有TqSdk，需补齐已冻结目标的项目隔离依赖或准确离线部署入口，让账号操作前具备可加载的SDK；不能让所有连接永远Unsupported。
- Bridge标准错误持续排空并脱敏，防止大量错误卡住；连接/响应有有界deadline，取消/超时整树终止并确认退出。真实Python异常类型区分认证/网络/权限，不把全部异常归为Network；诊断不要回显stdin请求。
- WPF测试连接目前传CancellationToken.None，无取消按钮、任务登记或关闭等待。接入现有共享关闭收尾，有取消按钮/CTS与任务状态，退出不遗留接入进程。修改账号后不能使用别的已保存账号的密码（当前空密码不比较账号即回退）；只匹配同一账号才使用保存凭据。
- `target_version` 必须读实际安装版本，`can_data_downloader` 不能用`tqsdk.tools`模块存在代替专业权限。未测权限使用明确unknown/未验证状态；真实连接成功不代表历史覆盖或专业许可成功。

### 二、完成已有G4历史与实时范围，而非只交付占位类型

- `qh_tqsdk_bridge.py` history分支当前对所有装有SDK且有凭据请求固定Permission，没有调用get_kline_serial或DataDownloader。按冻结方案实现普通末端8000窗口的实际过滤/覆盖和专业下载实际调用及权限失败；提供本机合约/周期/范围、检查范围、下载/取消/重试、覆盖报告/打开图表入口。缺账号只阻止实测，不阻止编写真实路径及替身SDK集成测试。
- 集中会话现在每次启动/关闭TqApi且没有live请求、订阅、完成判定、断线恢复消费；LiveBarNormalizer/LiveSessionLedger只被夹具调用。按G4-TQ-04～06接通管理会话和既定本机入口：具体合约订阅上限、标准化完成bar、新根/供应商同步确认、等待/暂停/恢复、到期状态、静默补缺、策略输入和持久恢复去重。无需新增框架或交易功能。
- 当前normalizer仅内存HashSet，重启无恢复关联；ledger仅单条状态不能证明合约/周期去重恢复。按已有方案持久化每个实际订阅流的边界/去重信息，使重启相同完成bar不再次执行。
- `complete_bar_dedup_and_recovery_silent`夹具把恢复bar设成已见bar的副本，仅断言Count1，未覆盖新恢复bar；`restart_ledger`只比较载入字段，未测试恢复后的重复处理。增加真正穿过会话、网关、发布、重启消费路径的必要验证，明确模拟身份，不能把任意true字段当权限/静默执行证据。
- 历史覆盖不得只相信供应商/夹具`complete=true`，要核对所请求的合约/周期/区间、合法已完成行和实际质量；缺覆盖不能显示完整成功。取消覆盖下载/重试等待/校验发布阶段；失败或取消后的临时数据不得正式查询。

### 三、提交范围

先修一，实际进程测试及关闭取消通过后继续二；完成全部独立工作再统一交付本机可执行包/操作步骤和报告。报告如实区分骨架、已实现路径、夹具和真实未验项。不变全局Python、不购买、不要求密码到聊天，不向用户提前催账号，不进入G5真实通知或下单。既有离线包不覆盖，必要回归仅受影响的离线闭环/退出，不重跑百万和三轮100k。A05现场/P04/新机器及两交易日继续开放。

官方依据已由负责人读取：[DataDownloader说明](https://tqsdk-python.readthedocs.io/en/latest/reference/tqsdk.tools.download.html) 明确它属于专业功能，通过TqApi实际运行下载任务并等待完成；模块可导入与账号权限是不同事实。[行情及历史说明](https://tqsdk-python.readthedocs.io/en/latest/usage/mddatas.html) 给出行情与合约规范。目标文档版本3.8.7不是检测到的安装版本；真实账号权限仍待本机验证。
