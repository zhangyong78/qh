# G4c 独立审核

时间：2026-10-03 03:43，Asia/Shanghai。

结论：上一轮进程入口和取消登记有修复，包完整性及既有基础夹具通过；G4独立成果仍需返修，暂不请求真实账号或放行G5。

## 实际通过

cursor53取得完成快照54。实读bridge、Gateway、LiveSession、Publisher、Coordinator、WPF及报告。启动bridge参数已加，连接10秒/history2分钟父级截止、stderr排空、取消整树回收、WPF probe/history/live CTS与共享窗口退出登记、同账号凭据回退、实际版本与专业权限null均已补入源码。通用打包清单也已排除自身。这些是源码核对；真实账号及实际长时恢复未验证。

直接逐项读取最终ZIP：839项零差异、无自引用；150180346字节，SHA256 `9695C0D09E3E6F63D4D3CB4C32774EC03D3F834BDBC60083773C00B14818A87C`。实际运行最终包基础fixture到隔离数据，exit0/all_passed=true。证据均在 `artifacts/supervisor-g4c-review-20261003/`。

## 已复现的最小修复任务

1. **专业CSV解析。** 使用官方DataDownloader列结构的两根合法CSV替身实际调用最终包的 `professional_history`，得到 Empty；代码只读取裸open/high/low/close/volume，但真实列是合约前缀。负责人已读取[官方实现](https://raw.githubusercontent.com/shinnytech/tqsdk-python/master/tqsdk/tools/downloader.py)，`_get_headers`使用datetime/datetime_nano及symbol.column。按实际目标SDK列规则解析，保留时间/持仓/未知值与请求范围校验；必要集成夹具必须经过真实bridge函数，不能只改FixtureGateway标签。
2. **交易日与完成状态。** `bar_from_values`直接用timestamp.date写trading_day，并忽略输入供应商交易日。替身传2026-10-09，实际归一化为2026-10-01。不能用自然日期充当已验证期货交易日；用实际供应商或明确经验证映射，缺可信信息就标未知并阻断严格发布/回放。标准历史用timestamp+duration<=电脑now确认完成；未被新根/同步确认的末根替身仍is_complete=true。末根必须遵守供应商确认/同步规则，未知保留等待，不能靠墙上时钟；professional末根也需一致规则。证据 `bridge-audit.json`。
3. **请求边界/部分覆盖。** 独立审核程序引用最终包Core，请求TEST.REQUESTED、返回TEST.WRONG，实际成功发布snapshot-e02240a2c75f228f。Publisher没有核对请求合约、周期、范围或完成行；ParseHistory/ToCsv把全部输入强制Complete=true。校验这些边界后才发布，未完成或错误流必须拒绝，完整覆盖由实际有效数据/时段事实决定，不只信complete字段。缺覆盖可作为明确部分候选处理但不能称请求完整成功。WPF现强制professional=true，普通末端8000路径无本机选择入口，补显式普通/专业选择与准确权限提示，不购买。
4. **实时失败/恢复状态。** 实际最终包Probe返回Unsupported（仍无SDK），同包LiveSession遇到该错误却0bar/无异常静默完成，原始error_kind/message被yield break吞掉，UI也没有最终状态。缺bridge/认证/权限/网络/超时须在本机准确显示并落账；初始握手有截止。当前bridge把所有bar `is_recovery=False`，发生错误退出，无暂停/恢复补缺流程；Coordinator无条件RecoverySilent=true，不能证明实际恢复。实现既定断线暂停→恢复静默补缺→正常新完成bar的状态消费，无法补齐保持暂停，不伪造恢复完成。上次账本里Recovering字段的夹具不等于这条真实协议链。
5. **部署及共享研究上下文。** 当前包实际仍没有TqSdk，报告要求用户提供账号依赖，但SDK部署不是账号操作。先补项目隔离SDK依赖/可复现部署脚本和明确运行时选择，验证同一发布入口能加载版本及依赖，无账号不得联网登录。WPF历史发布后直接LoadSnapshot不走现有_contextGate，可能改变正在回放/只读查看的上下文；采用现有闸门或发布后明确延迟加载，保留旧run不变。持久去重账本损坏目前catch后清空，不能静默重做历史bar；保留损坏证据并暂停/可恢复状态。

证据：`bridge-audit.json`（SDK格式/交易日/末根替身），`gateway-audit.json`（真实最终包Gateway/Live错误传播与错误合约发布），`zip-manifest-check.json`、`fixture-report.json`。独立C#审核程序首次缺SQLite provider初始化，仅是审核环境错误；补齐最终包batteries/native后再跑才得到上述结果，不能归为产品失败。

按上述真实根因最小修改，不重构无关模块，不重复百万/三轮100k。必要测试覆盖真实Gateway/bridge/Live状态路由，而不是仅用预先返回成功的FixtureGateway。提交新包与准确报告后负责人复核；真实权限/交易日现场、两交易日、A05/P04及新机器仍开放。没有完成可自行处理的部署/状态分支前不向用户催账号。
