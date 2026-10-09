"""Add a per-trade actual R cash amount to the retained corrected-stop runs."""
import json
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
DATA=ROOT/'artifacts/sr2701-15m/wenhua-aligned-data'
OUT=DATA/'multiside-research-20261008'
proof_path=OUT/'protective-stop-replay-20261009.json'
proof=json.loads(proof_path.read_text(encoding='utf8'))
audits=[]
for result in proof['results']:
    run_dir=DATA/'trade-runs'/result['run_id']
    record=json.loads((run_dir/'run.json').read_text(encoding='utf-8-sig'))
    fills=[json.loads(line) for line in (run_dir/'fills.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    intents=[json.loads(line) for line in (run_dir/'intents.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    trades=[json.loads(line) for line in (run_dir/'closed-trades.jsonl').read_text(encoding='utf-8-sig').splitlines()]
    by_fill={f['FillSequence']:f for f in fills}
    trade_rows=[]
    for summary,trade in zip(result['trades'],trades,strict=True):
        entry=by_fill[trade['EntryFillSequence']];exit_fill=by_fill[trade['ExitFillSequence']]
        initial=next((i for i in intents if i['Action']=='protect' and i['Direction']==trade['Direction']
            and i['SignalBarEnd']>=entry['BarEnd'] and i['SignalBarEnd']<=exit_fill['BarEnd']
            and i['Description'].startswith('初始ATR') and i['ReferencePrice'] is not None),None)
        if initial is None:
            initial_r=None;net_r=None;stop=None
        else:
            stop=float(initial['ReferencePrice'])
            initial_r=abs(float(entry['Price'])-stop)*int(trade['Quantity'])*float(record['Config']['Multiplier'])
            net_r=float(trade['NetPnl'])/initial_r if initial_r else None
        summary['initial_stop_price']=stop
        summary['risk_amount']=initial_r
        summary['r']=net_r
        trade_rows.append({'entry_fill_sequence':trade['EntryFillSequence'],'exit_fill_sequence':trade['ExitFillSequence'],
            'entry_price':trade['EntryPrice'],'exit_price':trade['ExitPrice'],'quantity':trade['Quantity'],
            'initial_stop_price':stop,'risk_amount':initial_r,'gross_pnl':trade['GrossPnl'],
            'fees':trade['Fees'],'net_pnl':trade['NetPnl'],'net_r':net_r})
    audits.append({'side':result['direction'],'run_id':result['run_id'],'slippage_ticks':result['slippage_ticks'],
        'parameter_budget':result['parameters']['risk_budget'],'trades':trade_rows})
proof_path.write_text(json.dumps(proof,ensure_ascii=False,indent=2),encoding='utf8')
case=next(t for run in audits if run['side']=='long' and run['slippage_ticks']==1 for t in run['trades']
    if t['entry_price']==5328 and t['exit_price']==5389 and t['quantity']==5)
assert case['initial_stop_price']==5313 and case['risk_amount']==750 and case['gross_pnl']==3050 and case['fees']==15 and case['net_pnl']==3035
(OUT/'per-trade-r-audit-20261009.json').write_text(json.dumps({'definition':'1R cash = abs(actual entry fill − initial ATR stop price) × actual lots × contract multiplier; net R = realized net PnL / 1R cash. Fees and slippage remain included in net PnL, not in the denominator.', 'case':case,'runs':audits},ensure_ascii=False,indent=2),encoding='utf8')
print(f"PASS: per-trade R audit; screenshot trade = 1R {case['risk_amount']}, gross {case['gross_pnl']}, fees {case['fees']}, net {case['net_pnl']} ({case['net_r']:.4f}R).")
