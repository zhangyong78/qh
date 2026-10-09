"""Replay existing frozen parameters with corrected stop execution; retain old runs."""
import hashlib, json, re, runpy, subprocess
from datetime import datetime
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA = ROOT / 'artifacts/sr2701-15m/wenhua-aligned-data'
OUT = DATA / 'multiside-research-20261008'
H = runpy.run_path(str(ROOT/'tools/run-sr-ema-short-comparison.py'))
R = runpy.run_path(str(ROOT/'tools/run-sr-short-research.py'))
E = runpy.run_path(str(ROOT/'tools/build-sr-multiside-report.py'))['extra']
manifest = json.loads((OUT/'final-main-release.json').read_text(encoding='utf8'))
prep = R['prepare'](DATA, '15m')
results = []
for side in ('long','short'):
    path = ROOT/'strategies'/f'ema_dynamic_{side}_trade.py'
    params = manifest['parameters'][side]
    for slip in (0,1,2):
        args = ['dotnet',str(H['CLI']),'trade-replay',str(DATA),prep[0],str(path),manifest['start'],manifest['end'],
                '--initial-equity=100000','--lots=1','--multiplier=10','--min-tick=1','--margin-rate=0.1',
                '--fee-mode=Fixed','--open-fee=2','--today-fee=3','--ordinary-fee=1',f'--slippage-ticks={slip}',
                *[f'{k}={v}' for k,v in params.items()]]
        p = subprocess.run(args,cwd=ROOT,capture_output=True,text=True,encoding='utf8',timeout=180)
        assert p.returncode == 0,p.stdout+p.stderr
        rid = re.search(r'run_id=([^;\s]+)',p.stdout)[1]
        record = json.loads((DATA/'trade-runs'/rid/'run.json').read_text(encoding='utf-8-sig'))
        assert record['Status'] == 'completed'
        item = dict(direction=side,entry_ema=55,slippage_ticks=slip,run_id=rid,record=record)
        detail = E(H['summarize'](item,DATA,prep[2],prep[3]),rid)
        detail.update(label='多头复核主选' if side=='long' else '空头复核主选',role='main',parameters=params)
        positions = H['read_jsonl'](DATA/'trade-runs'/rid/'positions.jsonl')
        detail['max_initial_margin'] = max((p['ReservedMargin'] for p in positions),default=0)
        results.append(detail)
        print(f'PASS {side} {slip}tick: net={detail["net_pnl"]} dd={detail["max_drawdown"]} closed={detail["closed_trades"]}',flush=True)
sample_trades = [dict(direction=x['direction'],slippage_ticks=x['slippage_ticks'],trade=t)
    for x in results for t in x['trades'] if t['entry_price']==5568 and t['entry_bar'].startswith('2026-03-20')]
output = dict(passed=True,timestamp=datetime.now().astimezone().isoformat(),results=results,sample_trades=sample_trades,
    original_fee_verification='fee-aware-verification-20261009.json',
    scope='Existing long/short main parameters only; separate accounts; no new matrix optimization',
    execution='Prior completed-bar protective order, next-bar intrabar touch; adverse open gap and slippage; EMA close-confirmed next-open exit; entry bar protection excluded',
    hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in
            [ROOT/'src/Qh.Core/TradeReplayRunner.cs',ROOT/'worker/qh_signal_worker.py',ROOT/'strategies/ema_dynamic_long_trade.py',ROOT/'strategies/ema_dynamic_short_trade.py']})
(OUT/'protective-stop-replay-20261009.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf8')
print(f'PASS {len(sample_trades)} replays of the referenced entry-bar case; old runs retained.')
