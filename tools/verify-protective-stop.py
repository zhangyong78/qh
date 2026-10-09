"""Exercise persistent stops through the real worker and CLI, in isolated data."""
from pathlib import Path
import csv, json, re, subprocess

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'protective-stop-checks-20261009'
OUT.mkdir(parents=True, exist_ok=True)
CLI = ROOT / 'src/Qh.Cli/bin/Release/net10.0/Qh.Cli.dll'

def call(*args):
    result = subprocess.run(['dotnet', str(CLI), *map(str, args)], cwd=ROOT,
                            capture_output=True, text=True, encoding='utf8', timeout=60)
    assert result.returncode == 0, result.stdout + result.stderr
    return result.stdout

results = []
for side, scenario, stop, target, expected in [
    ('long', 'touch_then_rebound', 101, (110, 140, 101, 135), 100),
    ('short', 'touch_then_rebound', 99, (90, 99, 60, 65), 100),
    ('long', 'adverse_gap', 101, (90, 105, 85, 104), 89),
    ('short', 'adverse_gap', 99, (110, 115, 95, 96), 111),
    ('long', 'persist_across_bars', 101, (110, 120, 101, 118), 100),
    ('short', 'persist_across_bars', 99, (90, 99, 80, 82), 100),
    ('long', 'tighten_next_bar', 101, (130, 145, 120, 140), 119),
    ('short', 'tighten_next_bar', 99, (70, 80, 65, 70), 81),
]:
    name = side + '-' + scenario
    data = OUT / name
    source = OUT / (name + '.csv')
    # Submission bar crosses its own candidate stop: it must not close retroactively.
    # Opening bar has no protective order yet, also deliberately excluded.
    prices = [(100, 110, 90, 100), (100, 150, 50, 100), (100, 150, 50, 100)]
    if scenario == 'persist_across_bars':
        prices += [(110, 120, 105, 115)] if side == 'long' else [(90, 95, 80, 85)]
    if scenario == 'tighten_next_bar':
        prices += [(130, 150, 110, 140)] if side == 'long' else [(70, 90, 50, 60)]
    prices += [target, (135, 145, 125, 140)]  # Must not wait for this rebound/open.
    with source.open('w', encoding='utf8', newline='') as f:
        w = csv.writer(f)
        w.writerow(['timestamp','symbol','exchange','period','trading_day','open','high','low','close','volume','open_interest','is_complete'])
        for index, row in enumerate(prices):
            w.writerow([f'2026-10-08T09:{index:02}:00+08:00','CZCE.SR701','CZCE','1m','2026-10-08',*row,1,1,'true'])
    strategy = OUT / (name + '.py')
    strategy.write_text('''STRATEGY = {"name":"protective fixture","version":"1","kind":"trade","warmup_bars":0,"parameters":{}}
def create_strategy(parameters): return Strategy()
class Strategy:
    def on_bar(self, context):
        if context.bar_index == 0:
            return {"intent":{"action":"open_limit","direction":"SIDE","quantity":1,"reference_price":100,"description":"entry"}}
        if context.bar_index == 2:
            return {"intent":{"action":"protect","direction":"SIDE","quantity":1,"reference_price":STOP,"description":"2R锁0R"}}
        if context.bar_index == 3 and "SCENARIO" == "tighten_next_bar":
            return {"intent":{"action":"protect","direction":"SIDE","quantity":1,"reference_price":TIGHT,"description":"4R锁3R"}}
'''.replace('SIDE',side).replace('STOP',str(stop)).replace('SCENARIO',scenario).replace('TIGHT',str(120 if side=='long' else 80)),encoding='utf8')
    snapshot = re.search(r'snapshot=(snapshot-[^;\s]+)',call('import',source,data))[1]
    rid = re.search(r'run_id=([^;\s]+)',call('trade-replay',data,snapshot,strategy,
        '2026-10-08T09:00:00+08:00','2026-10-08T09:10:00+08:00',
        '--initial-equity=100000','--slippage-ticks=1','--open-fee=2','--today-fee=3'))[1]
    run = data / 'trade-runs' / rid
    def read(name): return [json.loads(l) for l in (run/name).read_text(encoding='utf-8-sig').splitlines()]
    fills = read('fills.jsonl'); trades = read('closed-trades.jsonl')
    assert len(fills) == 2 and len(trades) == 1
    assert fills[1]['Price'] == expected
    assert fills[1]['BarEnd'].startswith(f'2026-10-08T09:{len(prices)-1:02}:')
    assert trades[0]['NetPnl'] == (expected-100)*(1 if side=='long' else -1)*10-5
    results.append(dict(side=side, scenario=scenario, run_id=rid, exit_price=expected, net_pnl=trades[0]['NetPnl']))
(OUT/'verification.json').write_text(json.dumps(dict(passed=True,results=results),ensure_ascii=False,indent=2),encoding='utf8')
print('PASS: both sides touch/rebound, adverse gaps, persistent orders, delayed activation, fees/slippage; no exit on submission or entry bar.')
