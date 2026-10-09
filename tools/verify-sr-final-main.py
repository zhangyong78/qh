"""Freeze current default presets and independently reconcile six official replays."""
import hashlib,json,runpy,subprocess,re
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
DATA=Path('D:/mycode/qh/artifacts/sr2701-15m/wenhua-aligned-data')
OUT=DATA/'multiside-research-20261008'
H=runpy.run_path(str(ROOT/'tools/run-sr-ema-short-comparison.py'))
R=runpy.run_path(str(ROOT/'tools/run-sr-short-research.py'))
E=runpy.run_path(str(ROOT/'tools/build-sr-multiside-report.py'))['extra']
BASE=json.loads((OUT/'selected-results.json').read_text(encoding='utf8'))
prep=R['prepare'](DATA,'15m');results=[]
for direction in ('long','short'):
    path=ROOT/'strategies'/f'ema_dynamic_{direction}_trade.py'
    defaults={k:v['default'] for k,v in runpy.run_path(str(path))['STRATEGY']['parameters'].items()}
    parameters=json.loads((OUT/f'{direction}-final-parameters.json').read_text(encoding='utf8'))
    assert defaults==parameters,(direction,'strategy defaults not final')
    for slip in (0,1,2):
        cmd=['dotnet',str(H['CLI']),'trade-replay',str(DATA),prep[0],str(path),H['START'],H['END'],
            '--initial-equity=100000','--lots=1','--multiplier=10','--min-tick=1','--margin-rate=0.1',
            '--fee-mode=Fixed','--open-fee=2','--today-fee=3','--ordinary-fee=1',f'--slippage-ticks={slip}',
            *[f'{k}={v}' for k,v in parameters.items()]]
        process=subprocess.run(cmd,cwd=ROOT,capture_output=True,text=True,encoding='utf8',timeout=180)
        assert process.returncode==0,process.stdout+process.stderr
        rid=re.search(r'run_id=([^;\s]+)',process.stdout)[1]
        record=json.loads((DATA/'trade-runs'/rid/'run.json').read_text(encoding='utf-8-sig'));assert record['Status']=='completed'
        item=dict(direction=direction,entry_ema=55,slippage_ticks=slip,run_id=rid,record=record)
        result=E(H['summarize'](item,DATA,prep[2],prep[3]),rid)
        result.update(label='多头主选' if direction=='long' else '空头主选',role='main',parameters=parameters)
        if slip in (0,1):
            baseline=next(x for x in BASE if x['direction']==direction and x['role']=='main' and x['slippage_ticks']==slip)
            for filename in ('fills.jsonl','closed-trades.jsonl','equity.jsonl'):
                def normalized(run):return [{k:v for k,v in row.items() if k!='RunId'} for row in H['read_jsonl'](DATA/'trade-runs'/run/filename)]
                assert normalized(rid)==normalized(baseline['run_id']),(direction,slip,filename)
            result['baseline_match']=baseline['run_id']
        positions=H['read_jsonl'](DATA/'trade-runs'/rid/'positions.jsonl')
        result['max_initial_margin']=max((p['ReservedMargin'] for p in positions),default=0)
        for p in positions:assert p['ReservedMargin']==p['AverageEntryPrice']*p['Quantity']
        results.append(result)
        print(f'PASS {direction} slip={slip}: net={result["net_pnl"]} dd={result["max_drawdown"]} closed={result["closed_trades"]}',flush=True)
output=dict(passed=True,results=results,hashes={str(p):hashlib.sha256(p.read_bytes()).hexdigest() for p in
    [ROOT/'strategies/ema_dynamic_long_trade.py',ROOT/'strategies/ema_dynamic_short_trade.py',H['CLI'].parent/'Qh.Core.dll']})
(OUT/'final-main-replay-proof.json').write_text(json.dumps(output,ensure_ascii=False,indent=2),encoding='utf8')
print('PASS final main: exact strategy defaults; four full archived fill/trade/equity comparisons; six accounting audits; fixed margin identities.')
