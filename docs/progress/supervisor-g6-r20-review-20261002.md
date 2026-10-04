# G6 r20实际复核：功能收口通过项与剩余证据修复

时间：2026-10-02 22:52，Asia/Shanghai。仍为离线验收，不进入实时。以下只要求最小剩余收口，不重做已通过业务，不继续无边界优化P04。

## 本轮独立结果

负责人新解压 `artifacts/负责人 r20 独立复核-20261002`，ZIP SHA256 05FE3E8555124A9AB136C3952E196207C476AFEBC953C3ACB9009BDB8220D04E，837文件摘要/字节全部通过。受限PATH、无效QH_PYTHON_PATH、独立数据根包内自检通过：P02五次138.048/135.7399/148.5163/133.1708/113.4036ms，P03运行7422.88ms/最大间隔255.72ms/运行中缩放成立，复制后实际字段、新run及旧run不变、入口碰撞、F01/零交易/末根/导出判定通过；取消601.17ms。证据integrity.json/self-check.json/self-check-process.json。

包内trade与signal真实on_bar卡住均failed/strategy_timeout，含bar_start/bar_end/strategy/phase/budget；超时绝对截止修正通过基本验证。包内独立专用空头/反向夹具也完成，空头毛20/费5/净15，反向毛40/费10/净30，见 numeric-package-corrected-summary.json。初次负责人用了不存在的timeout_signal.py以及误把F01数据用于空头/反向专用预期，已经纠正；初始记录和operator-note.txt保留，不作为产品故障。

负责人本轮还从固定百万CSV独立导入新数据根，再运行实际r20包，排除首次导入且不使用高频采样：1000000输入/1000000处理、completed/exit0，墙钟128443.1954ms，run trd-20261002144736102-cef68cd0f；Worker125796ms、协议115655.58ms、同步策略706.38ms。证据million-import.txt/million-timing.json/million-replay.txt/million-data/trade-runs。P04仍未达30秒，报告应引用该最终版本独立记录，r16的127365ms只作历史。预算拆分r19 10k已有真实metrics，不能外推成百万达标；同步阻塞方案未实施实验，只能写“评估后未合并”，不能写实测证明无收益。

## 1. 修正P01计时起点并完成五次真实外层测量

App.StartupClock是无显式static constructor的静态字段，首次实际读取在Loaded回调；全部loaded_ms约0.02ms，说明没有包括MainWindow构建和CLR/启动载入。外层process_start_ms只是Process.Start返回，wall_ms包括整个15秒业务自检，二者都不是外层到首页就绪。ContentRendered 5～6ms是Loaded附近起算的阶段间隔，不能宣称完整适用启动通过。

最小修改：明确在OnStartup开始初始化应用阶段时钟；在现有self-check首次Dispatcher就绪入口、业务自检开始之前，立即保存单独就绪标记文件（如同目录startup-ready.json）。外层Stopwatch从Process.Start之前开始，等待本次唯一就绪标记，记录startup_to_ready_ms，完成后另记full_self_check_wall_ms。五次最终包/独立数据根/受限PATH，保存CSV或JSON、全部exit与就绪含义，适用目标5秒；不清系统缓存，不冒充人工或全新机器。原loaded相对阶段记录保留但修正描述。标记仅服务已有内部测试，不增加产品页面/框架。

## 2. P08与退出证据落盘，补齐最后可自动工作

实际 `artifacts/G6 P08 r18-20261002` 只有localappdata及run，没有samples/summary/script；负责人为核对此异常读了单个开发回合的相关命令输出，确认600.7秒空闲采样确实执行过，但只在聊天输出。采样的“children”按全机QhApp/Qh.Cli名称筛选，重复算入本次app且没有Python；CLI三轮发生在采样之前，因此没有研究期间父子峰值/趋势证据。这些限制需实写，不能把目录当作不存在的采样文件证据。

请保存可复跑的最小采样脚本、最终包哈希/时间、每条低频samples.jsonl/CSV、summary、任务结果与本次受管理PID清理证据。已有原始空闲输出可保存为“r18聊天输出转存、来源明确”，无需为了保存旧记录再跑十分钟；但最终包要补实际研究期间父/子资源和循环后基线。只监视自己启动的PID及其子树，包含Python，不采样/终止无关应用；周期保持合理低频，不与P04墙钟重叠。研究样本使用既有合法1万根或既有WPF自检任务即可，不能只用3根已结束任务证明研究负载。

正常空闲关闭原结果可记录为有限内部证据；运行中退出停止/partial/PID清理仍没测试。沿既有关闭收尾逻辑补内部受控测试，保留真实用户确认行为，不通过native自动点击冒充人工验收。不引入新框架；若内部调用需要很小的共用收尾函数，限于既有逻辑。报告A16/A12/P08按实际证据区分。

把最终A/P矩阵中的A03重复导入、A06加载失败、A09完整手算明确引用已有固定夹具的最终包证据或按已有CLI短测补齐；已通过历史源码项不重做无关功能，不以on_bar超时替代策略加载失败。最终包实际重复导入/旧run不变应有专门记录。核心函数未修改的历史源码证据继续保留并标明版本。

完成这些有限收口后稳定打包/说明更新、当前包基本自检和证据位置检查，停待审；P04保留最终128.44秒实测、具体预算和最小后续建议，本轮不扩展引擎/协议或继续盲优化。负责人根据适用离线功能与性能限制决定后续交付范围；不要自行宣布完整成品或进入G4/G5，不请求用户提前操作，不编辑04。
