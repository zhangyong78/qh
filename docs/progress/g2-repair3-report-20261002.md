# G2 第三轮审核返修报告

日期：2026-10-02，Asia/Shanghai  
状态：返修完成，待负责人再次审核；不进入 G3

对应审核：`docs/progress/supervisor-g2-repair2-review-20261002.md`。

## 本轮两项最小修复

1. `ReplayButton_Click` 仍会重新 Describe 当前策略，但 `LoadStrategyAsync(resetParameters:false)` 只更新策略描述，不再调用 `SetDefaultReplayRange()`；因此用户已确认的正式起止和参数 JSON 在回放前保持不变。显式“加载/校验策略”或新快照初始化时才设置默认范围。
2. `LoadStrategyAsync` 的已有任务保护移到 `try` 外；本次方法只清理自己创建的 CTS/Task，第二次点击不会 Dispose 第一项加载任务的令牌或清空它的追踪句柄。窗口关闭仍能取消并等待第一项任务。

## 回归证据

命令使用当前构建的 CLI 和固定快照 `snapshot-a762946b720ae114`。

| 场景 | 结果 | 证据 |
|---|---|---|
| 正式区间 09:04～09:05 | `completed`，run 保存相同起止；只有 `09:05 bar_end` 的 `cross_up` 正式信号 | `sig-20261002064827840-689184be8` |
| 同一区间 + 自定义 `fast_period=3 slow_period=4` | `completed`，参数与区间均写入新 run，预热需求按4根执行 | `sig-20261002064855349-fb33ffc5d` |
| 无参数策略 | `completed`，未被 fast/slow/average 注入阻断 | `sig-20261002062613153-0c3444fb9` |
| 参数化 `period=4` 策略 | `completed`，通用参数与参数相关预热通过 | `sig-20261002062613679-4fcd98f49` |
| 指定行异常 | `failed`，`run.json` 包含策略副本、`line 18`、traceback、`STDERR_MARKER_G2_RUNTIME`、`stderr_final` | `sig-20261002062754702-8e87f3e98` |

区间 run 的 `run.json` 关键值：

```text
StartInclusive=2026-10-01T09:04:00+08:00
EndExclusive=2026-10-01T09:05:00+08:00
FormalSignalCount=1
Parameters={"fast_period":2,"slow_period":3,"average":"sma"}
```

## G3 仅预备手算夹具

按本轮要求，仅在 `docs/03-Luna实施任务与验收关卡.md` 第 6.5 节补充独立手算说明：F01、空头、反向、资金不足、零交易、跨交易日普通平仓、比例费用、期末未平仓、跳空、缺口/取消的假设、输入和逐项预期。没有实现订单、成交、持仓、费用或权益引擎，不能视为进入 G3。

## 构建与摘要

```powershell
$env:DOTNET_ROOT = Join-Path (Get-Location) 'tools\dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
& $dotnet build src\Qh.Cli\Qh.Cli.csproj --configuration Release --nologo
& $dotnet build src\QhApp\QhApp.csproj --configuration Release --nologo
python -m py_compile worker\qh_signal_worker.py strategies\sma_ema_cross.py strategies\preheat_signal.py strategies\period_signal.py strategies\stderr_error.py
```

结果：CLI/WPF 0 warning、0 error；Python 语法检查通过。

当前源码 SHA-256：

- Worker：`1C6A56F85C4F677042BF67A4EE2F94AF9AE5D0EF6A21DABC02C2A59BCC4C013E`
- Runner：`F7E9A121830A32CE180C1E27DA3E136E6841FE534A243C1E8274C514C6ED7893`
- MainWindow.cs：`C9CB6D68162A1062469E251E65018BC126A207A14DC550A66F3C19353DA556C4`
- DataStore.cs：`500123D66CDAFE790B67DDCEC75B5D6935C93D75BBEAA40D96279CEE67E0E57D`

`docs/04-负责人监督记录.md` 未修改；本次核对摘要为 `30EC274FDE229012097DF6413DAF8ECB6AA009A9C2AD9EF383A81BF8C2D4FB59`。

## 仍未验证

- 原生 Windows 实际点击/截图/打包、P02/P03 规模性能仍未验证。
- 加载挂起从请求到完整结束约 5.447 秒，P06 五秒目标不宣称通过。

结论：G2 第三轮返修完成，提交负责人审核，停在 G2，不进入 G3。
