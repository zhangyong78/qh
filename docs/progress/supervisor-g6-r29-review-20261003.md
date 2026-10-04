# r29 负责人独立审核

时间：2026-10-03 01:13～01:22，Asia/Shanghai。

结论：运行中共享取消关卡通过，保留此结论；包完整性通过。新发现的空白内部渲染证据尚需最小修复，A05 不能据此宣称可视验证通过，因此暂不推进 G4。

## 已通过

从 ZIP 独立解压至 `artifacts/负责人 r29 独立复核-20261003/`，逐项校验837文件尺寸/哈希零差异。ZIP 150104057字节，SHA256 `008806A2BD90CF961959F3B9C08A2022003AB0B629DA6744861EBEE3CFCD7EA3`。

实际运行包内 WPF，受限 PATH、无效 QH_PYTHON_PATH、隐藏窗口、工作区隔离数据。exit0，内部断言通过。源代码已使用仅自检的第二根 stderr 阻塞握手，Stop前确认实际 Task 未完成、run=running、匹配 Worker 存活；严格 cancelled/error_kind=cancelled、1fill/fee2/equity9998 等条件纳入总passed，不再使用历史PID兜底。

负责人独立 run=`trd-20261002171548852-d8bd864c8`，worker PID34284，stop请求01:15:49.0403469，完成01:15:49.5675934，run结束01:15:49.5601184（均+08）。drain567.1893ms，partial cancelled，实际成交1笔 fee2，最终权益9998，Worker已消失、无PID复用、对象清理、再跑F01 completed。原始stderr证实第二根上下文已有long1持仓、entry103及手续费2。见 `summary.json`、`owner-assertions.json`、`running-close-evidence/`。

P02五次107.8388～153.8846ms，P03最大Dispatcher间隔247.4563ms，普通UI取消563.8819ms。Core、CLI、两套策略Worker与r22已测版本哈希一致，见 `backtest-binary-comparison.json`；既有矩阵和P04证据不重跑。P04仍128443.1954ms未达30000ms。

## 空白渲染证据

负责人实际查看独立截图及开发截图，均为空白。用包内Python标准库只读解析PNG：两图均1280x820、8bit RGBA，解压后的4199220字节全为零（含逐行filter与所有像素），即完全透明。客观记录 `png-pixel-audit.json`。此结果不能证明原生窗口也空白；当前只确认内部截图路径无效。

`SaveSelfCheckImage` 调用 `RenderTargetBitmap.Render(this)` 对Window捕获，未验证图像实际内容。`rendered_result` 只读Canvas子项/表格行/文本，能证明对象绑定，不能证明可见绘制；之前报告中“内部PNG有K线/权益/结果”的描述现予撤回，不能继续沿用。

最小任务：仅修内部自检渲染路径，按当前WPF实际内容根/控件布局渲染，不更改业务UI或增加功能；保存能真实看到K线、权益与结果的内容截图，必要时分开捕获现有结果控件。将全透明/空白图检测和实际业务图像条件纳入自检passed，不能只测PNG文件存在或控件数量，也不能画假示例图替代当前结果。重新构建适用包并查看真实PNG、保留退出严格断言；不重跑百万/三轮100k/全矩阵。同步更正 G6 报告第1节遗留的 failed/PID23172 旧退出描述及A05声明。

下一步仅该渲染最小修复。运行中退出不再作为未通过项；人工鼠标/退出确认、新机器、真实权限/行情和两交易日保持未验证，G4只在适用离线技术基线放行后开展独立工作，不宣称完整成品。
