"""Reproducible short research using the same official replay as the long study."""
from __future__ import annotations
import argparse, csv, hashlib, json, re, runpy, sqlite3, subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timedelta
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
H = runpy.run_path(str(ROOT / 'tools/run-sr-ema-short-comparison.py'))
CLI = H['CLI']; START = H['START']; END = H['END']
EXITS = [('none', 0, 2)] + [(f'ema{p}_r{r}', p, r) for p in (21,55) for r in (1,2,3)]
LOCKS = (3,4,5,6,8,10,12,15)

def config(stage, tf='15m', entry=55, atr=2, profile='two_r_n_minus_one', lock=3, cap=1, exit='none', slip=1):
    _, ep, activation = next(x for x in EXITS if x[0] == exit)
    params = dict(H['BASE_PARAMS'], entry_ema_period=entry, atr_stop_multiplier=atr,
                  protection_variant=profile, profit_lock_start_r=lock, max_entries_per_trend=cap,
                  ema_exit_period=ep, ema_exit_activation_r=activation)
    return dict(stage=stage, timeframe=tf, entry_ema=entry, atr_multiplier=atr, profile=profile,
                lock_r=lock, entries_limit=cap, exit=exit, slippage_ticks=slip, parameters=params,
                key=f'{tf}|{entry}|{atr}|{profile}|{lock}|{cap}|{exit}|{slip}')

def prepare(data, tf):
    snapshot = H['HELPERS']['TIMEFRAMES'][tf]
    with sqlite3.connect(data / 'index.sqlite') as db:
        source = Path(db.execute('SELECT source_path FROM snapshots WHERE snapshot_id=?',(snapshot,)).fetchone()[0])
    if not source.is_absolute(): source = Path('D:/mycode/qh') / source
    bars = list(csv.DictReader(source.open(encoding='utf-8-sig', newline='')))
    calendar = json.loads((data / 'calendar.json').read_text(encoding='utf-8-sig'))
    ranges = next(v for v in calendar.values() if isinstance(v,list) and v and isinstance(v[0],dict) and 'start' in v[0])
    sessions = [(H['timestamp'](s['start']),H['timestamp'](s['end'])) for s in ranges]
    by_end = {}
    for bar in bars:
        start = H['timestamp'](bar['timestamp']); nominal = start + timedelta(minutes=int(tf[:-1]))
        end = next((min(nominal,b) for a,b in sessions if a<=start<b), nominal)
        by_end[end] = bar
    return snapshot, source, H['HELPERS']['load_atr_map'](source,14), by_end

def execute(c, data, out, prepared, short_strategy=None):
    direction=c.get('direction','short')
    strategy=short_strategy if direction=='short' and short_strategy else ROOT/f'strategies/ema_dynamic_{direction}_trade.py'
    command = ['dotnet',str(CLI),'trade-replay',str(data),prepared[0],str(strategy),START,END,
               '--initial-equity=100000','--lots=1','--multiplier=10','--min-tick=1','--margin-rate=0.1',
               '--fee-mode=Fixed','--open-fee=2','--today-fee=3','--ordinary-fee=1',f"--slippage-ticks={c['slippage_ticks']}",
               *[f'{k}={v}' for k,v in c['parameters'].items()]]
    result = subprocess.run(command,cwd=ROOT,capture_output=True,text=True,encoding='utf-8',errors='replace',timeout=240)
    match = re.search(r'run_id=([^;\s]+)',result.stdout)
    if result.returncode or not match:
        raise RuntimeError(c['key']+': '+result.stdout[-1000:]+result.stderr[-1000:])
    record = json.loads((data/'trade-runs'/match[1]/'run.json').read_text(encoding='utf-8-sig'))
    if record['Status']!='completed': raise RuntimeError(str(record))
    item = dict(c,direction=direction,run_id=match[1],record=record)
    detail = H['summarize'](item,data,prepared[2],prepared[3])
    eq = detail['equity']; peak=100000; peak_time=H['timestamp'](START); underwater=0.; month={}; last=100000
    for t,v in eq:
        stamp=H['timestamp'](t)
        if v>=peak:
            underwater=max(underwater,(stamp-peak_time).total_seconds()/86400); peak=v; peak_time=stamp
        else: underwater=max(underwater,(stamp-peak_time).total_seconds()/86400)
        m=t[:7]; month[m]=month.get(m,0)+v-last; last=v
    intents={x['Sequence']:x for x in H['read_jsonl'](data/'trade-runs'/match[1]/'intents.jsonl')}
    exits=[intents[f['IntentSequence']]['Description'] for f in H['read_jsonl'](data/'trade-runs'/match[1]/'fills.jsonl') if f['Action']=='close']
    detail.update(key=c['key'], longest_underwater_days=underwater, monthly_equity=month,
                  ema_exit_count=sum(x.startswith('盈利达到') for x in exits),
                  return_drawdown=detail['net_pnl']/detail['max_drawdown'] if detail['max_drawdown'] else 0)
    (out/'details'/f"{match[1]}.json").write_text(json.dumps(detail,ensure_ascii=False),encoding='utf-8')
    scalar={k:v for k,v in detail.items() if k not in ('equity','trades')}
    return dict(item,metrics=scalar)

def main():
    ap=argparse.ArgumentParser(); ap.add_argument('--data-root',type=Path,required=True)
    ap.add_argument('--stage',choices=['atr','deep','neighbors','transfer','stress'],required=True); ap.add_argument('--workers',type=int,default=4)
    ap.add_argument('--final-config',type=Path); ap.add_argument('--output',type=Path)
    ap.add_argument('--short-strategy',type=Path); args=ap.parse_args(); data=args.data_root
    out=args.output or data/'multiside-research-20261008'; (out/'details').mkdir(parents=True,exist_ok=True)
    strategy=(args.short_strategy or ROOT/'strategies/ema_dynamic_short_trade.py').resolve()
    # Every parameter is supplied, but the frozen hash includes metadata/defaults too.
    digest=hashlib.sha256(strategy.read_bytes()+ (CLI.parent/'Qh.Core.dll').read_bytes()).hexdigest()
    checkpoint=out/'short-progress.json'
    saved=json.loads(checkpoint.read_text(encoding='utf-8')) if checkpoint.exists() else {}
    if saved and saved['fingerprint']!=digest: raise RuntimeError('Frozen strategy changed; use a new research directory.')
    done=saved.get('completed',{}); events=saved.get('log',[])
    plan=[]
    if args.stage=='atr':
        plan=[config('atr',entry=e,atr=a,profile=p,slip=s) for e in (21,55) for a in (1,1.5,2) for p in H['HELPERS']['PROFILES'] for s in (0,1)]
    elif args.stage=='deep':
        plan=[config('deep',entry=e,lock=l,cap=c,exit=x[0],slip=s) for e in (21,55) for l in LOCKS for c in (1,2,3,0) for x in EXITS for s in (0,1)]
    elif args.stage=='neighbors':
        finals=json.loads(args.final_config.read_text(encoding='utf-8'))
        plan=[config('neighbors',entry=f['entry_ema'],atr=a,lock=l,cap=f['entries_limit'],exit=f['exit'],slip=s)
              for f in finals for a in (1,1.5,2) for l in sorted({max(3,f['lock_r']-1),f['lock_r'],f['lock_r']+1}) for s in (0,1)]
        plan=list({c['key']:c for c in plan}.values())
    else:
        final=json.loads(args.final_config.read_text(encoding='utf-8'))
        plan=[config(args.stage,tf=tf,entry=f['entry_ema'],atr=f['atr_multiplier'],profile=f['profile'],lock=f['lock_r'],cap=f['entries_limit'],exit=f['exit'],slip=s)
              for tf in (('30m','60m') if args.stage=='transfer' else ('15m',)) for f in final for s in ((0,1) if args.stage=='transfer' else (2,))]
        for c,f in zip(plan,[f for tf in (('30m','60m') if args.stage=='transfer' else ('15m',)) for f in final for s in ((0,1) if args.stage=='transfer' else (2,))]):
            if f.get('direction')=='long': c.update(direction='long',key='long|'+c['key'])
    prepared={tf:prepare(data,tf) for tf in {c['timeframe'] for c in plan}}
    events.append(dict(time=datetime.now().astimezone().isoformat(),stage=args.stage,requested=len(plan),cached=sum(c['key'] in done for c in plan),state='started'))
    manifest={tf:dict(snapshot=p[0],source=str(p[1]),sha256=hashlib.sha256(p[1].read_bytes()).hexdigest(),bars=len(p[3])) for tf,p in prepared.items()}
    (out/f'{args.stage}-manifest.json').write_text(json.dumps(dict(strategy_sha256=hashlib.sha256(strategy.read_bytes()).hexdigest(),data=manifest,start=START,end=END),indent=2),encoding='utf-8')
    def save():
        temp=checkpoint.with_suffix('.tmp'); temp.write_text(json.dumps(dict(fingerprint=digest,completed=done,log=events),ensure_ascii=False),encoding='utf-8'); temp.replace(checkpoint)
    save()
    todo=[c for c in plan if c['key'] not in done]
    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        jobs={pool.submit(execute,c,data,out,prepared[c['timeframe']],strategy):c for c in todo}
        for n,future in enumerate(as_completed(jobs),1):
            item=future.result(); done[item['key']]=item; save()
            if n%20==0 or n==len(todo): print(f"{args.stage}: {n}/{len(todo)} done; last {item['key']} net={item['record']['NetPnl']} dd={item['record']['MaxDrawdown']}",flush=True)
    events.append(dict(time=datetime.now().astimezone().isoformat(),stage=args.stage,state='completed',runs=len(plan))); save()
    (out/f'{args.stage}-summary.json').write_text(json.dumps([dict(c,**{'metrics':done[c['key']]['metrics'],'run_id':done[c['key']]['run_id']}) for c in plan],ensure_ascii=False,indent=2),encoding='utf-8')
    print(f'PASS {args.stage}: {len(plan)} configurations; fills, fees, risk sizing and equity reconciled.',flush=True)

if __name__=='__main__': main()
