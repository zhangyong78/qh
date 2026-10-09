"""Boundary checks for the short EMA strategy, using the actual worker schema."""
from pathlib import Path
import csv
import json
import re
import runpy
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
worker = runpy.run_path(str(ROOT / "worker" / "qh_signal_worker.py"))
short = worker["load_module"](str(ROOT / "strategies" / "ema_dynamic_short_trade.py"))
metadata = worker["validate_metadata"](short)
long = worker["load_module"](str(ROOT / "strategies" / "ema_dynamic_long_trade.py"))


class Context:
    def __init__(self, *, high=109, low=95, close=95, fast=90, trend=100, position=None, costs=None):
        self.bar = {"high": high, "low": low, "close": close}
        self.values = {21: fast, 55: trend}
        self.simulation = {"position": position}
        if costs is not None:
            self.simulation["costs"] = costs

    def ema(self, period):
        return self.values[period]

    def atr(self, period):
        assert period == 14
        return 5.0


def no_exit(result):
    return result is None or result["intent"]["action"] == "protect"


def create(**overrides):
    return short.create_strategy(worker["parameters_with_defaults"](metadata, overrides))


def entered():
    strategy = create(profit_lock_start_r=3, ema_exit_period=0, max_entries_per_trend=1)
    intent = strategy.on_bar(Context())["intent"]
    assert intent["direction"] == "short" and intent["reference_price"] == 100
    assert intent["quantity"] == 10
    # An extreme entry bar must not retrospectively tighten or hit a stop.
    position = {"direction": "short", "entry_price": 100, "quantity": 8}
    assert no_exit(strategy.on_bar(Context(high=150, low=30, position=position)))
    assert strategy.active_stop == 110 and strategy.initial_risk_distance == 10
    return strategy, position


for ema, expected in ((21, 90), (55, 100)):
    intent = create(entry_ema_period=ema).on_bar(Context(close=85))["intent"]
    assert intent["reference_price"] == expected
    assert intent["action"] == "open_limit" and intent["quantity"] == 10

try:
    create(entry_ema_period=34)
    raise AssertionError("unsupported entry EMA accepted")
except ValueError:
    pass

strategy, position = entered()
assert no_exit(strategy.on_bar(Context(low=80, position=position)))
assert strategy.active_stop == 99  # At 2R, move the stop with fee/tick protection.
assert no_exit(strategy.on_bar(Context(high=109, low=79, position=position)))
assert strategy.active_stop == 99  # 2R + F, fee-covered short breakeven.
result = strategy.on_bar(Context(high=99, low=95, position=position))
assert result is None  # Engine owns the already-live 2R protective order.
assert "2R触发锁0R" in strategy.active_stop_reason

strategy, position = entered()
assert no_exit(strategy.on_bar(Context(low=69, position=position)))
assert strategy.active_stop == 79  # 3R + F locks 2R + F.
assert no_exit(strategy.on_bar(Context(high=78, low=70, position=position)))
assert strategy.active_stop == 79  # Never loosen protection.
assert no_exit(strategy.on_bar(Context(high=78, low=49, position=position)))
assert strategy.active_stop == 59  # Cross-level 5R locks 4R + F.
result = strategy.on_bar(Context(high=59, low=55, position=position))
assert result is None
assert "5R触发锁4R" in strategy.active_stop_reason

strategy, position = entered()
result = strategy.on_bar(Context(high=110, low=30, position=position))
assert result["intent"]["action"] == "protect"
# The engine checks the old stop before the strategy receives this completed bar.
# A surviving position may update its stop for the following bar only.

strategy, position = entered()
assert no_exit(strategy.on_bar(Context()))  # One entry per bearish trend.
assert no_exit(strategy.on_bar(Context(fast=101)))  # Bullish crossover rearms entry.
assert strategy.on_bar(Context())["intent"]["action"] == "open_limit"
skip = create(risk_budget=50).on_bar(Context())
assert "intent" not in skip and skip["signal"]["category"] == "entry_skipped_risk_budget"

# Fractional ATR distance and non-unit ticks must round stops away from entry.
strategy = create(min_tick=0.5, atr_stop_multiplier=2.03)
strategy.on_bar(Context())
strategy.on_bar(Context(position={"entry_price": 100, "quantity": 8}))
assert strategy.active_stop == 110.5 and strategy.initial_risk_distance == 10.5
print("PASS: both entry EMAs, worker schema, sizing, entry-bar exclusion, 2R/3R/5R boundaries, monotonic stops, stop priority, trend rearm and tick rounding.")

# Profit activation cannot use the same completed bar to invent an EMA exit.
strategy = create(profit_lock_start_r=4, ema_exit_period=55, ema_exit_activation_r=3)
strategy.on_bar(Context())
position = {"entry_price": 100, "quantity": 8}
strategy.on_bar(Context(position=position))
assert no_exit(strategy.on_bar(Context(high=109, low=69, close=105, position=position)))
assert strategy.ema_exit_armed and strategy.active_stop == 99
result = strategy.on_bar(Context(high=98, low=85, close=90, trend=89, position=position))
assert result["intent"]["action"] == "close"
assert "达到3R后收盘突破EMA55" in result["intent"]["description"]
strategy = create(max_entries_per_trend=0)
strategy.on_bar(Context()); strategy.on_bar(Context(position=position))
assert strategy.on_bar(Context())["intent"]["action"] == "open_limit"
print("PASS: delayed EMA activation, configurable trailing start and unlimited sequential entries.")

# Long-side reason labels match the active stop that was actually touched.
long_metadata = worker["validate_metadata"](long)
long_strategy = long.create_strategy(worker["parameters_with_defaults"](long_metadata, {
    "ema_exit_period": 0, "profit_lock_start_r": 4,
}))
assert long_strategy.on_bar(Context(fast=110, trend=100, close=110))["intent"]["action"] == "open_limit"
long_position = {"direction": "long", "entry_price": 100, "quantity": 8}
assert no_exit(long_strategy.on_bar(Context(fast=110, trend=100, high=120, low=80, close=110, position=long_position)))
assert no_exit(long_strategy.on_bar(Context(fast=110, trend=100, high=141, low=135, close=140, position=long_position)))
assert long_strategy.active_stop == 131 and "4R触发锁3R" in long_strategy.active_stop_reason
long_exit = long_strategy.on_bar(Context(fast=110, trend=100, high=140, low=130, close=135, position=long_position))
assert long_exit is None
assert "4R触发锁3R" in long_strategy.active_stop_reason

# The current run's two-way fixed fee reserve is 2 open + max(3 today, 1 ordinary),
# converted by the 10-unit contract multiplier. The long/short stops round away
# from entry to the next valid tick so the fee reserve is not understated.
fixed_costs = {"fee_mode": "fixed", "multiplier": 10, "open_fee": 2,
               "today_close_fee": 3, "ordinary_close_fee": 1, "fee_rate": 0,
               "slippage_ticks": 0, "min_tick": 1}
long_entry = long.create_strategy(worker["parameters_with_defaults"](long_metadata, {
    "ema_exit_period": 0,
})).on_bar(Context(fast=110, trend=100, close=110, costs=fixed_costs))
assert long_entry["intent"]["quantity"] == 10  # Fees do not reduce the opening size.
long_cost_strategy = long.create_strategy(worker["parameters_with_defaults"](long_metadata, {
    "ema_exit_period": 0, "dynamic_fee_offset_points": 0,
}))
long_cost_strategy.on_bar(Context(fast=110, trend=100, close=110, costs=fixed_costs))
assert no_exit(long_cost_strategy.on_bar(Context(fast=110, trend=100, position=long_position, costs=fixed_costs)))
assert no_exit(long_cost_strategy.on_bar(Context(fast=110, trend=100, high=123, low=110, close=115, position=long_position, costs=fixed_costs)))
assert long_cost_strategy.active_stop == 101
assert "双向手续费0.50点" in long_cost_strategy.active_stop_reason

short_cost_strategy = create(dynamic_fee_offset_points=0, ema_exit_period=0, profit_lock_start_r=4)
short_position = {"direction": "short", "entry_price": 100, "quantity": 8}
short_cost_strategy.on_bar(Context(costs=fixed_costs))
assert no_exit(short_cost_strategy.on_bar(Context(position=short_position, costs=fixed_costs)))
assert no_exit(short_cost_strategy.on_bar(Context(low=77, position=short_position, costs=fixed_costs)))
assert short_cost_strategy.active_stop == 99
assert "双向手续费0.50点" in short_cost_strategy.active_stop_reason

rate_costs = {"fee_mode": "rate", "multiplier": 10, "fee_rate": 0.001,
              "slippage_ticks": 0, "min_tick": 1}
long_rate_strategy = long.create_strategy(worker["parameters_with_defaults"](long_metadata, {
    "ema_exit_period": 0, "dynamic_fee_offset_points": 0,
}))
long_rate_strategy.on_bar(Context(fast=110, trend=100, close=110, costs=rate_costs))
assert no_exit(long_rate_strategy.on_bar(Context(fast=110, trend=100, position=long_position, costs=rate_costs)))
assert no_exit(long_rate_strategy.on_bar(Context(fast=110, trend=100, high=123, low=110, close=115, position=long_position, costs=rate_costs)))
assert long_rate_strategy.active_stop == 101
assert "双向手续费0.20点" in long_rate_strategy.active_stop_reason
assert "滑点缓冲0.00点=总偏移0.20点" in long_rate_strategy.active_stop_reason

short_rate_strategy = create(dynamic_fee_offset_points=0, ema_exit_period=0, profit_lock_start_r=4)
short_rate_strategy.on_bar(Context(costs=rate_costs))
assert no_exit(short_rate_strategy.on_bar(Context(position=short_position, costs=rate_costs)))
assert no_exit(short_rate_strategy.on_bar(Context(low=77, position=short_position, costs=rate_costs)))
assert short_rate_strategy.active_stop == 99
assert "双向手续费0.20点" in short_rate_strategy.active_stop_reason

# Entry sizing excludes fees. The moving protective stop adds the two-way fee
# estimate and one-tick adverse-slippage buffer; stop prices round outward.
long_sum_strategy = long.create_strategy(worker["parameters_with_defaults"](long_metadata, {
    "ema_exit_period": 0, "dynamic_fee_offset_points": 1,
}))
long_sum_strategy.on_bar(Context(fast=110, trend=100, close=110, costs=fixed_costs))
assert no_exit(long_sum_strategy.on_bar(Context(fast=110, trend=100, position=long_position, costs=fixed_costs)))
assert no_exit(long_sum_strategy.on_bar(Context(fast=110, trend=100, high=123, low=110, close=115, position=long_position, costs=fixed_costs)))
assert long_sum_strategy.active_stop == 102  # base lock 100 + fee 0.5 + tick 1, rounded up.
assert "双向手续费0.50点+滑点缓冲1.00点=总偏移1.50点" in long_sum_strategy.active_stop_reason

short_sum_strategy = create(dynamic_fee_offset_points=1, ema_exit_period=0, profit_lock_start_r=4)
short_sum_strategy.on_bar(Context(costs=fixed_costs))
assert no_exit(short_sum_strategy.on_bar(Context(position=short_position, costs=fixed_costs)))
assert no_exit(short_sum_strategy.on_bar(Context(low=77, position=short_position, costs=fixed_costs)))
assert short_sum_strategy.active_stop == 98  # mirrored short side, rounded down.
assert "双向手续费0.50点+滑点缓冲1.00点=总偏移1.50点" in short_sum_strategy.active_stop_reason
print("PASS: fixed/rate fees, plus one-tick buffer, flow into long and short protection; realized fees remain charged once.")

if "--integration" in sys.argv:
    # Isolated synthetic data checks the engine's sell-limit clipping and touch
    # boundaries, including cases that may not occur in the historical sample.
    output = ROOT / "artifacts" / "ema-short-checks"
    output.mkdir(parents=True, exist_ok=True)
    cli = ROOT / "src" / "Qh.Cli" / "bin" / "Release" / "net10.0" / "Qh.Cli.dll"
    data_root = output / "fixture-data"
    source = output / "fixture.csv"
    fields = ["timestamp", "symbol", "exchange", "period", "trading_day", "open", "high", "low", "close", "volume", "open_interest", "is_complete"]
    with source.open("w", encoding="utf-8", newline="") as stream:
        writer = csv.writer(stream)
        writer.writerow(fields)
        for index, prices in enumerate([(100, 101, 99, 100), (100, 101, 98, 99), (99, 100, 97, 98), (98, 99, 97, 98)]):
            writer.writerow([f"2026-10-08T09:{index*15:02}:00+08:00", "CZCE.SR701", "CZCE", "15m", "2026-10-08", *prices, 1, 1, "true"])
    fixture = output / "limit-fixture.py"
    fixture.write_text('''STRATEGY = {"name":"short limit boundary fixture","version":"1.0.0","kind":"trade","warmup_bars":0,"parameters":{"limit":{"type":"float","default":100.0}}}
def create_strategy(parameters):
    return Strategy(parameters["limit"])
class Strategy:
    def __init__(self, limit): self.limit=limit
    def on_bar(self, context):
        if context.bar_index==0:
            return {"intent":{"action":"open_limit","direction":"short","quantity":1,"reference_price":self.limit,"description":"fixture limit"}}
        if context.bar_index==1 and context.simulation.get("position"):
            return {"intent":{"action":"close","direction":"short","quantity":1,"description":"fixture close"}}
''', encoding="utf-8")
    def call(*arguments):
        result = subprocess.run(["dotnet", str(cli), *map(str, arguments)], cwd=ROOT, text=True, encoding="utf-8", capture_output=True, timeout=60)
        assert result.returncode == 0, result.stdout + result.stderr
        return result.stdout
    imported = call("import", source, data_root)
    snapshot = re.search(r"snapshot=(snapshot-[^;\s]+)", imported)[1]
    for limit, expected in [(100, 100), (99, 99), (98, 99), (101, 101), (102, None)]:
        result = call("trade-replay", data_root, snapshot, fixture, "2026-10-08T09:00:00+08:00", "2026-10-08T10:00:00+08:00",
                      "--initial-equity=100000", "--lots=1", "--multiplier=10", "--min-tick=1", "--margin-rate=0.1", "--slippage-ticks=1",
                      "--fee-mode=Fixed", "--open-fee=2", "--today-fee=3", "--ordinary-fee=1", f"limit={limit}")
        run_id = re.search(r"run_id=([^;\s]+)", result)[1]
        run_dir = data_root / "trade-runs" / run_id
        fills_path = run_dir / "fills.jsonl"
        fills = [json.loads(line) for line in fills_path.read_text(encoding="utf-8-sig").splitlines()] if fills_path.exists() else []
        if expected is None:
            assert not fills
            assert "limit_not_touched" in (run_dir / "unfilled.jsonl").read_text(encoding="utf-8-sig")
        else:
            assert len(fills) == 2
            assert fills[0]["Direction"] == "short" and fills[0]["Price"] == expected
            assert fills[1]["Action"] == "close" and fills[1]["Price"] == 100
            assert fills[0]["Fee"] == 2 and fills[1]["Fee"] == 3
    print("PASS: five CLI sell-limit fixtures: open-at-limit, adverse-slip clipping, favorable gap, intrabar high touch, untouched rejection; short close slippage and fees.")
