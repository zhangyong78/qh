# G6源码材料r4负责人复核（2026-10-03 14:50～14:56）

本材料小关通过；运行包/源码可供本机人工验收，完整离线及实时成品仍未通过。

实际源码ZIP `artifacts/g6-source-archive-20261003-r4.zip`，SHA256 `A2B6495E376257A114553584C2A45BCE74317B5E32F86016C4AC9C095CBE72B4`，413258字节、150项；负责人直接读ZIP逐项大小/哈希核验，missing/undeclared/缓存/runtime载荷均零。必要src/worker/strategies/fixtures齐全；除生成的README-source/选择表/主文档位置说明外，147项与当前工作区一致，current-status字节完全一致。63个Markdown的8个相对链接无失效；负责人按出现次数查66个docs/progress引用无缺失，开发43为另一计数口径，不作为文件缺失。主交付说明采用明确外部位置占位，运行包和外部主说明须与源码共同保留，避免源码自哈希循环。

根README/源码说明已改为当前功能、明确不附SDK/runtime及.NET10SDK和打包脚本tools/dotnet前置。CsvImporter.TryParseBar实际仍有无偏移兼容解释分支，说明推荐显式时区、兼容路径并非新引入；不据先前总结将这一句当本次源码回归。性能报告第94行已区分r20的128443.1954与buf64外层42460.8报告值/原始Worker40278分项，外层/独立资源缺失明确，不补造或重跑。

负责人复算运行r3 ZIP仍为SHA `40FC493297E0A63D890AEE40C7AA0B3FDB2304758EB7D281CE5C99D1975EF814`、269878197字节，未变；继承前次严格10015项清单和r2完整载荷一致证据，不重复编译/业务/性能。实际本机启动文件 `artifacts/g6-offline-package-20261003-p04-outputguard-r3/app/QhApp.exe` 存在，162304字节。

证据 `artifacts/supervisor-source-r4-review-20261003/source-audit.json`、`runtime-unchanged.json`。负责人初次读取假定清单有外层目录导致StopIteration，已按实际根层位置修正，只是审核脚本定位错误，不是源码ZIP缺文件。

下一关为用户本机实际可见窗口/离线导入/信号定位/小回测的人工验收；现有工具不能代替原生人工操作证据，旧透明自检PNG不代表正常窗口必为空白。首次通知本机入口和三根小夹具，收到实际现象再定点修复。P04固定百万已测失败、目标30秒保留；新机器、真实权限/两日验证未过，G5不放行。开发当前无独立代码修改任务，等待用户反馈，不反复测试或新增功能。监督保持ACTIVE，通知一次后无变化安静。
