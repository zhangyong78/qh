# G1 第三轮返修报告：合法快照不可变复用

日期：2026-10-02，Asia/Shanghai  
返修依据：`docs/progress/supervisor-g1-second-repair-review-20261002.md`  
状态：待负责人审核；不进入 G2

## 修复

根因是 `HasCompleteSnapshot` 把已有快照的质量报告摘要与本次导入任务的质量报告摘要比较。重复行或输入逆序会改变本次 `ImportResult`，但不改变规范化后的快照内容，导致合法快照被错误隔离并改写。

现在复用判定只依赖：

- 相同规范化内容 `content_hash`；
- 现有 `bars.csv` 实际 SHA-256 与预期规范化 CSV 摘要一致；
- 现有 `bars.parquet` 实际 SHA-256、必需列结构和行数通过校验；
- 现有 `quality.json` 实际 SHA-256、JSON 结构和行数通过校验。

不再比较本次任务的 `quality_sha256`。因此本次重复/乱序诊断保留在 `ImportResult`，原快照的质量报告、来源和其它文件保持不可变。只有损坏/不完整快照才隔离重建。

## 回归证据

### 重复与逆序复用

证据目录：`D:\mycode\qh\artifacts\g1-final3-20261002125015\immutable`。

1. 导入 `fixtures/sample-bars-two.csv`，生成 `snapshot-378f4cb1e1ec9ba9`。
2. 导入 `fixtures/sample-bars-two-duplicate.csv`：同一 snapshotId，`duplicates=1`，查询成功。
3. 导入 `fixtures/sample-bars-two-reverse.csv`：同一 snapshotId，`out_of_order=1`，查询成功。
4. 比较原目录 `bars.csv`、`bars.parquet`、`quality.json`、`snapshot.meta`、`source.txt` 的 SHA-256：`snapshot_files_unchanged=True`。
5. 质量重载仍成功：`quality_rows=2; gaps=0; issues=0; session_table=cn-futures-g1-test-v1`。

这证明任务诊断变化不会改写既有研究输入。

### 损坏重建保持有效

证据目录：`D:\mycode\qh\artifacts\g1-corrupt-final-20261002125035`。

- 将非空损坏内容写入 `bars.parquet` 后，`quality-check` 返回 `InvalidDataException: 快照 Parquet 摘要不匹配`，退出码 5。
- 用同一规范化 CSV 重导入后，Parquet 重新生成，DuckDB 查询成功，退出码 0。

### 构建

WPF 使用项目隔离 .NET SDK 重新构建并发布：

`D:\mycode\qh\artifacts\g1-third-repair-app-20261002125054`

结果：0 warnings、0 errors，publish 成功。

## 未验证项

- 原生 WPF 人工交互、关闭确认实际点击、全规模 P02/P03：未验证；不以构建或进程启动代替。
- TqSdk、策略执行、通知、真实行情和后续 G2：未开始。

## 结论

负责人指出的最后一项 G1 数据不可变性问题已按最小范围修复；原有损坏快照拒绝/重建逻辑保留。当前继续停留 G1 待负责人审核，不修改 `docs/04-负责人监督记录.md`。
