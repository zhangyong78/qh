"""Independent decimal MA/next-open accounting audit of the saved SR result."""
import csv
import datetime as dt
import json
import sys
from decimal import Decimal as D
from pathlib import Path

root = Path(sys.argv[1])
data_directory = sys.argv[3] if len(sys.argv) > 3 else 'verified-data'
run = root / data_directory / 'trade-runs' / sys.argv[2]
record = json.loads((run / 'run.json').read_text(encoding='utf-8'))
with (root / 'SR2701-15m.csv').open(encoding='utf-8-sig', newline='') as stream:
    bars = list(csv.DictReader(stream))
def read_lines(name):
    return [json.loads(line, parse_float=D) for line in (run / name).read_text(encoding='utf-8').splitlines() if line]

actual_fills = read_lines('fills.jsonl')
actual_intents = read_lines('intents.jsonl')
actual_equity = read_lines('equity.jsonl')
closes = [D(bar['close']) for bar in bars]
start = dt.datetime.fromisoformat(record['StartInclusive'])
config = record['Config']
fast, slow = record['Parameters']['fast_period'], record['Parameters']['slow_period']
balance = D(str(config['InitialEquity']))
multiplier = D(str(config['Multiplier']))
slip = D(str(config['MinTick'])) * config['SlippageTicks']
fee = D(str(config['OpenFee']))
assert config['OpenFee'] == config['TodayCloseFee'] == config['OrdinaryCloseFee']
assert config['FeeMode'] == 0 and config['Lots'] == 1
assert config['StopLossPrice'] is None and config['TakeProfitPrice'] is None
position = None
entry = None
pending = None
fills = []
signals = []
closed_pnls = []
equities = []
peak, max_drawdown = balance, D(0)
previous = None
total_fees = D(0)
for i, bar in enumerate(bars):
    timestamp = dt.datetime.strptime(bar['timestamp'], '%Y/%m/%d %H:%M:%S').replace(tzinfo=dt.timezone(dt.timedelta(hours=8)))
    bar_end = timestamp + dt.timedelta(minutes=15)
    if pending is not None:
        if position is not None:
            exit_price = D(bar['open']) - position * slip
            gross = (exit_price - entry) * position * multiplier
            balance += gross - fee
            total_fees += fee
            closed_pnls.append(gross - 2 * fee)
            fills.append(('close', 'long' if position == 1 else 'short', exit_price, bar_end))
        position = pending
        entry = D(bar['open']) + position * slip
        balance -= fee
        total_fees += fee
        fills.append(('open', 'long' if position == 1 else 'short', entry, bar_end))
        pending = None
    equity = balance + ((closes[i] - entry) * position * multiplier if position is not None else D(0))
    peak = max(peak, equity)
    max_drawdown = max(max_drawdown, peak - equity)
    equities.append((bar_end, equity))
    if i + 1 >= slow:
        # Compare cross-products, avoiding rounding near an exact crossing.
        difference = sum(closes[i-fast+1:i+1]) * slow - sum(closes[i-slow+1:i+1]) * fast
        direction = 1 if previous is not None and previous <= 0 < difference else -1 if previous is not None and previous >= 0 > difference else None
        if direction is not None and timestamp >= start and direction != position:
            signals.append((timestamp, 'long' if direction == 1 else 'short'))
            pending = direction
        previous = difference

assert len(fills) == len(actual_fills)
for expected, actual in zip(fills, actual_fills):
    observed = (actual['Action'], actual['Direction'], D(str(actual['Price'])), dt.datetime.fromisoformat(actual['BarEnd']))
    assert expected == observed, (expected, observed)
assert len(signals) == len(actual_intents)
for expected, actual in zip(signals, actual_intents):
    assert expected == (dt.datetime.fromisoformat(actual['SignalBarStart']), actual['Direction'])
expected_equity = dict(equities)
for point in actual_equity:
    assert expected_equity[dt.datetime.fromisoformat(point['BarEnd'])] == D(str(point['Equity']))
assert equity == D(str(record['FinalEquity']))
assert max_drawdown == D(str(record['MaxDrawdown']))
assert total_fees == D(str(record['TotalFees']))
assert len(closed_pnls) == record['ClosedTradeCount']
summary = {
    'passed': True, 'method': 'independent Decimal SMA cross-products and next-bar-open accounting',
    'signals_verified': len(signals), 'fills_verified': len(fills), 'equity_points_verified': len(actual_equity),
    'closed_trades': len(closed_pnls), 'winning_trades': sum(x > 0 for x in closed_pnls),
    'net_pnl': str(equity - D(str(config['InitialEquity']))), 'final_equity': str(equity),
    'max_drawdown': str(max_drawdown), 'fees': str(total_fees),
    'open_position': 'long' if position == 1 else 'short' if position == -1 else None,
    'floating_pnl': str(equity - balance), 'terminal_liquidation': False,
}
(root / 'independent-audit.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(summary, ensure_ascii=False))
