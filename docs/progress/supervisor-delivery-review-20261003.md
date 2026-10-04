# G6交付材料负责人审核（2026-10-03 14:14～14:18）

运行包r3核验通过；源码归档/交付文档收口暂不通过，须最小修复。不是完整成品验收。

## 已核验

实际r3 ZIP SHA256 `40FC493297E0A63D890AEE40C7AA0B3FDB2304758EB7D281CE5C99D1975EF814`、269878197字节，10015项逐项大小/哈希与清单一致，缺失/未声明/自含/g4-data均零。r2与r3全部载荷对比只变更README-offline.md；程序/Worker/SDK/运行时/夹具均未变，沿用r2既有实际测试，不重复执行业务或性能。

实际源码ZIP SHA256 `15785EB68FE593477C4C393FA994953AE70F9C8A115EFDA515711B41D2FBB415`、227866字节、99项。清单逐项哈希/大小、missing/undeclared均零；src/worker/strategies/fixtures必要非缓存源文件无缺失，源码与当前工作区一致，未含bin/obj/__pycache__/运行数据/runtime。仅小文件只读核验，无新压力或业务运行。证据 `artifacts/supervisor-delivery-review-20261003/{zip-manifest-check,source-audit,r2-r3-payload-diff}.json`。

## 未通过事项与最小修复

1. 源码ZIP根README仍称G1原型、不执行策略、不接TqSdk、无偏移时间按上海解释，且“项目自带隔离SDK”的tools/dotnet已经按要求排除。这些不是当前G6代码行为，入口误导。更新本任务根README/README-source为当前源码与运行包分工、真实时间格式及构建前置条件；源码包不自带SDK/runtime，明确需要.NET10 SDK及tools/package-offline.ps1所需本机SDK目录，不需要实际下载/重建程序。
2. ASCII重命名后实现关卡和方案索引有8处Markdown目标失效；SOURCE-SELECTION仅概括映射而无具体映射表。保留原目录/名称或给明确old→new映射并修归档内引用。应核对交付材料中的纯文本docs/progress等路径与归档实际位置；历史证据不随源码复制时明确它们是原工作区证据。不得把相对链接指向不存在文件。
3. 源码ZIP的current-status在更新之前打包，仍保留旧百万外层断言和旧交付入口，和工作区新版不一致；g6-delivery-materials仅增加归档自身哈希导致内容差异可采用独立伴随清单解决循环，但current-status业务状态必须冻结准确再打包。
4. 工作区p04-performance-experiment第94行仍称buf64外层文件已找到并指向r20/million-timing.json，该原文件只有128443.1954ms/exit0/05FE3E85…，不能支撑42460.8。新主交付文档已写缺失，仍需同步这条事实，不改历史数值/不补造/不跑百万。buf64原run/replay-metrics中的Worker40278确有负责人原始复核，不要将其也降格为仅总结值；缺的是buf64外层/独立资源文件。

下一任务仅文档/源码归档修复与严格清单/链接/工作区源码一致检查。保留已通过r3运行ZIP不重编译、不重复测试，不进入G5；A05人工可视、新机器、P04失败及真实权限/两日验证状态保持。
