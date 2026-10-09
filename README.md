# 期货研究与提醒：G6 源码与离线运行包

当前项目包含 Windows x64 WPF 研究入口、CSV 快照/质量校验、Signal/Trade Python 策略回放、模拟撮合/权益结果、只读重开和受管理 Worker 的取消/退出收尾。配套 self-contained 运行包提供包内 .NET、Python 和项目隔离 TqSdk；源码归档不嵌入这些 runtime。

当前应用版本：**v0.1.0**。版本显示在主窗口标题和首页标题中。

## 源码构建前置条件

目标环境为 Windows 11 + PowerShell 7。源码归档不包含 `tools\dotnet`、.NET runtime、Python runtime、`bin/`、`obj/` 或用户数据；直接构建需要本机安装 .NET 10 SDK：

桌面“期货研究与回测工作台”快捷方式运行 `tools\start-dev.ps1`，始终从当前源码启动开发版。手动运行：

```powershell
& '.\tools\start-dev.ps1'
```

也可以直接运行项目：

```powershell
dotnet build 'src\Qh.Cli\Qh.Cli.csproj' -c Release
dotnet build 'src\QhApp\QhApp.csproj' -c Release
```

`tools\package-offline.ps1` 是发布包脚本，按其现有约定使用仓库内 `tools\dotnet\dotnet.exe`，并准备本机 Python 3.12/pip 以安装 `requirements-tqsdk.txt` 和生成包内运行时；这些 SDK/runtime 不随源码归档提供。无需为本源码说明下载或重建程序；用户运行应使用配套的 self-contained ZIP。

## 当前离线行为

运行包可以执行可信本地 Python signal/trade 策略、导入 CSV、保存快照、生成 signals/orders/fills/equity 和重新打开历史 run；不接真实下单。包内策略不是安全沙箱，不应读取未来 K 线、访问网络/账号或调用真实交易链路。

CSV 必须包含：`timestamp,symbol,exchange,period,trading_day,open,high,low,close,volume,open_interest,is_complete`。交付格式要求 `timestamp` 使用带显式时区偏移的 ISO-8601；解析器会归一化到 `+08:00`，对无偏移文本仍保留兼容性解释路径，但这不是推荐输入格式。严格导入检查冲突、非法 OHLC、未完成 K 线、重复和时间质量；单次导入只允许一个 symbol/exchange/period。

策略必须声明 `STRATEGY`、提供 `create_strategy(parameters)` 和 `on_bar(context)`；接口、夹具、备份升级和逐项 A/P 状态见 `docs/progress/g6-delivery-materials-20261003.md`。当前 G6 交付包、源码包和限制以该材料及 `docs/progress/current-status.md` 为准。

## 明确边界

- P04 固定百万根目标已测失败，30 秒目标未降低；这不因源码归档或有限输出保护回归而改变。
- A05 实际 WPF 可视/人工点击、P01 人工首页、新机器、真实账号/权限、两完整交易日、P05/P07 和 G5 均未由源码归档宣称通过。
- Python 文件按可信本地程序边界处理，不是安全沙箱；本项目不包含账号密码或真实凭据。
