"""Main-only release report, retaining the research audit trail as archives."""
import hashlib,json,runpy,shutil
from datetime import datetime
from html import escape
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
DATA=Path('D:/mycode/qh/artifacts/sr2701-15m/wenhua-aligned-data')
OUT=DATA/'multiside-research-20261008'
OLD=runpy.run_path(str(ROOT/'tools/build-sr-multiside-report.py'))
table=OLD['table'];E=OLD['extra'];H=OLD['H']
money=lambda v:f'{v:,.0f}'

def main():
    # A corrected report must not be overwritten by the obsolete next-open
    # protection proof. Its complete old report and logs remain archived.
    if (OUT/'protective-stop-replay-20261009.json').exists():
        runpy.run_path(str(ROOT/'tools/update-sr-stopfix-report.py'),run_name='__main__')
        return
    proof=json.loads((OUT/'final-main-replay-proof.json').read_text(encoding='utf8'));assert proof['passed']
    ui=json.loads((OUT/'final-main-ui-verification.json').read_text(encoding='utf8'));assert ui['passed']
    assert all(x.get('position_correct') and x.get('stale_warning') for x in ui['results'])
    launcher_path=OUT/'final-main-launcher-verification.json'
    launcher=json.loads(launcher_path.read_text(encoding='utf-8-sig')) if launcher_path.exists() else None
    if launcher:assert launcher['passed'] and len(launcher['checks'])==2
    rows=proof['results'];assert len(rows)==6 and all(x['role']=='main' for x in rows)
    selected=[x for x in rows if x['slippage_ticks']==1]
    params={s:json.loads((OUT/f'{s}-final-parameters.json').read_text(encoding='utf8')) for s in ('long','short')}
    previous=OUT/'白糖多空投研定稿.html';legacy=OUT/'multiside-investment-report.html';archive=OUT/'multiside-investment-report-research-archive-20261009.html'
    if legacy.exists() and legacy.stat().st_size>100_000 and not archive.exists():shutil.copyfile(legacy,archive)
    decision=json.loads((OUT/'decision.json').read_text(encoding='utf8'))
    qq=json.loads((DATA/'qqokx-best-short-20261009/progress.json').read_text(encoding='utf8'))
    margin=json.loads((OUT/'margin-occupancy.json').read_text(encoding='utf8'))
    generated=datetime.now().astimezone().isoformat()
    manifest=dict(version='sr15-main-v1',status='local_configuration_final',released_at=generated,
        scope='CZCE.SR701 15m; separate long/short replays; no real orders or common-account simulation',
        startup_run_id=next(x['run_id'] for x in selected if x['direction']=='long'),
        parameters=params,ui_runs={x['direction']:x['run_id'] for x in ui['results']},
        verified_runs={x['direction']:x['run_id'] for x in selected},
        desktop_cold_starts_verified=2 if launcher else 0,
        expected={x['direction']:{k:x[k] for k in ('net_pnl','max_drawdown','closed_trades','max_initial_margin')} for x in selected},
        config=ui['results'][0]['config'],snapshot='snapshot-37c8f4717a61ae49',
        start='2026-02-02T21:00:00+08:00',end='2026-10-01T00:00:00+08:00',
        hashes={str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in
            [ROOT/'strategies/ema_dynamic_long_trade.py',ROOT/'strategies/ema_dynamic_short_trade.py',
             ROOT/'src/QhApp/MainWindow.xaml',ROOT/'src/QhApp/MainWindow.xaml.cs',ROOT/'src/QhApp/MainWindow.FinalMainCheck.cs',ROOT/'src/QhApp/App.xaml.cs',ROOT/'tools/start-dev.ps1']})
    (OUT/'final-main-release.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
    (OUT/'final-main-results.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf8')
    metrics=table(['主选','净收益','收益率','最大回撤','平仓数','PF','前三笔盈利占比','扣前三笔后','最长水下天数','最大单笔净亏'],
        [[x['label'],money(x['net_pnl']),f"{x['net_pnl']/1000:.2f}%",money(x['max_drawdown']),x['closed_trades'],f"{x['profit_factor']:.2f}",
          f"{x['top3_winner_share']:.1f}%",money(x['net_excluding_top3']),f"{x['longest_underwater_days']:.1f}",money(x['max_loss'])] for x in selected])
    rules=table(['条件','多头主选','空头主选'],[r for r in decision['rules'] if r[0]!='空头EMA21备选'])
    costs=table(['主选','每边滑点','净收益','回撤','平仓数','费用','最大保证金（10%）'],
        [[r['label'],r['slippage_ticks'],money(r['net_pnl']),money(r['max_drawdown']),r['closed_trades'],money(r['fees']),money(r['max_initial_margin'])] for r in rows])
    monthly=table(['月份','多头主选','空头主选'],[[f'2026-{m:02}']+[money(x['monthly_equity'].get(f'2026-{m:02}',0)) for x in selected] for m in range(2,10)])
    dd=table(['主选','权益峰值K线结束','权益谷值K线结束','已实现损益与费用变化','浮盈变化','回撤合计'],
        [[x['label'],x['drawdown_decomposition']['peak'],x['drawdown_decomposition']['trough'],money(x['drawdown_decomposition']['realized_change']),
          money(x['drawdown_decomposition']['floating_change']),money(x['max_drawdown'])] for x in selected])
    audit=''.join('<details><summary>'+escape(x['label'])+' · 逐笔交易（1tick）</summary>'+table(
        ['入场K线起点','平仓K线起点','手数','开仓价','平仓价','净损益','净R','退出原因'],
        [[t['entry_bar'],t['exit_bar'],t['quantity'],t['entry_price'],t['exit_price'],money(t['net_pnl']),f"{t['r']:.2f}",t['reason']] for t in x['trades']])+'</details>' for x in selected)
    accounts=table(['主选','已平仓净利','期末浮盈','未平开仓费','期末手数','账户净利','新复核run'],
        [[x['label'],money(x['closed_net_pnl']),money(x['end_open_pnl']),money(x['live_entry_fees']),x['end_open_quantity'],money(x['net_pnl']),x['run_id']] for x in selected])
    uiverify=table(['实际软件回测','run ID','净利','回撤','配置与默认一致','逐笔成交一致','曲线与EMA','期末持仓正确','修改未重跑提示'],
        [[x['direction'],x['run_id'],money(x['net_pnl']),money(x['max_drawdown']),x['fields'],x['fills_equal'],x['charts'],
          x.get('position_correct','待补充'),x.get('stale_warning','待补充')] for x in ui['results']])
    stamps=table(['记录时间','QQ研究阶段','状态','请求 / 回放数','复用数'],
        [[x['time'],x['stage'],x['state'],x.get('requested',x.get('runs','')),x.get('cached','')] for x in qq['log']])
    margins=table(['15分钟主选','最大手数','最大初始保证金','按持仓收盘价重估峰值'],
        [[x['label'],x['max_lots'],money(x['max_initial_margin']),money(next(p for p in margin['profiles'] if p['label']==('long-main-55' if x['direction']=='long' else 'short-main-55'))['max_close_mark_margin']['margin'])] for x in selected])
    hashes=table(['定稿文件','SHA256'],list(manifest['hashes'].items()))
    text=r'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖15分钟多空投研 · 主选定稿v1</title><style>
*{box-sizing:border-box}body{background:#edf3f8;color:#20384b;font:15px/1.75 "Microsoft YaHei",sans-serif;margin:0}header{padding:30px max(20px,calc((100vw - 1300px)/2));background:#14354c;color:#fff}h1{font-size:28px;margin:0}main{max-width:1300px;margin:auto;padding:22px}section{background:#fff;border:1px solid #d8e3ec;border-radius:12px;margin-bottom:20px;padding:23px}h2{font-size:22px;margin-top:0}h3{font-size:17px}.grid{display:grid;grid-template-columns:1fr 1fr;gap:18px}.callout{background:#ecf5ff;border-left:4px solid #2b81ad;padding:16px}.note{font-size:13px;color:#627889}nav,.legend{display:flex;flex-wrap:wrap;gap:20px}nav a{color:#d3eaff}a{color:#176799}.scroll{overflow:auto;max-height:550px}table{width:100%;border-collapse:collapse;font-size:13px}th,td{white-space:nowrap;padding:10px;text-align:right;border-bottom:1px solid #e0e8ef}th{position:sticky;top:0;background:#eef4fa}th:first-child,td:first-child{text-align:left}svg{width:100%;height:350px}select{padding:8px;margin:8px;font:inherit;border:1px solid #aebfd0;border-radius:5px}summary{cursor:pointer;font-weight:bold;padding:12px 0}pre{padding:16px;background:#f2f6fa;overflow:auto}@media(max-width:800px){.grid{grid-template-columns:1fr}main{padding:12px}section{padding:16px}}
</style></head><body><header><h1>白糖 SR701 · 15分钟多空主选定稿 v1</h1><p>本金100,000元 · 单笔风险1,000元 · 两条主选 · 完整条件、研究日志、保证金与软件复核</p><nav><a href="#choice">定稿结论</a><a href="#rules">参数条件</a><a href="#curve">资金曲线</a><a href="#log">研究日志</a><a href="#ui">软件复核</a></nav></header><main>
<section id="choice"><h2>最终只保留这两条主选</h2><div class="grid"><div class="callout"><b>多头：EMA55限价 · 每波最多1次</b><p>EMA21&gt;EMA55且收盘&gt;EMA55；ATR14×2；2R保本；4R起锁n−1R；3R后启用EMA55退出。</p><p>1tick净利15,041元、最大回撤4,202元。</p></div><div class="callout"><b>空头：EMA55限价 · 每波最多3次</b><p>EMA21&lt;EMA55且收盘&lt;EMA55；ATR14×2；2R保本；4R起锁n−1R；1R后启用EMA21退出。</p><p>1tick净利17,557元、最大回撤5,171元。</p></div></div>__METRICS__
<p>多头选择依据是回撤、邻近参数、交易配对和集中度；空头虽也是原矩阵最高净利，但ATR×1.5与锁盈3/4/5R的邻域有支持，每波3次优于不限次数，扣前三笔后仍为正。QQ四组跨品种迁移及阈值探索没有提供足够证据替换当前主选，所以定稿参数保持原研究结论。</p><p class="note">“实盘候选定稿”指冻结本版本配置，软件已完成主选应用与本地回测。尚无独立样本外验证；未连接真实交易账户或发送委托。限价与下一根开盘退出、开仓当根不管理是当前回放假设，不能当成盘中真实止损成交保证。两条账户分别10万元，收益不直接相加成为组合收益。</p></section>
<section id="rules"><h2>主选参数与完整开平条件</h2>__RULES__<p>共同参数：EMA21／EMA55，挂单EMA55，ATR14×2，最小跳动1点，合约乘数10，风险预算1000元，每手成本预留25元，保本偏移F=1点。多头每波1次、空头每波3次均为顺序再入场，持仓期间不加仓。</p>
<p><b>仓位：</b>用信号K线ATR计算计划止损，并向不利方向对齐tick；手数=floor[1000/(计划止损点数×10+25)]，不足一手跳过。实际成交后，以信号ATR和实际开仓价重算初始止损、冻结R，后续ATR不改变R；手数倍率固定1。保证金或手续费不足拒单。</p>
<p><b>限价成交：</b>完成K线后生成下一根委托，未触及则失效，下一根完成后按新EMA重挂。多头开盘≤限价，成交=min(开盘+滑点,限价)，否则最低价≤限价按限价成交；空头开盘≥限价，成交=max(开盘−滑点,限价)，否则最高价≥限价按限价成交。</p>
<p><b>触发顺序：</b>成交当根不管理。之后每根先检查此前已生效保护，再检查此前已启用EMA退出；最后更新保护和退出启用状态。盈利门槛包含F：多头最高价≥E+nR+F，空头最低价≤E−nR−F。2R把保护移到E±F；4R起锁n−1R。EMA退出达到门槛后从下一根完整K线开始生效，价格严格突破退出均线才平仓。确认退出后在下一根有效开盘全平，多头开盘−滑点、空头开盘+滑点。</p>
<p><b>区间与交易设置：</b>白糖CZCE.SR701，15分钟快照snapshot-37c8f4717a61ae49；正式2026-02-02 21:00（含）至2026-10-01 00:00（不含）；3893根输入、3640根正式、253根正式前预热。本金100000、保证金10%、开仓2／平今3／普通平仓1元每手、滑点1tick，无额外固定止损或止盈价。预热不交易，期末不强平。</p>
<p>EMA用最初N根收盘均值初始化，之后2/(N+1)递推；ATR用TR=max(H−L,|H−前收|,|L−前收|)，最初N根均值后Wilder递推。夜盘按既有本地交易日映射，休市不造K线，跨休市价差进入ATR。本轮未另行核对官方逐日交易日历。</p><details><summary>两条完整参数JSON</summary><pre>__PARAMS__</pre></details><p><a href="long-final-parameters.json">多头主选参数</a> · <a href="short-final-parameters.json">空头主选参数</a> · <a href="final-main-release.json">定稿清单</a></p></section>
<section id="curve"><h2>两条主选的资金曲线与回撤</h2><label>每边滑点<select id="slip"><option value="1">1tick定稿口径</option><option value="0">0tick</option><option value="2">2tick压力</option></select></label><div class="legend" id="legend"></div><svg id="equity" viewBox="0 0 1150 350" aria-label="独立账户资金曲线"></svg><div id="hover" class="note">移动鼠标看同根K线权益。</div><h3>从历史高点回撤金额</h3><svg id="drawdown" viewBox="0 0 1150 350" aria-label="主选权益回撤"></svg>__COSTS__<p class="note">全部滑点情景重新回放。交易路径变化时，收益差不等于固定扣除滑点费。</p><h3>每月权益变化 · 1tick</h3>__MONTHLY__<h3>最大回撤的峰谷分解</h3>__DD__<p>多头浮盈变化占峰谷回撤约85.7%，重点在利润保护及成交时序；空头已实现损益与费用变化占约73.7%，重点在重复入场质量。分解为同一峰谷的账户恒等式，不是逐笔因果归因，也未记录更细盘中权益低点。</p></section>
<section><h2>保证金与期末持仓</h2>__MARGINS__<p>按双边全额相加，独立持仓K线叠加的初始保证金峰值63,522元，出现于2026-07-27 21:30，当时多6手、空6手；未应用同合约对锁优惠，未做统一账户回放。回放冻结开仓保证金，不模拟逐日结算价或比例调整；10%是研究假设，实盘须用期货公司实际标准。</p>__ACCOUNTS__<p>净利=已平仓净利+期末浮盈−未平开仓费。多头期末空仓；空头剩5手，已平仓18017−浮亏450−开仓费10=17557。修复软件摘要读取旧持仓记录的显示问题后，界面与这个账目一致。风险预算1000不是亏损硬上限，多头最大单亏1865、空头1475；定稿没有增加未经研究的手数上限或保证金阈值。</p></section>
<section id="log"><h2>研发历程与新增研究日志</h2><ol><li><b>数据与启动：</b>统一SDK入口后一度退回TEST·1分钟；桌面入口统一到白糖15/30/60数据目录。检查休市、夜盘、短尾K线及跳空，不跨停盘拼K线。</li><li><b>策略迁移与仓位：</b>迁移QQOKX EMA动态委托做多，明确以损定量；500／1000预算比较后按用户选择冻结1000、本金10万。软件曾出现2212元与脚本差异，统一正式区间、仓位、费用与执行时序，以保存run核对。</li><li><b>多头ATR与保护：</b>60组ATR14×1／1.5／2、保护条件和周期对照，分析大回撤来自浮盈变化及跳空。继续448组锁盈起点、每波次数和EMA退出研究，最后保留本报告多头主选。</li><li><b>空头独立研究：</b>从EMA21／55挂单对比起步；36组ATR／保护交叉，896组深度研究请求（4组重复，新增892次），再检查邻域、周期迁移和2tick压力。每波3次是顺序再入场，接受单波累计风险超过1000，最终定稿EMA55主选。</li><li><b>QQ最佳空头迁移（2026-10-09）：</b>读取真实最佳包，核对BTC EMA55／ATR14、ETH MA61／ATR11、SOL MA20／ATR15、DOGE MA21／ATR13。54组原参数对照，64组阈值请求中复用8组、新增56组，再补6组2tick，共116个唯一回放。15分钟没有更好的替代；30分钟缺少稳定优势；60分钟SOL探索只比现有比较基准多赚366、回撤多999且成本下集中度余量不足。因此维持15分钟主选，两种均线体系不混为同一策略。</li><li><b>保证金复核（2026-10-09）：</b>从持仓和成交记录逐笔核对10%初始保证金，多头峰值32208、空头43056，检查收盘价重估及双边叠加。记录保证金12%／15%的比例情景，区分风险金、占用资金和追加保证金。</li><li><b>定稿与软件复核（2026-10-09）：</b>报告主区只展示两条主选；界面移除备选按钮，两个应用按钮恢复数据、区间、完整策略和交易配置，直接创建新回测。修复“当前持仓”误读最后非空历史行；胜率显示两位小数。0／1／2tick六组复跑，0／1tick的完整成交、平仓及权益序列与原冻结run逐项一致。实际界面再回测两条主选，首击新建run、EMA／资金曲线、期末持仓和修改未重跑提示均验证。</li></ol>
<p>本报告只保留主选配置。候选比较和原矩阵仍作为研究依据归档，未删除历史数据。<a href="multiside-investment-report-research-archive-20261009.html">原多空研究归档</a> · <a href="../qqokx-best-short-20261009/qqokx-white-sugar-short-report.html">QQ迁移完整日志与报告</a> · <a href="margin-occupancy-report.html">保证金研究明细</a> · <a href="../atr14-matrix-20261008/atr14-matrix-report.html">多头ATR阶段</a> · <a href="../atr14-matrix-20261008/exit-entry-study-20261008/exit-entry-study-report.html">多头深度阶段</a></p><details><summary>QQ阶段执行时间记录</summary>__LOGS__</details><p class="note">历次扫描都基于同一历史，116组也不是116份独立证据；没有进行真正样本外测试或计算PBO。作者关于重复回测筛选偏差的说明：<a href="https://www.davidhbailey.com/dhbpapers/backtest-prob.pdf">The Probability of Backtest Overfitting</a>。</p></section>
<section id="ui"><h2>软件界面与回测检查</h2><p>桌面入口固定到定稿构建目录与白糖数据根目录。回测策略页只有“应用多头主选”“应用空头主选”两条定稿入口；点击会加载15分钟数据、正式区间、本金10万、风险1000、费用2/3/1及1tick，同时离开历史只读状态。随后第一次点击开始回测就生成新run。启动默认展示已核对的多头主选保存结果。</p>__UI__<p><a href="final-main-ui-verification.json">实际界面验证JSON</a> · <a href="final-main-replay-proof.json">六组回放核对证据</a> · <a href="final-main-long.png">多头界面</a> · <a href="final-main-short.png">空头界面</a></p><details><summary>全部主选逐笔交易 · 1tick</summary>__AUDIT__</details><details><summary>定稿源码哈希</summary>__HASHES__</details><p class="note">生成__GENERATED__。软件使用本地回放器；交易所单位10吨/手、tick1元/吨见<a href="https://www.czce.com.cn/cn/uploadfile/2024/06/26/20240626165544343.pdf">郑商所白糖细则</a>。手续费、保证金率和下一根开盘成交均按研究假设保存，尚未映射为经纪商真实委托执行。</p></section></main>
<script>const rows=__ROWS__;const fmt=v=>Number(v).toLocaleString('zh-CN',{maximumFractionDigits:2});
function plot(id,series){let svg=document.getElementById(id),W=1150,H=350,L=85,R=20,T=20,B=38,lo=Infinity,hi=-Infinity;for(const s of series)for(const p of s.points){lo=Math.min(lo,p[1]);hi=Math.max(hi,p[1])}if(id==='equity'){lo=Math.min(lo,100000);hi=Math.max(hi,100000)}else lo=0;let pad=(hi-lo)*.06||1;lo=id==='equity'?lo-pad:0;hi+=pad;let x=i=>L+i*(W-L-R)/(series[0].points.length-1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo),out=[];for(let n=0;n<5;n++){let v=lo+(hi-lo)*n/4,i=Math.round((series[0].points.length-1)*n/4);out.push(`<line x1="${L}" x2="${W-R}" y1="${y(v)}" y2="${y(v)}" stroke="#e0e8ef"/><text x="${L-8}" y="${y(v)+4}" text-anchor="end" font-size="12" fill="#647b8c">${fmt(Math.round(v))}</text><text x="${x(i)}" y="${H-9}" text-anchor="middle" font-size="12" fill="#647b8c">${series[0].points[i][0].slice(0,10)}</text>`)}for(const s of series)out.push(`<path d="${s.points.map((p,i)=>(i?'L':'M')+x(i).toFixed(2)+','+y(p[1]).toFixed(2)).join(' ')}" fill="none" stroke="${s.color}" stroke-width="2"/>`);svg.innerHTML=out.join('');if(id==='equity')svg.onmousemove=e=>{let rect=svg.getBoundingClientRect(),i=Math.round(((e.clientX-rect.left)/rect.width*W-L)/(W-L-R)*(series[0].points.length-1));i=Math.max(0,Math.min(i,series[0].points.length-1));document.getElementById('hover').textContent=series[0].points[i][0]+' · '+series.map(s=>s.label+' '+fmt(s.points[i][1])).join(' / ')}}
function redraw(){let series=rows.filter(r=>r.slippage_ticks===+document.getElementById('slip').value).map(r=>({label:r.label,color:r.direction==='long'?'#236d9d':'#c26950',points:r.equity,row:r}));document.getElementById('legend').innerHTML=series.map(s=>`<span style="color:${s.color}">${s.label} · 净利${fmt(s.row.net_pnl)} / 回撤${fmt(s.row.max_drawdown)}</span>`).join('');plot('equity',series);plot('drawdown',series.map(s=>{let peak=100000;return{...s,points:s.points.map(([t,v])=>{peak=Math.max(peak,v);return[t,peak-v]})}}))}document.getElementById('slip').addEventListener('change',redraw);redraw();</script></body></html>'''
    # Do not silently publish stale or missing verification flags.
    assert all(x.get('position_correct') and x.get('stale_warning') for x in ui['results'])
    for token,value in dict(METRICS=metrics,RULES=rules,PARAMS=escape(json.dumps(params,ensure_ascii=False,indent=2)),
        COSTS=costs,MONTHLY=monthly,DD=dd,MARGINS=margins,ACCOUNTS=accounts,LOGS=stamps,UI=uiverify,AUDIT=audit,
        HASHES=hashes,GENERATED=generated,ROWS=json.dumps(rows,ensure_ascii=False,separators=(',',':')).replace('</',r'<\/')).items():
        text=text.replace('__'+token+'__',value)
    if launcher:
        cold=table(['桌面冷启动验证','进程ID','构建程序','通过'],
            [[x['attempt'],x['process_id'],x['app'],x['passed']] for x in launcher['checks']])
        text=text.replace('<details><summary>全部主选逐笔交易',
            '<p>使用桌面快捷方式相同目标与参数进行两次真实冷启动，均加载白糖15分钟3893根及多头主选15041／4202结果，没有回到TEST。仅关闭了这次验证自己启动的进程，既有旧窗口未关闭。</p>'+cold+
            '<p><a href="final-main-launcher-verification.json">两次桌面冷启动证据</a></p><details><summary>全部主选逐笔交易')
    previous.write_text(text,encoding='utf8')
    legacy.write_text('<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta http-equiv="refresh" content="0;url=白糖多空投研定稿.html"><title>报告已更名</title><p>报告已更名：<a href="白糖多空投研定稿.html">打开白糖多空投研定稿</a></p></html>',encoding='utf8')
    print('PASS final report: two main strategies, six cost curves, research/margin/UI logs retained; archive preserved.')

if __name__=='__main__':main()
