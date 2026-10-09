"""Combine the original long research with the independently tested short study."""
import csv, hashlib, json, runpy
from datetime import datetime
from html import escape
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
DATA=Path('D:/mycode/qh/artifacts/sr2701-15m/wenhua-aligned-data')
OUT=DATA/'multiside-research-20261008'
R=runpy.run_path(str(ROOT/'tools/run-sr-short-research.py')); H=R['H']

def write_json(path,value): path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')
def money(v): return f'{v:,.0f}'
def table(headers,rows):
    return '<div class="scroll"><table><thead><tr>'+''.join('<th>'+escape(str(x))+'</th>' for x in headers)+'</tr></thead><tbody>'+''.join('<tr>'+''.join('<td>'+escape(str(x))+'</td>' for x in r)+'</tr>' for r in rows)+'</tbody></table></div>'

def extra(detail,run):
    eq=detail['equity']; peak=100000; peak_time=H['timestamp'](H['START']); last=100000; monthly={}; underwater=0; episode=False
    for t,v in eq:
        dt=H['timestamp'](t)
        if v<peak or episode: underwater=max(underwater,(dt-peak_time).total_seconds()/86400)
        if v>=peak: peak=v; peak_time=dt; episode=False
        else: episode=True
        monthly[t[:7]]=monthly.get(t[:7],0)+v-last; last=v
    directory=DATA/'trade-runs'/run
    equity_full=H['read_jsonl'](directory/'equity.jsonl'); max_dd=0; peak_row=None; trough_row=None; last_peak=None
    for row in equity_full:
        if last_peak is None or row['Equity']>=last_peak['Equity']: last_peak=row
        if last_peak['Equity']-row['Equity']>max_dd:
            max_dd=last_peak['Equity']-row['Equity']; peak_row=last_peak; trough_row=row
    intents={x['Sequence']:x for x in H['read_jsonl'](directory/'intents.jsonl')}
    fills=H['read_jsonl'](directory/'fills.jsonl'); closed=H['read_jsonl'](directory/'closed-trades.jsonl')
    by_seq={x['FillSequence']:x for x in fills}; streak=longest=0
    for trade,out in zip(closed,detail['trades']):
        intent=intents[by_seq[trade['ExitFillSequence']]['IntentSequence']]
        out['reason']=intent['Description']; streak=streak+1 if trade['NetPnl']<0 else 0; longest=max(longest,streak)
    detail.update(monthly_equity=monthly,longest_underwater_days=underwater,max_loss_streak=longest,
                  return_drawdown=detail['net_pnl']/detail['max_drawdown'] if detail['max_drawdown'] else 0.0,
                  ema_exit_count=sum(x['reason'].startswith('盈利达到') for x in detail['trades']))
    if peak_row is not None:
        realized=lambda row: float(row['RealizedGrossPnl'])-float(row['Fees'])
        detail['drawdown_decomposition']=dict(peak=peak_row['BarEnd'],trough=trough_row['BarEnd'],
            realized_change=realized(peak_row)-realized(trough_row),floating_change=float(peak_row['FloatingPnl'])-float(trough_row['FloatingPnl']))
    return detail

def main():
    if (OUT/'final-main-release.json').exists():
        runpy.run_path(str(ROOT/'tools/finalize-sr-multiside-report.py'))['main']()
        return
    progress=json.loads((OUT/'short-progress.json').read_text(encoding='utf-8'))
    completed=progress['completed']; decision=json.loads((OUT/'decision.json').read_text(encoding='utf-8'))
    prep=R['prepare'](DATA,'15m')
    old=json.loads((DATA/'atr14-matrix-20261008/exit-entry-study-20261008/progress.json').read_text(encoding='utf-8'))
    long_done=old['completed']; long_done={x['key']:x for x in long_done} if isinstance(long_done,list) else long_done
    selections=[]
    for c in decision['candidates']:
        if c['direction']=='long':
            item=long_done[c['key']]
            base=dict(direction='long',entry_ema=55,slippage_ticks=item['slip'],run_id=item['run_id'],record=item['record'])
            d=H['summarize'](base,DATA,prep[2],prep[3]); d=extra(d,item['run_id'])
            params=item['record']['Parameters']
        else:
            item=completed[c['key']]
            d=json.loads((OUT/'details'/f"{item['run_id']}.json").read_text(encoding='utf-8'))
            d=extra(d,item['run_id']); params=item['parameters']
        d.update(label=c['label'],role=c['role'],selection_key=c['key'],parameters=params)
        selections.append(d)
    for direction in ('long','short'):
        chosen=next(x for x in selections if x['direction']==direction and x['role']=='main' and x['slippage_ticks']==1)
        write_json(OUT/f'{direction}-final-parameters.json',chosen['parameters'])
    short21=next(x for x in selections if x['direction']=='short' and x['entry_ema']==21 and x['slippage_ticks']==1)
    write_json(OUT/'short-ema21-alternative-parameters.json',short21['parameters'])
    write_json(OUT/'selected-results.json',selections)
    short_metrics=[dict({k:x[k] for k in ('stage','key','entry_ema','atr_multiplier','profile','lock_r','entries_limit','exit','slippage_ticks','timeframe')},**x['metrics']) for x in completed.values() if x.get('direction')=='short']
    write_json(OUT/'matrix-index.json',short_metrics)
    pressure=[x for x in selections if x['slippage_ticks']==1]
    metrics=table(['方案','净收益','最大回撤','收益/回撤','平仓数','胜率%','PF','前三笔盈利占比%','扣前三笔后净利','最长水下天数','最大单亏','超预算笔数'],
        [[x['label'],money(x['net_pnl']),money(x['max_drawdown']),f"{x['return_drawdown']:.2f}",x['closed_trades'],f"{x['win_rate']:.1f}",f"{x['profit_factor']:.2f}",f"{x['top3_winner_share']:.1f}",money(x['net_excluding_top3']),f"{x['longest_underwater_days']:.1f}",money(abs(x['max_loss'])),x['risk_over_count']] for x in pressure])
    monthly=table(['月份']+[x['label'] for x in pressure],[[m]+[money(x['monthly_equity'].get(m,0)) for x in pressure] for m in [f'2026-{i:02}' for i in range(2,10)]])
    audit=[]
    for x in pressure:
        if x['role'] not in ('main','alternative'): continue
        audit.append('<details><summary>'+escape(x['label'])+' · 逐笔交易与平仓原因</summary>'+table(['开仓K线起点','平仓K线起点','手数','开仓价','平仓价','净损益','净R','退出依据'],[[t['entry_bar'],t['exit_bar'],t['quantity'],t['entry_price'],t['exit_price'],money(t['net_pnl']),f"{t['r']:.2f}",t['reason']] for t in x['trades']])+'</details>')
    records=table(['方案','滑点','run ID','已平仓净利','期末浮盈','未平开仓费','期末手数','净利核对'],[[x['label'],x['slippage_ticks'],x['run_id'],money(x['closed_net_pnl']),money(x['end_open_pnl']),money(x['live_entry_fees']),x['end_open_quantity'],money(x['net_pnl'])] for x in selections])
    logs=table(['记录时间','阶段','状态','数量'],[[x['time'],x['stage'],x['state'],x.get('runs',x.get('requested',''))] for x in progress['log']])
    hashes=table(['文件','SHA256'],[[str(p.relative_to(ROOT)),hashlib.sha256(p.read_bytes()).hexdigest()] for p in [ROOT/'strategies/ema_dynamic_long_trade.py',ROOT/'strategies/ema_dynamic_short_trade.py',ROOT/'tools/run-sr-short-research.py']])
    transfer=[x for x in completed.values() if x['stage']=='transfer']
    transfer_table=table(['方向','周期','挂单EMA','ATR倍数','锁盈起点','每波上限','EMA退出','滑点','净利','回撤','run ID'],[[x['direction'],x['timeframe'],x['entry_ema'],x['atr_multiplier'],x['lock_r'],x['entries_limit'],x['exit'],x['slippage_ticks'],money(x['metrics']['net_pnl']),money(x['metrics']['max_drawdown']),x['run_id']] for x in transfer])
    neighbors=[x for x in completed.values() if x['stage']=='neighbors' and x['slippage_ticks']==1]
    neighbors_table=table(['挂单EMA','ATR倍数','锁盈起点','每波上限','EMA退出','净利','回撤','收益/回撤','扣前三笔后净利'],[[x['entry_ema'],x['atr_multiplier'],x['lock_r'],x['entries_limit'],x['exit'],money(x['metrics']['net_pnl']),money(x['metrics']['max_drawdown']),f"{x['metrics']['return_drawdown']:.2f}",money(x['metrics']['net_excluding_top3'])] for x in neighbors])
    body=r'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖多空投研 · 研发日志与最终选择</title><style>
    *{box-sizing:border-box}body{margin:0;color:#20364b;background:#eef3f8;font:15px/1.7 "Microsoft YaHei",sans-serif}header{background:#12334f;color:#fff;padding:32px max(22px,calc((100vw - 1300px)/2))}h1{font-size:28px;margin:0}main{max-width:1300px;margin:auto;padding:22px}section{background:#fff;border:1px solid #d8e2ec;border-radius:12px;padding:23px;margin-bottom:20px}h2{font-size:21px;margin-top:0}h3{font-size:17px}.callout{padding:15px;border-left:4px solid #2b6cb0;background:#edf5ff}.note{font-size:13px;color:#617488}nav{display:flex;gap:20px;flex-wrap:wrap;margin-top:15px}nav a{color:#d9efff}a{color:#1764a0}.scroll{overflow:auto;max-height:550px}table{width:100%;border-collapse:collapse;font-size:13px}th,td{padding:9px;text-align:right;white-space:nowrap;border-bottom:1px solid #e1e8f0}th{position:sticky;top:0;background:#eef4fb}td:first-child,th:first-child{text-align:left}select,input,button{padding:8px;margin:5px;border:1px solid #abc0d5;border-radius:5px;background:white}svg{width:100%;height:360px}.legend{display:flex;gap:16px;flex-wrap:wrap}summary{cursor:pointer;font-weight:600;padding:12px 0}pre{overflow:auto;background:#f3f6fa;padding:14px;font-size:12px}td.negative{color:#b84242}th[data-sort]{cursor:pointer}.grid{display:grid;grid-template-columns:1fr 1fr;gap:20px}@media(max-width:800px){.grid{grid-template-columns:1fr}main{padding:12px}section{padding:15px}}
    </style></head><body><header><h1>白糖 SR701 · 多空策略投研报告</h1><p>15分钟为选型主体 · 本金10万元 · 单笔风险预算1,000元 · 研发历程、交易分析、资金曲线与可复现结论</p><nav><a href="#choice">最终选择</a><a href="#history">多头历程</a><a href="#short">空头研究</a><a href="#curve">资金曲线</a><a href="#rules">完整条件</a><a href="#matrix">矩阵查询</a><a href="#log">研究日志</a></nav></header><main>
    <section id="choice"><h2>最终选择与依据</h2>__DECISION__<p>选择顺序：先检查净收益与回撤，再看盈利集中度、最长水下期、相邻参数、每波次数和滑点敏感性。收益/回撤是诊断比值，不是年化收益或夏普比率。</p>__METRICS__<p class="note">以下所有多空账户分别使用10万元，未进行共同持仓、统一保证金或资金分配的组合回测，不能把净利简单相加当成组合结果。“扣前三笔”仅做集中度诊断，未删除后重新回放，不是实际可实现的收益。</p></section>
    <section id="history"><h2>「1.15分钟做多」研发历程与最终结果</h2><ol><li><b>修复研究入口与数据口径。</b>统一SDK入口后桌面重启曾回到TEST·1分钟；入口改到工作区白糖快照目录，15/30/60分钟可复用。审查夜盘归属、休市区间、短尾K线以及跨休市跳空；休市不填虚拟K线，ATR保留真实前收价差。</li><li><b>迁移QQOKX的EMA限价做多。</b>EMA21＞EMA55且收盘＞EMA55时，在EMA55挂多单；未成交逐根重挂。明确以损定量，风险预算不足一手时跳过。用户从500/1000风险金比较中确定本轮用1000元，本金10万元。</li><li><b>对齐软件与脚本回测。</b>曾出现软件显示2,212元与研究结果不同的问题；将区间、策略参数、仓位、手续费和成交时序统一到正式回放器，以保存run为复现依据。早期结果不与后续冻结区间混算。</li><li><b>分解大回撤。</b>旧保护方案的6,824元回撤分解中，浮盈回吐约3,900元，占约57%；另外有已实现亏损。保护触发后等下一根开盘以及夜盘跳空，使部分实际亏损超过1000元预算。因此收紧止损并不能消除成交延迟风险。</li><li><b>ATR14与保护规则交叉：60次回放。</b>15/30/60分钟，ATR14×1/1.5/2，A/B/C三套保护加ATR10旧基准，分别测0/1tick。中间选择15分钟ATR14×2+C：1tick净利13,791、回撤4,202。ATR×1.5旧保护虽赚16,586，回撤7,015，前三笔占57.9%，扣前三笔只剩852元，未选作主线。</li><li><b>锁盈、再入场和均线退出：448次回放。</b>锁盈起点3/4/5/6/8/10/12/15R，每波1/2/3/不限，无均线退出或EMA21/55在1/2/3R启用，分别测0/1tick。最终主选4R起锁、每波1次、3R启用EMA55退出：净利15,041、回撤4,202、25笔平仓、PF2.66。相比中间基准，25笔同入场对照中3笔改善、2笔变差、20笔相同，只有2笔由EMA退出。</li></ol>
    <p>多头备选6R起锁、每波1次、3R启用EMA21退出：净利17,603、回撤4,802，但前三笔占52.2%、最长水下130天；比原基准增加3,812元，其中一笔改善4,572元，其他配对交易合计反而−760元。无EMA退出的15R方案赚19,342元，但回撤8,261、前三笔占93.6%、扣前三笔后−6,823元，不能仅因最高盈利而选择。</p>
    <p><a href="../atr14-matrix-20261008/atr14-matrix-report.html">多头ATR原始报告</a> · <a href="../atr14-matrix-20261008/exit-entry-study-20261008/exit-entry-study-report.html">多头448组深度报告</a> · <a href="../short-ema-comparison-20261008/short-ema-report.html">空头初始EMA21/55对照</a></p></section>
    <section id="short"><h2>空头如何沿用研发方法，而不是直接复制结论</h2><p>空头入场反向为EMA21＜EMA55且收盘＜EMA55，分别比较EMA21与EMA55卖出限价；独立检查ATR和保护、锁盈起点、每波成交次数以及盈利后的EMA退出。原始空头基准：EMA21净利7,917/回撤6,104；EMA55净利7,212/回撤4,625。EMA55期末还有5手、浮亏450元，已平仓净收益7,672减浮亏450及未平开仓费10才得到7,212，不能只比较已平仓收益。</p>
    <p>第一阶段36组：两种挂单×三种ATR倍数×三种保护×两种滑点。旧阶梯EMA21×2赚9,738，但回撤6,856、前三笔占79.5%、扣前三笔后−6,349。EMA55×1.5+C赚8,404、回撤4,601，是值得继续检验的邻近候选。第二阶段896组：两种挂单各完整复制多头448组研究结构，固定ATR14×2；与第一阶段重复4组，实际新增892次。之后针对入围方案补测ATR及锁盈邻域、30/60分钟迁移和2tick压力。</p>__SHORT_ANALYSIS__
    <h3>邻近参数 · 1tick</h3>__NEIGHBORS__<p class="note">参数扫描均使用同一段历史，896组不是896个独立样本。时间分段为同样本的稳定性诊断；本轮没有独立未来验证或真正样本外测试。</p></section>
    <section id="curve"><h2>资金曲线与权益回撤</h2><label>成本情景<select id="slip"><option value="1">1tick</option><option value="0">0tick</option></select></label><div class="legend" id="legend"></div><svg id="equity" viewBox="0 0 1150 360" aria-label="独立账户资金曲线"></svg><div id="hover" class="note">移动鼠标查看同一根K线的权益。</div><h3>从历史最高权益回撤金额</h3><svg id="drawdown" viewBox="0 0 1150 360" aria-label="权益回撤金额"></svg><h3>每月账户权益变化 · 1tick</h3>__MONTHLY__<p class="note">月度含浮盈浮亏及已经扣除费用，八个月合计等于账户净利；与按平仓月份归集的交易净利不同。最长水下期从最近一次权益新高到恢复或期末，未恢复的尾部区间也计入。</p></section>
    <section id="rules"><h2>最终多空开平条件与以损定量</h2>__RULES__<p>共同执行顺序：新仓成交所在K线不管理；之后先检查此前已生效保护触及，再检查此前启用的EMA退出，最后才更新保护价及启用状态。旧保护优先，新规则从下一根生效。触及保护并非立即按保护价成交，平仓意图统一在下一根有效K线开盘成交。</p><p>风险手数=floor[1000÷(计划止损距离×10+25)]，不足一手跳过；不强制最少一手。计划止损按挂单价和信号ATR计算，成交后初始R按实际成交价重新取整并冻结。每手成本预留25元只用于计算手数；实际开仓2元/手、平今3元/手、平昨1元/手。F=1点是固定手续费偏移，并不自动随费率调整，实际保本受跳空、费用及延迟影响。</p>
    <p>保护C：达到2R先移到E±F；n≥锁盈起点时移到E±(n−1)R±F；做多只上移，做空只下移。盈利触发均包含F：多头最高价≥E+nR+F，空头最低价≤E−nR−F。EMA退出一旦启用保持到平仓：多头收盘严格低于退出EMA，空头收盘严格高于退出EMA。</p><p><a href="long-final-parameters.json">最终多头参数</a> · <a href="short-final-parameters.json">最终空头参数</a> · <a href="decision.json">选型记录</a> · <a href="selected-results.json">完整候选结果</a></p></section>
    <section><h2>30/60分钟参数迁移检查</h2>__TRANSFER__<p class="note">这些是15分钟所选规则直接迁移，并未在30/60分钟做完整优化，不能将其称为这些周期的最终参数。当前库没有日线快照，日线未回测。夜盘和停盘已按快照交易时段对齐，30/60分钟短尾K线不跨休市拼接；交易日历由本地快照推导，并非供应商官方日历。</p></section>
    <section id="matrix"><h2>全部空头矩阵查询</h2><label>周期<select id="tf"><option value="">全部</option><option>15m</option><option>30m</option><option>60m</option></select></label><label>挂单<select id="entry"><option value="">全部</option><option>21</option><option>55</option></select></label><label>滑点<select id="cost"><option value="1">1tick</option><option value="0">0tick</option><option value="2">2tick</option></select></label><label>搜索<input id="search" placeholder="如 ema21_r3"></label><p id="count" class="note"></p><div class="scroll"><table><thead><tr id="matrix-head"></tr></thead><tbody id="matrix-body"></tbody></table></div><p class="note">点击列标题排序。结果按唯一参数键去重，阶段标签代表首次跑入该库的阶段；完整参数与run ID保存在short-progress.json，细节文件存放details目录。</p></section>
    <section><h2>交易与账户核对</h2>__AUDIT____RECORDS__<p class="note">入场时间标注成交K线起点；限价触及的根内精确时刻不可知。K线OHLC不能确定根内高低顺序。账户净利=已平仓净利+末仓浮盈亏−未平仓已付开仓费；区间末不强平，尚未发生的平仓费未扣。</p></section>
    <section id="log"><h2>研究日志与复现依据</h2><p>研究截至 __GENERATED__。冻结正式区间：2026-02-02 21:00（含）—2026-10-01 00:00（不含），15分钟正式3640根。指标使用完整前史预热至少55根，本金100000、风险1000、手数倍率1、乘数10、tick1、保证金10%。开平手续费如上，正式比较以1tick为主，0tick作成本对照，2tick作候选压力测试。</p>__LOGS____HASHES__<p><a href="short-progress.json">全部回放记录和执行日志</a> · <a href="atr-manifest.json">ATR阶段数据哈希</a> · <a href="deep-manifest.json">深度阶段数据哈希</a> · <a href="matrix-index.json">矩阵指标JSON</a></p><p>建议下一阶段先锁定本报告规则，再取得后续独立行情进行不改参数验证，并重点记录限价成交质量、夜盘跳空和保护确认到实际成交的价差。对保护触及即时成交的研究须有更细粒度数据，不能用当前15分钟OHLC直接推定。</p></section></main>
    <script>const curves=__CURVES__,matrix=__MATRIX__;const palette=['#215da8','#c63e5b','#15917d','#9865c7','#bd7e1d','#777'];const fmt=v=>Number(v).toLocaleString('zh-CN',{maximumFractionDigits:2});
    function draw(id,series){const svg=document.getElementById(id),W=1150,H=360,L=85,R=20,T=20,B=35;const vals=series.flatMap(s=>s.points.map(p=>p[1]));let lo=id==='drawdown'?0:Math.min(100000,...vals),hi=Math.max(...vals);let pad=(hi-lo)*.08||1;if(id!=='drawdown')lo-=pad;hi+=pad;let x=i=>L+i*(W-L-R)/(series[0].points.length-1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo),out=[];for(let n=0;n<5;n++){let v=lo+(hi-lo)*n/4,yy=y(v),i=Math.round((series[0].points.length-1)*n/4);out.push(`<line x1="${L}" x2="${W-R}" y1="${yy}" y2="${yy}" stroke="#e2eaf2"/><text x="${L-7}" y="${yy+4}" text-anchor="end" font-size="12" fill="#647a90">${fmt(Math.round(v))}</text><text x="${x(i)}" y="${H-9}" text-anchor="middle" font-size="12" fill="#647a90">${series[0].points[i][0].slice(0,10)}</text>`)}for(let s of series)out.push(`<path d="${s.points.map((p,i)=>(i?'L':'M')+x(i).toFixed(2)+','+y(p[1]).toFixed(2)).join(' ')}" fill="none" stroke="${s.color}" stroke-width="2"/>`);svg.innerHTML=out.join('');if(id==='equity')svg.onmousemove=e=>{let rect=svg.getBoundingClientRect(),i=Math.round(((e.clientX-rect.left)/rect.width*W-L)/(W-L-R)*(series[0].points.length-1));i=Math.max(0,Math.min(i,series[0].points.length-1));document.getElementById('hover').textContent=series[0].points[i][0]+' · '+series.map(s=>s.label+' '+fmt(s.points[i][1])).join(' / ')}}
    function redraw(){let picked=curves.filter(r=>r.slippage_ticks===Number(document.getElementById('slip').value)),series=picked.map((r,i)=>({label:r.label,color:palette[i%palette.length],points:r.equity,row:r}));document.getElementById('legend').innerHTML=series.map(s=>`<span style="color:${s.color}">${s.label} · ${fmt(s.row.net_pnl)}</span>`).join('');draw('equity',series);draw('drawdown',series.map(s=>{let peak=100000;return {...s,points:s.points.map(([t,v])=>{peak=Math.max(peak,v);return[t,peak-v]})}}))}
    const columns=[['timeframe','周期'],['entry_ema','挂单EMA'],['atr_multiplier','ATR倍数'],['profile','保护'],['lock_r','锁盈R'],['entries_limit','每波上限(0不限)'],['exit','EMA退出'],['net_pnl','净利'],['max_drawdown','回撤'],['return_drawdown','收益/回撤'],['closed_trades','平仓数'],['top3_winner_share','前三笔%'],['net_excluding_top3','扣前三笔后'],['longest_underwater_days','水下天数'],['ema_exit_count','EMA退出数'],['run_id','run ID']];let sortKey='return_drawdown',descending=true;
    document.getElementById('matrix-head').innerHTML=columns.map(([k,t])=>`<th data-sort="${k}">${t}</th>`).join('');function renderMatrix(){let tf=document.getElementById('tf').value,entry=document.getElementById('entry').value,cost=+document.getElementById('cost').value,q=document.getElementById('search').value.toLowerCase();let rows=matrix.filter(r=>(!tf||r.timeframe===tf)&&(!entry||r.entry_ema===+entry)&&r.slippage_ticks===cost&&(!q||JSON.stringify(r).toLowerCase().includes(q)));rows.sort((a,b)=>{let av=a[sortKey],bv=b[sortKey],c=typeof av==='number'?av-bv:String(av).localeCompare(String(bv));return descending?-c:c});document.getElementById('count').textContent=`当前 ${rows.length} 组 / 唯一回放 ${matrix.length} 组`;let body=document.getElementById('matrix-body');body.replaceChildren();for(let r of rows){let tr=document.createElement('tr');for(let[k]of columns){let td=document.createElement('td'),v=r[k];td.textContent=v==null?'—':typeof v==='number'?fmt(v):v;if(typeof v==='number'&&v<0)td.className='negative';tr.appendChild(td)}body.appendChild(tr)}}
    for(let id of ['tf','entry','cost','search'])document.getElementById(id).addEventListener('input',renderMatrix);for(let th of document.querySelectorAll('[data-sort]'))th.onclick=()=>{descending=sortKey===th.dataset.sort?!descending:true;sortKey=th.dataset.sort;renderMatrix()};document.getElementById('slip').addEventListener('change',redraw);redraw();renderMatrix();</script></body></html>'''
    rules=table(['项目','多头主选','空头主选'],decision['rules'])
    substitutions={'DECISION':decision['conclusion_html'],'METRICS':metrics,'SHORT_ANALYSIS':decision['analysis_html'],
        'NEIGHBORS':neighbors_table,'MONTHLY':monthly,'RULES':rules,'TRANSFER':transfer_table,'AUDIT':''.join(audit),'RECORDS':records,'LOGS':logs,'HASHES':hashes,
        'GENERATED':datetime.now().astimezone().isoformat(),'CURVES':json.dumps(selections,ensure_ascii=False,separators=(',',':')),
        'MATRIX':json.dumps(short_metrics,ensure_ascii=False,separators=(',',':'))}
    for key,value in substitutions.items(): body=body.replace('__'+key+'__',value)
    ui_ids=json.loads((OUT/'ui-final-runs.json').read_text(encoding='utf-8')) if (OUT/'ui-final-runs.json').exists() else {}
    appendix='<section><h2>软件复核入口与方法来源</h2><p>回测页“15分钟投研最终选择”可直接打开多头主选、空头EMA55主选、空头EMA21备选及本报告。开平条件随实际参数更新；修改后未重跑会提示当前图中还是保存回测。</p>'
    appendix+=table(['界面入口','保存run ID'],list(ui_ids.items()))
    appendix+='<p><a href="ui-verification.json">界面验证记录</a> · <a href="ui-final-runs.json">软件最终run清单</a> · <a href="frozen-short-strategy.py">研究时冻结的空头策略</a> · <a href="short-ema21-alternative-parameters.json">空头EMA21备选参数</a> · <a href="../atr14-matrix-20261008/progress.json">多头ATR阶段日志</a> · <a href="../atr14-matrix-20261008/exit-entry-study-20261008/progress.json">多头深度阶段日志</a></p>'
    appendix+='<p class="note">本轮手续费2/3/1元和保证金10%均是保存的模拟假设，未当作当前经纪商报价。交易单位10吨/手、最小变动1元/吨可核对<a href="https://www.czce.com.cn/cn/uploadfile/2024/06/26/20240626165544343.pdf">郑商所白糖业务细则</a>。在同段历史上比较大量策略存在选择偏差，参见作者原文<a href="https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf">The Probability of Backtest Overfitting</a>；本轮未估计PBO，也未声称通过样本外验证。</p></section>'
    body=body.replace('</main>',appendix+'</main>')
    assert '__DECISION__' not in body
    (OUT/'白糖多空投研定稿.html').write_text(body,encoding='utf-8')
    (OUT/'multiside-investment-report.html').write_text('<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=白糖多空投研定稿.html"><title>报告已更名</title><p>报告已更名：<a href="白糖多空投研定稿.html">打开白糖多空投研定稿</a></p></html>',encoding='utf-8')
    # A short-specific report uses the same audited evidence and links to the combined one.
    short=body.replace('白糖 SR701 · 多空策略投研报告','白糖 SR701 · 空头参数深度研究').replace('<section id="history">','<section id="history" style="display:none">')
    backup=OUT/'bak';backup.mkdir(exist_ok=True)
    short=short.replace('<head>','<head><base href="../">',1)
    (backup/'short-depth-report.html').write_text(short,encoding='utf-8')
    print('Reports generated:',OUT)

if __name__=='__main__': main()
