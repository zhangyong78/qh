"""Publish corrected current results, keeping the previous report and all logs."""
import hashlib, json, re, shutil
from html import escape
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT/'artifacts/sr2701-15m/wenhua-aligned-data/multiside-research-20261008'
REPORT = OUT/'白糖多空投研定稿.html'
ARCHIVE = OUT/'白糖多空投研定稿-保护成交修正前-20261009.html'
LEGACY_SOURCE = OUT/'白糖多空投研定稿-锁R滑点并入费用前-20261009.html'
if not LEGACY_SOURCE.exists(): shutil.copyfile(REPORT, LEGACY_SOURCE)
old = LEGACY_SOURCE.read_text(encoding='utf8')
proof = json.loads((OUT/'protective-stop-replay-20261009.json').read_text(encoding='utf8'))
ui = json.loads((OUT/'stopfix-ui-20261009/ui-verification.json').read_text(encoding='utf8'))
assert proof['passed'] and ui['passed']
rows = proof['results']; main = [r for r in rows if r['slippage_ticks']==1]
sample = next((x['trade'] for x in proof.get('sample_trades',[]) if x['direction']=='long' and x['slippage_ticks']==1),None)
def num(v): return f'{v:,.2f}' if v is not None else '—'
def table(headers, values):
    return '<div class="scroll"><table><thead><tr>'+''.join('<th>'+escape(str(x))+'</th>' for x in headers)+'</tr></thead><tbody>'+''.join('<tr>'+''.join('<td>'+escape(str(x))+'</td>' for x in row)+'</tr>' for row in values)+'</tbody></table></div>'
metrics = table(['现行配置 · 每边1tick','净收益','最大回撤','平仓笔数','胜率','PF','最大单笔净亏','期末浮盈'],
    [[r['label'],num(r['net_pnl']),num(r['max_drawdown']),r['closed_trades'],num(r['win_rate'])+'%',num(r['profit_factor']),num(r['max_loss']),num(r['end_open_pnl'])] for r in main])
compare = table(['方向','旧口径净收益','修正净收益','差额','旧回撤','修正回撤'],
    [[r['label'],num(15041 if r['direction']=='long' else 17557),num(r['net_pnl']),num(r['net_pnl']-(15041 if r['direction']=='long' else 17557)),num(4202 if r['direction']=='long' else 5171),num(r['max_drawdown'])] for r in main])
costs = table(['方向','每边滑点','净收益','最大回撤','平仓笔数','手续费'],
    [[r['label'],r['slippage_ticks'],num(r['net_pnl']),num(r['max_drawdown']),r['closed_trades'],num(r['fees'])] for r in rows])
audits = ''
for r in main:
    audits += '<details><summary>'+r['label']+' · 逐笔已平仓交易（修正口径）</summary>'
    audits += table(['方向','入场K线开始','出场K线开始','手数','入场价','成交出场价','单笔1R元','净收益','净收益R','出场原因'],
        [[r['label'],t['entry_bar'],t['exit_bar'],t['quantity'],t['entry_price'],t['exit_price'],num(t['risk_amount']),num(t['net_pnl']),num(t['r'])+'R',t['reason']] for t in r['trades']])+'</details>'
rules = table(['执行条件','现行规则'],[
    ['入场','多头EMA21>EMA55且收盘>EMA55；空头反向；按已完成K线EMA55挂下一根有效K线限价单，未触及则撤销重挂。'],
    ['仓位','本金100000元，单次风险预算1000元；手数=floor(1000/(初始止损点数×10+25))。不足1手跳过。'],
    ['初始止损','ATR14×2，按tick向风险外侧取整，以实际入场价确定1R。入场K线不回溯使用其高低价管理新仓，初始保护从下一根起生效。'],
    ['每波次数','多头最多1次；空头最多3次；反向EMA交叉重置。'],
    ['2R保护','达到2R+F后，多头保护=入场价+F；空头=入场价−F。F包含双向手续费点数与1tick滑点缓冲。锁0R表示扣除成本后的目标，不是立即平仓。'],
    ['4R起锁盈','n≥4时，多头保护=入场价+(n−1)R+F；空头=入场价−(n−1)R−F，只向盈利方向调整。'],
    ['双向费与滑点','F=双向手续费折点+设置的滑点tick数×tick点数。固定费：(开仓费+max(平今费,平昨费))/合约乘数；当前费用(2+max(3,1))/10=0.5点，再加1tick=1点，F=1.5点，保护价按有利方向取整；触发门槛按nR+F计算。比例费同样先换算双向手续费点数，再加滑点。'],
    ['盘中保护成交','仅使用上一根或更早已确定的保护价；多头最低价≤保护价，空头最高价≥保护价即在该K线撮合，不等反弹后的下一开盘。多头基准价=min(本根开盘,保护价)，空头=max(本根开盘,保护价)，再扣不利滑点。'],
    ['保护更新时序','本根已有保护优先执行；收盘后确认更高R档，新保护从下一根起有效。不能用本根高点上调后，倒用同根低点立即止盈。'],
    ['EMA退出','多头达到3R+F后启用EMA55收盘跌破退出；空头达到1R+F后启用EMA21收盘突破退出；启用从下一根起判断，确认后下一根有效K线开盘扣滑点全平。'],
    ['手续费与滑点','动态保护价的F同时预留双向手续费与设置滑点；实际成交净利仍从实际成交价差扣除真实手续费一次，并计入实际滑点。保护价偏移是成本预留，不是额外的已结算手续费。发生跳空时实际损失可能超过预留。'],
])
legacy_main = re.search(r'<main[^>]*>([\s\S]*?)</main>', old)[1]
legacy_main = re.sub(r'\bid="([^"]+)"',r'id="old-\1"',legacy_main)
legacy_main = legacy_main.replace('href="#','href="#old-')
# Keep original narrative, verification links and execution logs in-page. The
# untouched archive retains the old interactive curves and original data script.
html = '''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖多空投研定稿 · 保护成交与成本修正版</title><style>
body{font:16px/1.7 "Microsoft YaHei",sans-serif;color:#243b4b;background:#f2f5f8;margin:0}header,main{max-width:1220px;margin:auto;padding:24px}header{background:#e8f0f6}h1{margin:0}section{background:white;border:1px solid #dce5ed;border-radius:10px;margin:18px 0;padding:24px}h2{margin-top:0}a{color:#236d9d}.note{color:#596e7d}.alert{padding:16px;background:#fff3d7;border-left:5px solid #cb8828}.scroll{overflow:auto}table{border-collapse:collapse;width:100%;font-size:14px}th,td{padding:9px 12px;border-bottom:1px solid #dce5ed;text-align:left;vertical-align:top}th{background:#edf3f7;white-space:nowrap}details{margin:14px 0;padding:12px;border:1px solid #dce5ed}summary{cursor:pointer;font-weight:600}svg{width:100%;height:auto;display:block}#legend{display:flex;gap:24px;flex-wrap:wrap}pre{overflow:auto;font-size:13px;background:#f5f7f9;padding:12px}
</style></head><body><header><h1>白糖多空投研定稿 · 保护成交与成本修正版</h1><p>CZCE.SR701 · 15分钟 · 本金10万 · 单次风险1000 · 原主选参数复核 · 2026-10-09</p><nav><a href="#finding">手续费与锁R</a> · <a href="#results">现行结果</a> · <a href="#curve">资金曲线</a> · <a href="#rules">开平条件</a> · <a href="#trades">逐笔交易</a> · <a href="#ui">软件回测</a> · <a href="#log">日志与历史</a></nav></header><main>
<section id="finding"><h2>锁0R盈利741元：旧保护单成交口径确有问题</h2><p class="alert">旧结果把盘中触及5569的保护退出延后到下一根开盘，赚到了触价后的反弹。本次已修正多空撮合，并按“双向手续费+1tick”重算保护价；旧报告保留为历史记录。</p><p>3月23日21:00—21:15这根K线：开5598、高5602、低5569、收5594。此前2R锁0R保护价已是5569。旧程序在21:15确认最低价触价，再用21:15—21:30 K线开5594减1tick，成交5593。这里是<b>触价后本根反弹25点</b>，并非保护价生效后还能继续持仓获利。</p>__SAMPLE__<p>修正后同一笔3手：入场5568，保护价5569，盘中触价成交基准5569，减1tick后成交5568，毛利0元，实际开仓费6元＋平昨费3元＝9元，净亏9元。锁0R的保护价已预留预期双向手续费和1tick，结算仍按真实成交价扣实际手续费一次；tick取整和实际成交会使净利偏离0元。</p><h3>为什么5R锁4R的净利是3035元</h3><p>入场5328，初始止损5313，5手，每点每手10元，所以1R=750元，4R毛利目标是3000元。原版本将0.50点双向手续费和1tick缓冲取最大值，只加了1点；本次修正为相加1.50点，保护价由5388上调并按tick向盈利方向取整到5390。1tick不利滑点后实际成交5389，毛利3050元，实际双向手续费15元只扣一次，净利3035元，即4.0467R。多出35元来自整tick向有利方向取整；这说明保护价已经覆盖手续费和滑点预留，同时结算仍扣除真实手续费，并非重复收费。</p><p>“达到2R”只负责移动保护单；以后触及保护单才退出。本次保留收盘更新保护、下一根生效的规则，避免用一根OHLC虚构先涨后跌的顺序。</p></section>
<section id="results"><h2>修正后的现行结果</h2><p>正式区间：2026-02-02 21:00至2026-10-01 00:00（结束不含）；同一快照、同一主选参数、开平费2/3/1元，每边1tick滑点。多空各自独立账户。</p>__METRICS__<h3>与旧保护口径比较</h3>__COMPARE__<p class="alert">原ATR／次数／EMA退出矩阵仍基于旧成交口径。它们的收益和参数排名不能直接当作现行结论。本次只复核原主选，没有重新优化参数；当前主选供人工复盘使用。</p><h3>0／1／2tick成本对照</h3>__COSTS__</section>
<section id="curve"><h2>修正资金曲线与收盘权益回撤</h2><label>每边滑点 <select id="slip"><option value="0">0tick</option><option value="1" selected>1tick</option><option value="2">2tick</option></select></label><div id="legend"></div><svg id="equity" viewBox="0 0 1150 350"></svg><p id="hover" class="note">在资金曲线上移动鼠标查看权益。</p><svg id="drawdown" viewBox="0 0 1150 350"></svg><p class="note">分别展示两条独立账户权益，不相加冒充共同账户。回撤按完成K线收盘权益估值；15分钟OHLC无法复原逐tick成交和完整盘中路径，初始保护仍排除入场K线，期末不强制平仓。</p></section>
<section id="rules"><h2>当前开平条件与双向费用</h2>__RULES__<details><summary>完整冻结参数</summary><pre>__PARAMS__</pre></details></section>
<section id="trades"><h2>修正逐笔交易：出场原因对应实际成交</h2>__AUDITS__</section>
<section id="ui"><h2>回测页面与桌面入口</h2><p>两套策略已按“双向手续费+1tick”计算保护价，手续费在实际净利中只扣一次。本次重跑了原主选的多空0／1／2tick六组结果并保留每笔1R。桌面入口按发布清单载入主选保存run。</p><p>已打开的旧窗口仍是旧进程，关闭后双击桌面入口，下一次启动会加载修正后的源码和主选run。历史run保留旧配置用于审计，不会被改写。</p>__UI__<p><a href="stopfix-ui-20261009/ui-verification.json">此前界面复核JSON</a> · <a href="protective-stop-replay-20261009.json">六组新回放与逐笔证据</a> · <a href="per-trade-r-audit-20261009.json">逐笔R和费用核对</a> · <a href="final-main-release.json">当前启动与主选清单</a></p>__LAUNCHER__</section>
<section id="log"><h2>本次修改日志与完整历史研究保留</h2><ol><li>复核2R锁0R仍赚741元的旧成交口径，并修正保护单盘中触价成交。</li><li>查明锁R保护价曾以max(手续费点数,1tick)计算，少加了一项；已改成双向手续费折点+1tick，多空同步。</li><li>真实平仓手续费仍按成交单据计费一次；保护价偏移是目标净收益的成本预留，不重复记入账务。逐笔手续费、滑点与R值已复核。</li><li>重跑多空原主选0／1／2tick共六组，记录逐笔保护价、成交价、实际开平费用和权益曲线；旧回放及日志另存保留。</li><li>更新中文定稿、主选run和桌面启动清单；已打开的旧窗口需要重启才会载入新策略。</li></ol><p><a href="protective-stop-replay-before-fee-plus-tick-20261009.json">成本公式修正前的回放</a> · <a href="per-trade-r-audit-before-fee-plus-tick-20261009.json">旧逐笔核对</a> · <a href="白糖多空投研定稿-保护成交修正前-20261009.html">保护成交修正前的旧报告</a> · <a href="final-main-release-before-stopfix-20261009.json">旧启动清单</a></p><details><summary>展开全部原研究说明、阶段日志及原结果（旧口径，不是现行结果）</summary><p class="alert">以下为此前报告原内容。旧盈亏、曲线、参数排名与执行说明只作历史审计，现行规则与结果以上方修正部分为准。旧交互曲线请打开完整旧报告。</p>__LEGACY__</details></section></main>
__SCRIPT__</body></html>'''
oldscript = re.search(r'<script>([\s\S]*?)</script>',old)[1]
script = re.sub(r'^const rows=.*?;const fmt=', 'const rows='+json.dumps(rows,ensure_ascii=False,separators=(',',':')).replace('</','<\\/')+';const fmt=',oldscript,count=1,flags=re.S)
assert script != oldscript and 'function redraw' in script
u = table(['实际UI路径','新run','净收益','最大回撤','逐笔/图表/持仓/提示'],
    [[x['direction'],x['run_id'],num(x['net_pnl']),num(x['max_drawdown']),'通过'] for x in ui['results']])
sample_table = table(['口径','保护价','出场成交价','毛利','双向费','净收益'],
    [['旧：触价后等下一开盘',5569,5593,750,9,741],['新：盘中保护单＋1tick',5569,5568,0,9,-9]])
for key,value in dict(SAMPLE=sample_table,METRICS=metrics,COMPARE=compare,COSTS=costs,RULES=rules,AUDITS=audits,
    PARAMS=escape(json.dumps({x['direction']:x['parameters'] for x in main},ensure_ascii=False,indent=2)),UI=u,LEGACY=legacy_main,SCRIPT='<script>'+script+'</script>',
    LAUNCHER='<p>已按桌面快捷方式相同目标真实冷启动两次，均显示SR701·15分钟及修正多头13251／3503结果。<a href="stopfix-launcher-verification-20261009.json">两次桌面启动证据</a></p>' if (OUT/'stopfix-launcher-verification-20261009.json').exists() else '').items():
    html = html.replace('__'+key+'__',value)
assert not re.search(r'__[A-Z]+__',html)
REPORT.write_text(html,encoding='utf8')
mfile = OUT/'final-main-release.json';m=json.loads(mfile.read_text(encoding='utf8'))
m['ui_runs']={x['direction']:x['run_id'] for x in ui['results']}
m['protective_stop_verification']['ui_file']='stopfix-ui-20261009/ui-verification.json'
m['report_archive']=ARCHIVE.name
for f in ('tools/update-sr-stopfix-report.py','tools/replay-sr-protective-stops.py','tools/verify-protective-stop.py','tools/verify-ema-short.py'):
    m['hashes'][f]=hashlib.sha256((ROOT/f).read_bytes()).hexdigest()
mfile.write_text(json.dumps(m,ensure_ascii=False,indent=2),encoding='utf8')
print('PASS: current Chinese report, six corrected curves, trade audit, original logs and full old report retained.')
