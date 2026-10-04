# G2 Python 信号回放最小契约

版本：g2-v1，日期：2026-10-02，Asia/Shanghai

## CSV 时间边界

CSV `timestamp` 表示 K 线起始时间，已统一为 Asia/Shanghai；K 线结束时间由周期计算：1m 加 1 分钟，5m/15m/30m/60m 加相应分钟，日线加 1 天。正式研究区间使用半开区间 `[formal_start_inclusive, formal_end_exclusive)`：起始时间包含，结束时间不包含。预热数据是正式开始前的已完成 K 线；正式统计只使用区间内 K 线。

## 策略文件契约

可信本地 `.py` 文件提供：

```python
STRATEGY = {
    "name": "策略名称",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 20,
    "warmup_bars_parameters": ["period"],
    "parameters": {
        "period": {"type": "int", "default": 20, "min": 1, "max": 1000}
    }
}

def create_strategy(parameters):
    return Strategy(parameters)

class Strategy:
    def on_bar(self, context):
        return None
```

`context` 只包含当前完成 K 线、K 线结束时间、正式/预热标记、参数和当前已到达数据计算出的 `sma(n)`、`ema(n)`、`atr(n)` 方法；不提供未来 K 线或可迭代的完整输入序列。返回 `None`、一个信号字典或信号字典列表。信号字典至少包含 `category`、`direction`、`description`，可选 `reference_price`。

策略是可信本地 Python，不是安全沙箱。语法、元数据、参数、运行异常和超时均记录策略路径/版本、运行 ID、K 线时间和错误。

`warmup_bars_parameters` 是参数相关预热声明；实际所需预热为 `max(warmup_bars, 所列参数值)`。正式区间前必须存在足量已完成 K 线，且正式区间至少包含一根已完成 K 线；否则 run 为 `blocked_coverage`，不伪造“完成但无信号”。

## 指标口径

- SMA(N)：当前已到达窗口最近 N 根收盘均值；不足 N 根为 `None`。
- EMA(N)：第 N 根用前 N 根收盘均值初始化，之后权重 `2/(N+1)` 递推。
- ATR(N)：首根 TR 为高低差；后续 TR 为高低差、`|高-前收|`、`|低-前收|` 最大值；第 N 根为 N 根 TR 均值，之后 Wilder 平滑。

## 运行与质量

每次回放创建独立 run ID 和策略文件快照。默认严格模式：快照质量报告存在疑似缺口、无效字段或未完成数据时阻止运行；探索模式显式保存 `data_incomplete=true` 及质量报告，不与严格结果混淆。预热信号不进入正式信号列表和统计。

信号保存策略版本、策略摘要、参数、快照 ID、K 线起止/结束时间、类别、方向、说明和参考价。重复确认同一 K 线结束边界不重复执行。查看、筛选、缩放和导出只读已保存信号，不重新运行策略；新研究必须生成新 run ID。

## G2 固定夹具

- 多信号：收盘 `10,12,9,10,14`，SMA(2)/SMA(3) 上穿发生在第 5 根。
- 下穿：收盘 `12,14,13,9`，第 4 根下穿。
- 无信号：单调收盘序列且快线不交叉，完成运行、信号数为 0，不视为异常。
- 预热：N 根之前指标未就绪，预热信号不计入正式结果。
- 未完成/缺口：未完成不能进入 G1 严格快照；质量有缺口的快照严格模式阻止，探索模式带不完整标记。
- 重复边界：同一完成 K 线再次输入只确认一次。
- 确定性重复：同一快照、策略快照、参数和边界产生相同信号序列；旧 run 文件不被覆盖。
- 语法/参数/运行异常、超时、取消和 Worker 退出均为失败或取消状态，不标记成功。
