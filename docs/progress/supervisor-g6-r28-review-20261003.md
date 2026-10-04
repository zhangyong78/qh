# r28 负责人独立审核

审核时间：2026-10-03 00:43～00:52，Asia/Shanghai。

结论：包完整性通过；运行中关闭验收仍未通过。本轮定位的是自检误判，没有认定共享取消函数本身失效。继续最小修正该关卡，不重跑已验收的百万根、三轮十万根和完整数值矩阵。

## 实际证据

负责人从 ZIP 独立解压至 `artifacts/负责人 r28 独立复核-20261003/`。SHA256 为 `456B53653228F60148CD01198455AC5AFEE4D89B1E9B897CBCAF93759CC5861D`，150101990 字节，837 文件逐项尺寸/哈希零差异，见 `manifest-check.json`。

独立运行包内 WPF 自检：隐藏窗口、限制 PATH、故意无效的 QH_PYTHON_PATH，数据和输出均隔离在上述工作区目录。进程 exit=0、自报 passed=true；其余内部断言通过。运行中关闭分支却再次得到 `failed/strategy_timeout`，run=`trd-20261002165013305-06bdd3b2b`，worker PID=4224，保留 1 fill、fee=2、equity=9998，drain_ms=1.6873，随后 F01 completed。开发原始证据也呈现同一问题：run=`trd-20261002161413157-58d4b48d6`，约 2.38 秒后超时，drain_ms=3.1358。

原始 run/fills/worker.log/metrics 在独立目录的 `running-close-evidence/`；完整自检在 `self-check.json`、摘要在 `summary.json`。这些证明超时后的部分结果保存和再运行，不能证明运行中关闭取消。

## 根因及最小修复

`MainWindow.xaml.cs` 自检等待可读 worker.log 的首根 bar_done 和非空 fills.jsonl。`TradeReplayRunner` 的日志未每行刷新，`TradeRunStore.Append` 每 4096 条才刷新，短夹具通常直到 finally/CloseRun 才可读；此时第二根已达到 2.25 秒 deadline。自检还把“历史 PID + 已完成首根 + 文件非空”作为存活身份检查的替代，且通过条件只要求 status!=completed，不要求 stop 前任务仍活跃，也不要求 cancelled。因此已失败任务调用共享收尾可在约 2 毫秒返回并错误通过。

只修自检观察机制和断言，保留真实用户关闭确认。可让仅自检用的策略在第二根、父级已执行首笔成交后的上下文中，向已及时刷新且独立记录的 stderr 发出带 PID/阶段的标记；先核实 simulation 字段后选用已有上下文。无需改每根 journal/protocol 刷新策略，也不要延长生产 timeout 或增加性能开销。

共享 StopActiveOperationsAndWaitAsync 调用之前必须保存：时间、尚未结束的实际 WPF replay Task、run 状态 running、存活且匹配身份的 worker、第二根阻塞标记。任何一项观察失败就使本关卡失败，禁止历史事实替代当前存活检查。Stop 后要求 cancelled/ErrorKind=cancelled、固定 1 fill/fee=2/equity=9998、相同 worker 身份退出、完整操作对象清理、再次 F01 completed。记录 stop 前后时间及原始 run/fills/worker 日志并将严格断言计入总 passed；不要把自然 timeout 当取消。

P08 seen-process 独立身份清理核对代码已补上，源代码核对通过；现有 r22 有限窗口数据保留原版本来源，不宣称新脚本实测。P04=128443.1954ms 仍未达到 30000ms，人工点击/退出确认、新机器、真实账号及两完整交易日仍未验证。G4/G5 尚未放行，自动化继续。
