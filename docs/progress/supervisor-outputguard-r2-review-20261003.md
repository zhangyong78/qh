# 输出保护 r2 负责人复核（2026-10-03 13:46～13:53）

本关通过，仅指既有策略输出保护返修；不代表完整离线成品或 G4 现场/G5 通过。

## 实际包与源码

实际 ZIP：`artifacts/g6-offline-package-20261003-p04-outputguard-r2.zip`，SHA256 `6591857DB65A8DBDB82F1E4BB57B726BA4CAB1C385379818140D7A373CC641E7`，269877764 字节。负责人流式逐项核对 10015 项大小/哈希，缺失、未声明、清单自含和 g4-data 为零。独立提取至 `artifacts/supervisor-outputguard-r2-review-20261003/package`；开发实际有限运行的 Core/CLI/Worker 与该 ZIP 二进制哈希相同。

源码核验：worker.log 采用非致命 TryReserve，诊断保留 4MiB 后停止追加；正式结果存储与该诊断副本分离。stderr/describe/单行协议保持致命限制；Trade/Signal 的 ReadResponse/Send/预算检查沿用适用 barContext。分段 64KiB，移除逐条强制 Flush，段轮转和 Dispose 收尾写入。没有混入已失败的 token/AutoFlush 候选。

## 有限实际运行与原始证据

负责人仅新增两个串行有界运行，均 20秒/1GB私有内存/1MB外层输出守护，不跑百万或巨大压力。

- 固定 40000 根零动作：`trd-20261003054910334-9314c9011`，completed/40000/40000，exit0，equity.jsonl 实际40000行，0意图/0成交；worker.log 实际4194301字节、64段、最大段65536字节。外层8055.2ms、峰private514670592字节、drain完成；记录PID7932/9044/24952/29192均not_running。正常研究不再被诊断保留上限阻断。
- 3根 partial 真实 stderr 超量：`trd-20261003055131141-0b8aa8b44`，failed/output_limit/1根，1intent/1fill/1equity/2order记录保留；bar起止、策略、bar_request诊断齐全。外层758.7ms、exit1、drain完成，记录PID13888/14704均not_running。
- 开发七项最终包小回归直接读取真实 timing、run.json、JSONL：F01和失败后重跑 completed/3根/2intent/2fill；9个信号真实序号1～9；stdout、stderr、partial超量均failed/output_limit且barContext齐全；describe exit5/output_limit、不创建run。没有用总结中的布尔值代替原始结果。

负责人证据：`artifacts/supervisor-outputguard-r2-review-20261003/zip-manifest-check.json`、`normal40k-check.json`、`normal40k-timing.json`、`partial-timing.json`、`raw-evidence-check.json`。250ms快照可能漏掉短命子进程；只确认实际观测身份退出，不外推任意进程清理保证。开发称63段而真实40000运行清单是64个文件，交付说明应统一。

## 下一关：G6独立交付材料收口

P04 固定百万仍失败，A05真实可视/人工交互、全新无开发工具机器、实际账号权限及两完整交易日仍未通过，G5未实现/未放行。现包 README 仍写“百万性能目标仍需目标机复测”，应明确已测失败；现 G6 报告部分最新包/清单停在 r39，不能拿旧包840项当现包10015项证据。

开发新增“旧buf64百万外层文件已找到”引用 `artifacts/负责人 r20 独立复核-20261002/million-timing.json`，负责人实读此文件仅有 wall_ms128443.1954、exit0、package_sha05FE3E85…，是旧r20记录，不能支撑buf64的42460.8。buf64 Worker40278ms/completed百万足以确认P04失败；buf64原始外层/资源若未找到，明确缺失，不补造、不重跑取代历史。

原范围下一最小任务仅整理真实可用离线候选的操作/验收/限制与完整源码交付材料，保留本包二进制不重建；以已存在证据收口，不重复大规模验证、不推进G5、不催账号。待材料交付后再逐项复核，监督继续。
