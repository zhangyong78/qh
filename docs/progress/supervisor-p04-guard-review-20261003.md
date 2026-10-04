# 守护身份收口审核及下一性能候选

2026-10-03，负责人实际审核。

## 收口通过（有限检查范围）

源码 Get-ProcessIdentity 保存原生StartTime UTC ticks，CIM仅诊断；Get-IdentityState用同源ticks、unknown/different/not_running分开；Stop-OwnedTree只对match执行终止并按累计映射记录。保留流式stdout/stderr及有限drain。工具SHA `122A02646A94EC8FEEFA41FEC062673B1E969361F92981328B4E7E29CDD208BC`。

负责人调用实际身份函数：PID1180两秒辅助运行中match、自然结束not_running；CIM与原生仍差2 ticks但不会误判。另在当前自有命令PID7404验证准确身份match、创建ticks加1的错误身份different，无杀进程。证据 `artifacts/supervisor-p04-guard-review-20261003`。初次负责人尝试OrderedDictionary.Clone失败，原negative结果unknown无效，已另存corrected结果，不改产品、不把探针错误计作工具故障。

开发自然/超时原始证据已查验：PID29556自然2.67秒/exit0/约27.4MB，无限制；PID6276全局1秒实际1.25秒停止，stop事件identity_state=match/action=stopped，随后not_running，约27.3MB，stdout/stderr drain完成，未遗留已记录自有进程。只说明这些有限场景，未知身份/长时子树采样/极端I/O的限制仍需准确呈现，不宣称任意环境保证。工具core_summary/worker_summary是进程摘要，不是二进制SHA；后续A/B必须单独记录实际Core/Worker哈希。

资源大文件每根/累计及run进度口径已纠正，不再追查假想输出重复，不将此前49GB来源说成已定位。最终buf64包保持原SHA，产品源码未因本关变化；P04仍已测失败，G4独立技术基线保留、完整离线与真实现场/G5未放行。

## 下一唯一性能候选

1. 先在当前CLI实际运行时确认Process.StandardInput.AutoFlush默认值。负责人.NET10.0.11探针为true，不代替当前CLI10.0.12验证。
2. 仅改TradeReplayRunner启动Worker后的StandardInput.AutoFlush=false，保留SendAsync每条完整JSON行之后显式FlushAsync(token)。目的为检查默认自动flush和显式flush重复；必须保持每条完整请求已发送才等响应，不能缓存多根/省略flush/改变协议/隔离/账户上下文/deadline。不得顺带恢复已失败token候选或改signal、G4、界面。
3. 独立候选目录冻结A/B实际Core/CLI/Worker哈希，不在测试过程中覆盖版本。相同快照、参数、子区间，串行1k检查和10k A/B/A/B；记录完整墙钟、Worker/协议分项、监控参数及启动差异。若采样不足或负改善，不扩大测量。
4. 如果有可复现收益，验证有限F01多手费用/同根多意图/止损、取消partial/初始化期限、2条0.8秒慢回复整根deadline/finish与正常子树退出。每个回复夹具仅3根、至多10条256字，20秒/1GB/1MB限额。不得以压力输出来测试deadline。
5. 只有候选同环境10k Worker改善至少约25%且适用回归全过，才允许一次同固定百万/零动作策略完整验证，原始外层计时/资源/累计PID身份落盘、90秒/4GB/低频采样；正常最终条数必须1000000，不改30秒目标。否则回退、保留buf64包，不重跑百万，交付分项预算及下一最小建议等待审核。
6. 若旧buf64百万原始外层计时/资源文件不存在，明确“未落盘/未找到”，不补造历史，不把Worker40278ms当外层42460.8ms。只在可保留新候选确实过小回归时重建严格包；无收益不重新打包。current-status本次任务/最终包/等待条件同步。G5与账号现场不推进。
