# G1 第二轮返修报告：快照完整性、跨时段质量与退出生命周期

日期：2026-10-02，Asia/Shanghai  
返修依据：`docs/progress/supervisor-g1-repair-review-20261002.md`  
状态：待负责人审核；不进入 G2

## 1. 本轮修复

### 1.1 快照实际完整性

每个快照现在包含：

- `bars.csv`
- `bars.parquet`
- `quality.json`
- `snapshot.meta`
- `source.txt`

`snapshot.meta` 保存内容哈希、CSV 实际 SHA-256、Parquet 实际 SHA-256、质量 JSON 实际 SHA-256、行数和时段表版本。发布前使用 DuckDB 读取 Parquet，校验必需列结构和行数；复用时重新计算三个文件摘要并重新校验 Parquet。查询入口同样执行全部检查，损坏或不完整快照抛出 `InvalidDataException`，不返回“可查询成功”。

### 1.2 跨显式时段缺口

缺口算法不再要求前后 K 线属于同一时段，而是枚举两条记录之间每个周期点，只统计落入版本化测试时段表的时间点：

`cn-futures-g1-test-v1 = 09:00–10:15、10:30–11:30、13:30–15:00、21:00–23:00、23:00–02:30`

休市点不填充、不计入缺口；跨 23:00 和跨午休会拆成有效段报告。

### 1.3 质量报告持久化/重载

完整 `QualityReport` 以 `quality.json` 随快照保存，包含冲突、重复、乱序、未完成、疑似缺口、输入范围和时段表版本。`DataStore.ReadQualityReport(snapshotId)` 会先验证快照，再反序列化质量报告。重复导入复用相同快照及质量文件，不覆盖既有研究输入；`ImportResult` 仍是单次导入任务报告，和快照质量文件分开。

### 1.4 任务取消与退出

- 导入、快照查询和指标计算均有可追踪 Task；查询在 DuckDB 读行期间检查取消令牌。
- 窗口存在活动导入/查询/Worker 时显示退出确认：选择“是”停止任务并退出，选择“否”取消退出。
- 停止过程异步等待实际任务终态；5 秒导入/查询等待或 2 秒 Worker 停止超时都会记录诊断并保持窗口，不在未确认终止时关闭。
- Worker 启动异常、ready 超时、cancelled 超时、stop 未确认、异常退出码均进入状态/日志；不使用 UI 阻塞等待。

## 2. 新增回归证据

### 2.1 质量重载与正常复用

证据目录：`D:\mycode\qh\artifacts\g1-final2b-20261002122437\normal`。

正常导入和 DuckDB 往返成功：8 行；随后执行：

```powershell
& "$env:DOTNET_ROOT\dotnet.exe" run --project 'src\Qh.Cli\Qh.Cli.csproj' --configuration Release --no-build -- quality-check 'artifacts\g1-final2b-20261002122437\normal' 'snapshot-798cd202583749aa'
```

结果：`quality_rows=8; gaps=3; issues=0; session_table=cn-futures-g1-test-v1`，退出码 0。相同输入重复导入退出码 0，快照 ID 不变。

`snapshot.meta` 中保存了三类实际文件摘要；`quality.json` 中保存了完整缺口段和时段版本。

### 2.2 损坏 Parquet 拒绝与恢复

证据目录：`D:\mycode\qh\artifacts\g1-corrupt-20261002122521\case`。

步骤和结果：

1. 正常发布 8 行快照。
2. 将 `bars.parquet` 替换为非空损坏内容。
3. `quality-check` 输出 `quality_check_failed=InvalidDataException: 快照 Parquet 摘要不匹配`，退出码 5；查询入口拒绝损坏状态。
4. 用同一 CSV 重导入：重新生成 Parquet，DuckDB 读回 8 行，退出码 0；旧损坏目录被隔离。

### 2.3 跨时段缺口

证据目录：`D:\mycode\qh\artifacts\g1-final2b-20261002122437`。

- `gap23`：22:50→23:10 报有效缺口 19 根（22:51～23:09）。
- `gaprest`：09:00→10:40 报有效缺口 84 根，拆为 09:01～10:14 与 10:30～10:39；10:15～10:29 午休不计入。
- 既有 `sample-bars-scheduled-rest-5m.csv`：10:10→10:30，疑似缺口 0。

### 2.4 Worker/程序生命周期

Worker 实测：`ready → started → heartbeat → cancelled → stopped`，`hasExited=True; exitCode=0; worker_processes_remaining=0`。另实测 stdin EOF 也收到 `stopped(reason=stdin_closed)` 并正常退出。

最终 WPF publish：`D:\mycode\qh\artifacts\g1-second-repair-app-20261002122751`；启动进程存活检查通过，随后明确停止，`wpf_exit=True`，无残留 QhApp/Worker 进程。该启动检查仅是进程证据，不是人工 UI 验收。

## 3. 构建与回归

- `dotnet build src/Qh.Cli/Qh.Cli.csproj --configuration Release`：0 warnings/0 errors。
- `dotnet build src/QhApp/QhApp.csproj --configuration Release`：0 warnings/0 errors。
- WPF publish：成功。
- SMA 全序列与前缀不变性：通过，CLI `indicator_exit=0`。
- Python Worker AST 语法检查：`python_syntax=ok`。

## 4. 验收状态

| 项目 | 状态 |
|---|---|
| 快照实际摘要/Parquet 结构/行数校验 | 通过实施回归，待负责人审核 |
| 跨 23:00、跨午休的有效段缺口 | 通过实施回归，待负责人审核 |
| 质量报告保存与重载 | 通过实施回归，待负责人审核 |
| 导入/查询取消与退出状态 | 代码实现并有进程协议证据；人工退出确认未验证 |
| WPF 人工视觉、缩放手感、退出对话框 | 未验证，当前原生 UI 工具不可用 |
| TqSdk、策略、通知、真实行情、长时性能 | 未开始/未验证 |

## 5. 结论

本轮只处理 G1 数据可靠性和任务生命周期，没有进入 G2。`docs/04-负责人监督记录.md` 未修改。当前保持“待负责人审核”，等待负责人用同一基础诊断和新增损坏/跨时段/质量重载/退出证据复核。
