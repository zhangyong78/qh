const fs=require('fs'),vm=require('vm'),assert=require('assert');
const root='D:/mycode/qh/artifacts/sr2701-15m/wenhua-aligned-data/multiside-research-20261008/';
const html=fs.readFileSync(root+'白糖多空投研定稿.html','utf8');
assert(!/__[A-Z]+__/.test(html));assert(!html.includes('空头EMA21备选'));
const script=[...html.matchAll(/<script>([\s\S]*?)<\/script>/g)];assert.equal(script.length,1);
const elems={};for(const m of html.matchAll(/\bid="([^"]+)"/g))elems[m[1]]={innerHTML:'',textContent:'',value:'',addEventListener(){},getBoundingClientRect(){return{left:0,width:1150}}};
elems.slip.value='1';const context=vm.createContext({document:{getElementById:k=>elems[k]}});vm.runInContext(script[0][1],context);
const rows=JSON.parse(vm.runInContext('JSON.stringify(rows)',context));assert.equal(rows.length,6);assert(rows.every(r=>r.role==='main'&&r.entry_ema===55));
for(const slip of ['0','1','2']){elems.slip.value=slip;context.redraw();for(const id of ['equity','drawdown']){
assert.equal((elems[id].innerHTML.match(/<path /g)||[]).length,2);assert(!/NaN|undefined|Infinity/.test(elems[id].innerHTML))}
elems.equity.onmousemove({clientX:500});assert(elems.hover.textContent.includes('2026-'));const picked=rows.filter(r=>r.slippage_ticks===+slip);
for(const r of picked){let peak=100000,dd=0;r.equity.forEach((p,i)=>{assert.equal(p[0],picked[0].equity[i][0]);peak=Math.max(peak,p[1]);dd=Math.max(dd,peak-p[1])});assert.equal(dd,r.max_drawdown);assert.equal(r.equity.at(-1)[1]-100000,r.net_pnl);assert.equal(Object.values(r.monthly_equity).reduce((a,b)=>a+b,0),r.net_pnl)}}
const ui=JSON.parse(fs.readFileSync(root+'stopfix-ui-20261009/ui-verification.json','utf8'));assert(ui.passed&&ui.results.every(r=>r.fields&&r.fills_equal&&r.charts&&r.position_correct&&r.stale_warning));
const proof=JSON.parse(fs.readFileSync(root+'protective-stop-replay-20261009.json','utf8'));
assert(proof.passed&&proof.sample_trade.exit_price===5568&&proof.sample_trade.net_pnl===-9);
assert(fs.existsSync(root+'白糖多空投研定稿-保护成交修正前-20261009.html'));
console.log('PASS final main HTML: two strategies only, six equity/DD cost curves, hover, account/monthly reconciliation and actual UI proof.');
