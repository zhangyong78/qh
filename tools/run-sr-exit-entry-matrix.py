"""Run the SR701 ATR14 trailing-start, re-entry, and EMA-exit matrix."""
from __future__ import annotations
import csv, json, math, statistics, subprocess, sys
from datetime import datetime, timezone, timedelta
from html import escape
from pathlib import Path

ROOT=Path(__file__).resolve().parents[1]
DATA=ROOT/"artifacts"/"sr2701-15m"/"wenhua-aligned-data"
BASE=DATA/"atr14-matrix-20261008"
OUT=BASE/"exit-entry-study-20261008"
CLI=ROOT/"src"/"Qh.Cli"/"bin"/"Release"/"net10.0"/"Qh.Cli.dll"
STRATEGY=ROOT/"strategies"/"ema_dynamic_long_trade.py"
SNAPSHOT="snapshot-37c8f4717a61ae49"
START="2026-02-02T21:00:00+08:00"; END="2026-10-01T00:00:00+08:00"
INITIAL=100000.0; BUDGET=1000.0
LOCKS=(3,4,5,6,8,10,12,15); CAPS=(1,2,3,0)
EXITS=(("none",0,0),)+tuple((f"ema{p}_r{r}",p,r) for p in (21,55) for r in (1,2,3))

def run_key(c, slip):
    return f"{c['lock_r']}|{c['entries']}|{c['exit']}|{slip}"

def configs():
    for lock in LOCKS:
        for cap in CAPS:
            for mode, period, activation in EXITS:
                yield {"lock_r":lock,"entries":cap,"exit":mode,"ema":period,"activation":activation}

def read_jsonl(path):
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()] if path.exists() else []

def execute(config, slip):
    k=run_key(config,slip)
    params={"ema_period":21,"trend_ema_period":55,"entry_ema_period":55,"atr_period":14,
        "atr_stop_multiplier":2,"profit_lock_start_r":config["lock_r"],
        "protection_variant":"two_r_n_minus_one","max_entries_per_trend":config["entries"],
        "ema_exit_period":config["ema"],"ema_exit_activation_r":config["activation"] or 2,
        "min_tick":1,"dynamic_fee_offset_points":1,"risk_budget":BUDGET,
        "contract_multiplier":10}
    args=["dotnet",str(CLI),"trade-replay",str(DATA),SNAPSHOT,str(STRATEGY),START,END,
        "--initial-equity=100000","--lots=1","--multiplier=10","--min-tick=1","--margin-rate=0.1",
        "--fee-mode=Fixed","--open-fee=2","--today-fee=3","--ordinary-fee=1",f"--slippage-ticks={slip}",
        *[f"{name}={value}" for name,value in params.items()]]
    result=subprocess.run(args,cwd=ROOT,capture_output=True,text=True,encoding="utf-8",errors="replace",timeout=240)
    if result.returncode: raise RuntimeError(f"{k}: {result.stderr[-1200:]}")
    run_id=next((line.split("run_id=",1)[1].split(";",1)[0] for line in result.stdout.splitlines() if "run_id=" in line),None)
    if not run_id: raise RuntimeError(f"{k}: no run id; {result.stdout[-1200:]}")
    directory=DATA/"trade-runs"/run_id
    record=json.loads((directory/"run.json").read_text(encoding="utf-8-sig"))
    if record.get("Status")!="completed": raise RuntimeError(f"{k}: {record.get('Status')} {record.get('ErrorMessage')}")
    return {"key":k,"config":config,"slip":slip,"run_id":run_id,"run_dir":str(directory),"record":record}

def ema_waves(path):
    closes=[]; states={p:{"last":0,"value":None} for p in (21,55)}; wave=0; up_before=False; result={}
    def step(p):
        state=states[p]
        if len(closes)<p: return None
        if state["last"]==0:
            state["value"]=sum(closes[:p])/p; state["last"]=p
        alpha=2/(p+1)
        while state["last"]<len(closes):
            state["value"]+=alpha*(closes[state["last"]]-state["value"]); state["last"]+=1
        return state["value"]
    with path.open(encoding="utf-8-sig",newline="") as stream:
        for row in csv.DictReader(stream):
            closes.append(float(row["close"])); fast=step(21); slow=step(55)
            up=fast is not None and slow is not None and fast>slow
            if up and not up_before: wave+=1
            if up:
                raw=row["timestamp"].replace("Z","+00:00")
                try: stamp=datetime.fromisoformat(raw)
                except ValueError: stamp=datetime.strptime(raw,"%Y/%m/%d %H:%M:%S").replace(tzinfo=timezone(timedelta(hours=8)))
                result[stamp.isoformat(timespec="seconds")]=wave
            up_before=up
    return result

def summarize(item,wave_map):
    d=Path(item["run_dir"]); record=item["record"]
    fills=read_jsonl(d/"fills.jsonl"); trades=read_jsonl(d/"closed-trades.jsonl")
    equity=read_jsonl(d/"equity.jsonl"); intents={x["Sequence"]:x for x in read_jsonl(d/"intents.jsonl")}
    pnls=[float(x["NetPnl"]) for x in trades]; wins=sorted((x for x in pnls if x>0),reverse=True)
    loss=abs(sum(x for x in pnls if x<0)); peak=INITIAL; max_dd=0.; dd_peak=INITIAL
    realized_peak=INITIAL; realized_dd=0.
    for point in equity:
        realized=INITIAL+float(point["RealizedGrossPnl"])-float(point["Fees"])
        realized_peak=max(realized_peak,realized); realized_dd=max(realized_dd,realized_peak-realized)
        value=float(point["Equity"])
        if value>peak: peak=value
        if peak-value>max_dd: max_dd=peak-value; dd_peak=peak
    waves={}; sizes=[]
    for fill in fills:
        if fill["Action"]!="open": continue
        intent=intents[fill["IntentSequence"]]
        w=wave_map.get(intent["SignalBarStart"])
        if w is not None: waves[w]=waves.get(w,0)+1
        sizes.append(int(fill["Quantity"]))
    close_descriptions=[intents[f["IntentSequence"]].get("Description","") for f in fills if f["Action"]=="close" and f["IntentSequence"] in intents]
    net=float(record["NetPnl"]); top=sum(wins[:3])
    return {"key":item["key"],"run_id":item["run_id"],"run_dir":item["run_dir"],"lock_r":item["config"]["lock_r"],
        "entries_limit":item["config"]["entries"],"exit":item["config"]["exit"],"ema":item["config"]["ema"],
        "activation":item["config"]["activation"],"slip":item["slip"],"net":net,
        "dd":float(record["MaxDrawdown"]),"dd_pct":max_dd/dd_peak*100 if dd_peak else 0,
        "realized_dd":realized_dd,"pf":sum(wins)/loss if loss else None,"win_rate":float(record["WinRate"] or 0),
        "closed":int(record["ClosedTradeCount"]),"open_fills":sum(f["Action"]=="open" for f in fills),
        "fills":int(record["FillCount"]),"ema_exits":sum("收盘跌破EMA" in desc for desc in close_descriptions),
        "avg_lots":statistics.mean(sizes) if sizes else None,"max_loss":min(pnls,default=0.),
        "over_budget":sum(x<-BUDGET for x in pnls),"over_budget_excess":sum(max(0,abs(x)-BUDGET) for x in pnls if x<-BUDGET),
        "top3_win_share":top/sum(wins)*100 if wins else None,"net_ex_top3":net-top,
        "waves":len(waves),"multi_entry_waves":sum(x>1 for x in waves.values()),
        "max_in_wave":max(waves.values(),default=0),"avg_entries_wave":sum(waves.values())/len(waves) if waves else None,
        "equity":[[x["BarEnd"],float(x["Equity"])] for x in equity]}

def fmt(v,d=2):
    return "—" if v is None or not math.isfinite(float(v)) else f"{float(v):,.{d}f}"

def make_report(rows):
    def label(row):
        cap="不限" if row["entries_limit"]==0 else str(row["entries_limit"])+"次"
        line="无" if row["ema"]==0 else f"EMA{row['ema']}@{row['activation']}R"
        return f"{row['lock_r']}R / {cap} / {line} / {row['slip']}tick"
    def table_rows(items):
        return "".join("<tr>"+"".join("<td>"+escape(str(v))+"</td>" for v in values)+"</tr>" for values in items)
    baseline={r["key"]:r for r in rows}["3|1|none|1"]
    by_key={r["key"]:r for r in rows}
    fixed=sorted((r for r in rows if r["slip"]==1 and r["entries_limit"]==1 and r["ema"]==0),key=lambda r:r["lock_r"])
    top=sorted((r for r in rows if r["slip"]==1),key=lambda r:r["net"],reverse=True)[:5]
    balanced=sorted((r for r in rows if r["slip"]==1 and r["closed"]>=10),key=lambda r:r["net"]/max(r["dd"],1),reverse=True)[:5]
    def shortlist(items):
        return "<div class='scroll'><table><thead><tr><th>锁盈/次数/均线</th><th>净利</th><th>回撤</th><th>笔数</th><th>收益/回撤</th><th>盈亏因子</th><th>扣前三后</th><th>run ID</th></tr></thead><tbody>"+table_rows(
            [(label(r),fmt(r["net"],0),fmt(r["dd"],0),r["closed"],fmt(r["net"]/r["dd"] if r["dd"] else None),fmt(r["pf"]),fmt(r["net_ex_top3"],0),r["run_id"]) for r in items])+"</tbody></table></div>"
    lock_table="<div class='scroll'><table><thead><tr><th>锁盈起点</th><th>净利</th><th>回撤</th><th>交易笔数</th><th>胜率</th><th>盈亏因子</th><th>扣前三后</th></tr></thead><tbody>"+table_rows(
        [(f"{r['lock_r']}R",fmt(r["net"],0),fmt(r["dd"],0),r["closed"],fmt(r["win_rate"],1)+"%",fmt(r["pf"]),fmt(r["net_ex_top3"],0)) for r in fixed])+"</tbody></table></div>"
    entry_rows=sorted((r for r in rows if r["slip"]==1 and r["lock_r"]==3 and r["ema"]==0),key=lambda r:r["entries_limit"])
    entry_table="<div class='scroll'><table><thead><tr><th>每波上限</th><th>实际开仓</th><th>多次入场波数</th><th>单波最多</th><th>净利</th><th>回撤</th><th>盈亏因子</th></tr></thead><tbody>"+table_rows(
        [("不限" if r["entries_limit"]==0 else f"{r['entries_limit']}次",r["open_fills"],r["multi_entry_waves"],r["max_in_wave"],fmt(r["net"],0),fmt(r["dd"],0),fmt(r["pf"])) for r in entry_rows])+"</tbody></table></div>"
    ema_sweep=sorted((r for r in rows if r["slip"]==1 and r["entries_limit"]==1 and r["activation"]==3 and r["ema"] in (21,55)),key=lambda r:(r["ema"],r["lock_r"]))
    ema_table="<div class='scroll'><table><thead><tr><th>保护锁盈起点</th><th>EMA触发</th><th>净利</th><th>回撤</th><th>收益/回撤</th><th>盈亏因子</th><th>平仓笔数</th><th>EMA退出笔数</th><th>前三占盈利</th><th>扣前三后</th></tr></thead><tbody>"+table_rows(
        [(f"{r['lock_r']}R",f"EMA{r['ema']}@3R",fmt(r["net"],0),fmt(r["dd"],0),fmt(r["net"]/r["dd"] if r["dd"] else None),fmt(r["pf"]),r["closed"],r["ema_exits"],fmt(r["top3_win_share"],1)+"%",fmt(r["net_ex_top3"],0)) for r in ema_sweep])+"</tbody></table></div>"
    audit=json.loads((OUT/"candidate-analysis.json").read_text(encoding="utf-8"))
    selected=[("原基准：3R，无EMA","3|1|none|1"),("主选：4R，EMA55@3R","4|1|ema55_r3|1"),
              ("进取备选：6R，EMA21@3R","6|1|ema21_r3|1"),("对照：15R，无EMA","15|1|none|1")]
    comparison="<div class='scroll'><table><thead><tr><th>方案（均每波1次）</th><th>净利</th><th>权益回撤</th><th>收益/回撤</th><th>盈亏因子</th><th>前三笔占盈利</th><th>去掉前三笔净利</th><th>最长未创新高天数</th><th>0→1 tick净利减少</th></tr></thead><tbody>"+table_rows(
        [(name,fmt(by_key[key]["net"],0),fmt(by_key[key]["dd"],0),fmt(by_key[key]["net"]/by_key[key]["dd"]),
          fmt(by_key[key]["pf"]),fmt(by_key[key]["top3_win_share"],1)+"%",fmt(by_key[key]["net_ex_top3"],0),
          fmt(audit["candidates"][key]["longest_underwater_days"],1),
          fmt(by_key[key[:-1]+"0"]["net"]-by_key[key]["net"],0)) for name,key in selected])+"</tbody></table></div>"
    months=sorted(audit["candidates"][selected[0][1]]["monthly_equity"])
    monthly="<div class='scroll'><table><thead><tr><th>月份</th>"+ "".join("<th>"+escape(name)+"</th>" for name,_ in selected)+"</tr></thead><tbody>"+table_rows(
        [(month,*[fmt(audit["candidates"][key]["monthly_equity"].get(month,0),0) for _,key in selected]) for month in months])+"</tbody></table></div>"
    analysis=("<p class='callout'><b>综合结论：下一轮参数打磨主选4R起锁N−1R、盈利3R后启用EMA55收盘跌破退出、每波最多1次。6R＋EMA21@3R保留为进取备选。</b>"
        "主选基于较低回撤、更均匀的利润分布、更短的权益恢复时间和较小的滑点敏感性；6R＋EMA21收益/回撤比更高，但收益提升更集中在少数交易。</p>"+comparison+
        "<h3>为什么主选4R＋EMA55</h3>"
        "<p>主选净利15,041元，比基准增加1,250元，最大回撤仍为4,202元；盈亏因子2.66，收益/回撤3.58。前三笔占总盈利39.0%，去掉各自最赚钱三笔后仍净赚5,636元。最大回撤的峰值发生在3月30日，主选7月3日恢复前高，最长未创新高约95.5天；基准和6R＋EMA21均约130天。</p>"
        "<p>逐笔配对：主选与基准25笔入场全部对应，3笔结果改善、2笔变差、20笔相同；实际只有2笔由EMA55触发退出。它的提升范围有限，所以应把它当作下一轮验证的起点，而不是已经证明的最优参数。</p>"
        "<h3>为什么6R＋EMA21不是主选</h3>"
        "<p>6R＋EMA21净利17,603元、回撤4,802元、收益/回撤3.67，仍是值得保留的方案。但它比基准多赚3,812元，其中单笔改善4,572元：如果只扣掉这一笔带来的增量，其他配对交易合计比基准少赚760元。前三笔占总盈利52.2%，去掉各自前三笔净利3,674元，低于主选的5,636元。0→1 tick时净利减少1,260元，主选减少610元；这部分差异也包含触发路径改变，不能解释为纯手续费差。</p>"
        "<p>相邻参数方面，EMA21@3R配合5/6/8R锁盈的净利分别约15,923/17,603/16,883元，配合10/12/15R均为16,043元，回撤均为4,802元，形成一段平台，支持保留这个方向。主选EMA55@3R配合3/4/5R时净利13,791/15,041/16,153元，回撤4,202/4,202/5,501元；4R位于回撤尚未明显放大的位置。1R或2R就启用EMA55时（4R锁盈）净利仅7,213/10,411元，3R更适合这段样本。</p>"
        "<h3>开仓次数、锁盈时机与风险预算</h3>"
        "<p>每波最多1次优先。固定其他参数的56组成对比较中，1次同时获得不低的净利和不高的回撤：相对2次为46/56组、相对3次为49/56组、相对不限为56/56组。不限次数在这一段样本的所有配对中都更差；该统计来自同一行情上的相关组合，不是56次独立验证。</p>"
        "<p>单纯把锁盈延后到15R：净利19,342元，但回撤8,261元，前三笔占盈利93.6%，去掉前三笔后净亏6,823元，最长未创新高约141.6天。它更依赖几次大趋势，不作为主选。</p>"
        "<p>本金100,000元、风险预算1,000元、ATR14×2保持为本轮研究口径。手数按计划止损点数×合约乘数计算，不在开仓时扣手续费预留。跳空和下一根开盘成交会使实际亏损超过预算。</p>"
        "<h3>主选规则与下一步</h3><p>2R时保护到开仓价＋F；3R后启用EMA55退出；4R起保护到N−1R（4R→3R、5R→4R……）。F=1点；盈利门槛按开仓价＋R倍数＋F判断。新保护从下一根完整K线开始生效；EMA55收盘跌破确认后下一根有效K线开盘模拟平仓；同根触及既有保护时，保护退出优先。每个EMA21＞EMA55连续阶段最多成交入场一次，EMA21≤EMA55后重置。</p>"
        "<p>优先逐笔复盘主选与基准的5笔不同结果，再用未参与选参的数据比较主选、基准和6R备选。现有448组是同一段行情的参数试验；主选仅25笔交易，尚无样本外验证。本结论用于15分钟白糖的下一轮研究，不能据此直接确定30/60分钟或实盘参数。</p>"
        "<h3>月度权益变化（含未实现盈亏，按月末权益差）</h3>"+monthly+
        "<p class='note'>最长未创新高时间按逐根权益计算，单位为自然日。去掉前三笔是删除各方案最赚钱三笔后的净利诊断，并非重新回放后的收益；成对交易按相同开仓K线匹配。月度表体现样本内分布，不能视为滚动或样本外测试。</p>")
    body=[]
    for r in rows:
        ema="不启用" if r["ema"]==0 else f"EMA{r['ema']}@{r['activation']}R"
        body.append("<tr>"+"".join("<td>"+escape(str(x))+"</td>" for x in (
            "不限" if r["entries_limit"]==0 else r["entries_limit"],f"{r['lock_r']}R",ema,f"{r['slip']}tick",
            r["open_fills"],r["multi_entry_waves"],r["max_in_wave"],r["closed"],fmt(r["net"],0),fmt(r["dd"],0),
            fmt(r["pf"]),fmt(r["win_rate"],1)+"%",fmt(r["max_loss"],0),r["ema_exits"],
            fmt(r["top3_win_share"],1)+"%",fmt(r["net_ex_top3"],0),r["run_id"]))+"</tr>")
    # Every configuration is represented in the chart using only formal-interval samples.
    curve_rows=[]
    for r in rows:
        points=r["equity"]
        if points:
            sampled=points[::15]
            if sampled[-1]!=points[-1]: sampled.append(points[-1])
            curve_rows.append({"key":r["key"],"points":sampled})
    data=json.dumps([{k:v for k,v in r.items() if k not in ("equity","run_dir")} for r in rows],ensure_ascii=False,separators=(",",":"))
    curves=json.dumps(curve_rows,ensure_ascii=False,separators=(",",":"))
    return """<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖止盈、重入次数研究</title>
<style>:root{--bg:#f3f6fa;--card:#fff;--ink:#162238;--muted:#61718a;--line:#d9e2ee}*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.58 "Segoe UI","Microsoft YaHei",sans-serif}header{padding:28px max(24px,calc((100vw - 1300px)/2));background:linear-gradient(115deg,#122950,#246198);color:white}h1{margin:0 0 5px;font-size:28px}header p{margin:3px 0}main{max-width:1300px;margin:22px auto;padding:0 20px 48px}section{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:19px;margin:0 0 17px;box-shadow:0 3px 14px #16345b0b}h2{font-size:21px;margin:0 0 9px}h3{font-size:16px;margin:17px 0 8px}.scroll{overflow:auto;max-height:440px;border:1px solid var(--line);border-radius:8px}table{width:100%;border-collapse:collapse;font-size:12px}th,td{padding:7px 8px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}th:first-child,td:first-child{text-align:left}th{position:sticky;top:0;background:#edf3fa}.callout{padding:11px 14px;background:#edf4ff;border-left:4px solid #2563eb}.note{color:var(--muted);font-size:13px}.controls{display:flex;gap:13px;flex-wrap:wrap;align-items:center;margin:12px 0}select,input{padding:7px 9px;border:1px solid #becbd9;border-radius:7px;background:#fff}#chart{display:block;width:100%;height:400px;background:white}.legend{display:flex;gap:16px;flex-wrap:wrap;font-size:13px}.legend span:before{content:"";display:inline-block;width:17px;height:3px;background:var(--c);vertical-align:middle;margin-right:6px}code{background:#eef2f7;padding:2px 4px}@media(max-width:700px){section{padding:13px}h1{font-size:22px}#chart{height:310px}}</style></head><body>
<header><h1>白糖 SR701 · ATR止盈、每波开仓次数与均线退出</h1><p>15m ATR14×2 · 10万元本金 · 每笔1,000元风险预算 · 共448次官方回放</p><p>固定 EMA21/55 入场与正式区间；结论优先参考1 tick，另有0 tick对照。生成时间：__TIME__</p></header><main>
<section><h2>综合结论与主选参数</h2>__ANALYSIS__</section>
<section><h2>研究口径</h2><p class="callout"><b>保护规则：</b>成交后冻结 R=开仓价−ATR初始止损价。2R时将保护提升至含手续费偏移F=1点的保本价。起点 L 为3/4/5/6/8/10/12/15R；达到 L 后锁住(L−1)R，此后每增加完整1R，再锁高1R。L=3R即之前的C方案。</p>
<p><b>均线退出：</b>不启用，或启用 EMA21/EMA55。完整K线最高价到开仓价+1/2/3R 后，均线条件从下一根K线起生效；保护价移动时才加双向手续费和1跳缓冲。若随后整根K线收盘价严格低于所选EMA，下一根有效K线开盘模拟平仓。入场K线不管理新仓。EMA与保护同时触及时，既有保护优先。</p>
<p><b>每波入场次数：</b>成交才计次；1/2/3或0（不限）。EMA21≤EMA55重置次数。平仓后该均线阶段若仍有剩余次数，可再次挂限价单；不同时持有多笔，未成交单逐根重挂不计次。不限仍受余额和K线机会约束。</p>
<p><b>固定条件：</b>EMA21&gt;EMA55且收盘站在EMA55上，按EMA55挂限价多单；ATR14初始止损×2；本金100,000元、风险预算1,000元、乘数10、tick 1，固定手续费开仓2/平今3/平昨1元。开仓手数按止损点数×乘数向下取整，不预留手续费；移动保护价加入双向手续费和1跳缓冲。区间2026-02-02 21:00至2026-10-01 00:00（北京时间、左闭右开）。每种配置分别跑0与1 tick，共8×4×7×2=448组。</p>
<p class="note">参数组合完整交叉，均线退出与重入次数对单次平仓和后续入场都有连锁影响。净利润排名可能依赖少数大盈利，判断时应同时看回撤、盈亏因子、交易量、亏损尾部和盈利集中度。</p></section>
<section><h2>只改变锁盈起点：每波1次，不用均线退出（1 tick）</h2><p class="note">这组直接显示开始锁n−1R由3R推迟到更远时，收益与回撤如何变化。</p>__LOCKTABLE__
<h3>净收益最高五组（1 tick）</h3>__PROFIT__
<h3>收益/回撤较高五组（1 tick，至少10笔平仓）</h3>__RATIO__
<p class="note">候选排序用于找到交易复盘对象，不能自动确定最终参数。还需要对照月份、最大单笔亏损、盈利集中度及多次开仓的波数。</p></section>
<section><h2>开仓次数与EMA退出对照</h2><h3>每波开仓次数对照（3R、无均线退出、1 tick）</h3>__ENTRYTABLE__<h3>EMA在3R启用后的锁盈起点对照（每波1次、1 tick）</h3>__EMATABLE__</section>
<section><h2>资金曲线</h2><p class="note">每15根正式K线取一点绘图以压缩文件大小；回撤和收益指标使用完整逐根曲线。按相同参数切换0/1 tick，并对照不启用均线退出和EMA21/55触发退出。</p>
<div class="controls"><label>锁盈起点 <select id="lock">__LOCKOPTS__</select></label><label>每波上限 <select id="entries"><option value="1">1次</option><option value="2">2次</option><option value="3">3次</option><option value="0">不限</option></select></label><label>均线激活 <select id="r"><option value="1">1R</option><option value="2">2R</option><option value="3" selected>3R</option></select></label><label>滑点 <select id="slip"><option value="1">1 tick</option><option value="0">0 tick</option></select></label></div><div id="legend" class="legend"></div><svg id="chart" viewBox="0 0 1100 400" role="img" aria-label="资金曲线比较"></svg><div id="hover" class="note">将鼠标移到曲线上查看权益。</div></section>
<section><h2>完整配置结果</h2><p class="note">文字筛选和表头排序可定位结果；输入 run ID 可在软件打开这次K线与成交复盘。</p><p><input id="search" type="search" placeholder="筛选参数或run ID" style="width:min(100%,360px)"></p><div class="scroll"><table id="results"><thead><tr>__HEAD__</tr></thead><tbody>__ROWS__</tbody></table></div></section>
<footer class="note">仅为SR701单合约、单段历史数据模拟，不代表未来表现。R触发按K线OHLC，无法还原K线内部先后顺序；均线退出和平仓沿用下一根有效K线开盘成交模型。</footer></main>
<script>const rows=__DATA__,curves=__CURVES__,C={none:"#2563eb",ema21:"#15926b",ema55:"#d84c55"};
function fmt(n){return n==null?"—":Number(n).toLocaleString("zh-CN",{maximumFractionDigits:2})}
function draw(){var lock=Number(document.getElementById("lock").value),cap=Number(document.getElementById("entries").value),r=Number(document.getElementById("r").value),slip=Number(document.getElementById("slip").value),modes=[["none","无均线止盈","none"],["ema21_r"+r,"EMA21 · "+r+"R","ema21"],["ema55_r"+r,"EMA55 · "+r+"R","ema55"]],series=[];
for(var m of modes){var k=lock+"|"+cap+"|"+m[0]+"|"+slip,row=rows.find(x=>x.key===k),curve=curves.find(x=>x.key===k);if(row&&curve)series.push({row:row,curve:curve,label:m[1],color:C[m[2]]})}
var svg=document.getElementById("chart");if(!series.length){svg.innerHTML="<text x='40' y='60'>无曲线数据</text>";return}
var W=1100,H=400,L=77,R=18,T=18,B=38,all=series.flatMap(s=>s.curve.points.map(p=>p[1])),lo=Math.min(100000,...all),hi=Math.max(100000,...all),pad=(hi-lo)*.08||1;lo-=pad;hi+=pad;
var x=i=>L+i*(W-L-R)/(series[0].curve.points.length-1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo),out=[];
for(var n=0;n<5;n++){var v=lo+(hi-lo)*n/4,yy=y(v),i=Math.round((series[0].curve.points.length-1)*n/4);out.push("<line x1='"+L+"' x2='"+(W-R)+"' y1='"+yy+"' y2='"+yy+"' stroke='#e7edf5'/><text x='"+(L-8)+"' y='"+(yy+4)+"' text-anchor='end' fill='#64748b' font-size='12'>"+Math.round(v).toLocaleString()+"</text><text x='"+x(i)+"' y='"+(H-8)+"' text-anchor='middle' fill='#64748b' font-size='11'>"+series[0].curve.points[i][0].slice(0,10)+"</text>")}
for(var s of series){var d=s.curve.points.map((p,i)=>(i?"L":"M")+x(i).toFixed(2)+","+y(p[1]).toFixed(2)).join(" ");out.push("<path d='"+d+"' fill='none' stroke='"+s.color+"' stroke-width='2'/>")}
svg.innerHTML=out.join("");document.getElementById("legend").innerHTML=series.map(s=>"<span style='--c:"+s.color+"'>"+s.label+" · 收益 "+fmt(s.row.net)+" / 回撤 "+fmt(s.row.dd)+"</span>").join("");
svg.onmousemove=function(e){var rect=svg.getBoundingClientRect(),i=Math.round((e.clientX-rect.left)/rect.width*(series[0].curve.points.length-1));i=Math.max(0,Math.min(i,series[0].curve.points.length-1));document.getElementById("hover").textContent=series[0].curve.points[i][0]+" · "+series.map(s=>s.label+"："+fmt(s.curve.points[i][1])+"元").join(" / ")}}
["lock","entries","r","slip"].forEach(id=>document.getElementById(id).addEventListener("change",draw));
document.getElementById("search").addEventListener("input",function(){var q=this.value.toLowerCase();for(var tr of document.querySelectorAll("#results tbody tr"))tr.hidden=!tr.textContent.toLowerCase().includes(q)});
document.querySelectorAll("#results th").forEach(function(th){th.addEventListener("click",function(){var i=Number(th.dataset.col),tb=th.closest("table").tBodies[0],rs=[...tb.rows],asc=th.dataset.asc!=="true";rs.sort((a,b)=>{var x=a.cells[i].textContent,y=b.cells[i].textContent,nx=Number(x.replace(/[,%]/g,"")),ny=Number(y.replace(/[,%]/g,""));return(Number.isFinite(nx)&&Number.isFinite(ny)?nx-ny:x.localeCompare(y,"zh-CN"))*(asc?1:-1)});rs.forEach(tr=>tb.appendChild(tr));th.dataset.asc=String(asc)})});draw();</script></body></html>""".replace("__ANALYSIS__",analysis).replace("__ENTRYTABLE__",entry_table).replace("__EMATABLE__",ema_table)

def main():
    if not CLI.exists(): raise FileNotFoundError(f"CLI not found: {CLI}")
    OUT.mkdir(parents=True,exist_ok=True)
    progress_path=OUT/"progress.json"
    progress=json.loads(progress_path.read_text(encoding="utf-8")) if progress_path.exists() else {"completed":[]}
    known={x["key"]:x for x in progress["completed"]}
    plan=list(configs());work=[(c,s) for s in (0,1) for c in plan]
    for index,(config,slip) in enumerate(work,1):
        k=run_key(config,slip)
        if k in known: continue
        print(f"[{index}/{len(work)}] {k}",flush=True)
        item=execute(config,slip);known[k]=item
        progress["completed"]=list(known.values());progress["updated_at"]=datetime.now().astimezone().isoformat()
        progress_path.write_text(json.dumps(progress,ensure_ascii=False,indent=2),encoding="utf-8")
        r=item["record"];print(f"  net={r['NetPnl']} dd={r['MaxDrawdown']} trades={r['ClosedTradeCount']} run={item['run_id']}",flush=True)
    if len(known)!=len(work): raise RuntimeError(f"Expected {len(work)} configurations; found {len(known)}")
    wave_map=ema_waves(ROOT/"artifacts"/"sr2701-15m"/"SR2701-15m.csv")
    rows=[summarize(item,wave_map) for _,item in sorted(known.items())]
    (OUT/"exit-entry-summary.json").write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding="utf-8")
    def f(v,d=0): return fmt(v,d)
    lock_rows=[]
    for r in sorted((x for x in rows if x["slip"]==1 and x["entries_limit"]==1 and x["ema"]==0),key=lambda x:x["lock_r"]):
        lock_rows.append((str(r["lock_r"])+"R",f(r["net"]),f(r["dd"]),r["closed"],f(r["win_rate"],1)+"%",f(r["pf"],2),f(r["net_ex_top3"])))
    lock_table="<div class='scroll'><table><thead><tr><th>锁盈起点</th><th>净利</th><th>回撤</th><th>笔数</th><th>胜率</th><th>盈亏因子</th><th>扣前三后</th></tr></thead><tbody>"+"".join("<tr>"+"".join("<td>"+escape(str(v))+"</td>" for v in row)+"</tr>" for row in lock_rows)+"</tbody></table></div>"
    def top(items,ratio=False):
        items=sorted(items,key=lambda x:x["net"]/max(x["dd"],1) if ratio else x["net"],reverse=True)[:5]
        return "<div class='scroll'><table><thead><tr><th>参数</th><th>净利</th><th>回撤</th><th>笔数</th><th>收益/回撤</th><th>盈亏因子</th><th>扣前三后</th><th>run ID</th></tr></thead><tbody>"+"".join(
            "<tr><td>"+escape(("不限" if x["entries_limit"]==0 else str(x["entries_limit"]))+"次 / "+str(x["lock_r"])+"R / "+("无EMA" if x["ema"]==0 else f"EMA{x['ema']}@{x['activation']}R"))+
            "</td><td>"+f(x["net"])+"</td><td>"+f(x["dd"])+"</td><td>"+str(x["closed"])+"</td><td>"+f(x["net"]/x["dd"] if x["dd"] else None,2)+
            "</td><td>"+f(x["pf"],2)+"</td><td>"+f(x["net_ex_top3"])+"</td><td>"+x["run_id"]+"</td></tr>" for x in items)+"</tbody></table></div>"
    heads=["每波上限","锁盈起点","均线退出","滑点","实际开仓","多次入场波数","单波最多","平仓","净利","回撤","盈亏因子","胜率","最大单笔净亏","EMA退出笔数","前三占盈利","扣前三后","run ID"]
    htmlrows=[]
    for r in rows:
        mode="不启用" if r["ema"]==0 else f"EMA{r['ema']}@{r['activation']}R"
        vals=["不限" if r["entries_limit"]==0 else r["entries_limit"],f"{r['lock_r']}R",mode,f"{r['slip']}tick",r["open_fills"],r["multi_entry_waves"],r["max_in_wave"],r["closed"],f(r["net"]),f(r["dd"]),f(r["pf"],2),f(r["win_rate"],1)+"%",f(r["max_loss"]),r["ema_exits"],f(r["top3_win_share"],1)+"%",f(r["net_ex_top3"]),r["run_id"]]
        htmlrows.append("<tr>"+"".join("<td>"+escape(str(v))+"</td>" for v in vals)+"</tr>")
    curves=[]
    for r in rows:
        points=r["equity"];sampled=points[::15]
        if points and sampled[-1]!=points[-1]:sampled.append(points[-1])
        curves.append({"key":r["key"],"points":sampled})
    data=json.dumps([{k:v for k,v in r.items() if k!="equity"} for r in rows],ensure_ascii=False,separators=(",",":"))
    curve_json=json.dumps(curves,ensure_ascii=False,separators=(",",":"))
    report=make_report(rows).replace("__TIME__",datetime.now().astimezone().isoformat(timespec="seconds"))
    report=report.replace("__LOCKTABLE__",lock_table).replace("__PROFIT__",top([x for x in rows if x["slip"]==1]))
    report=report.replace("__RATIO__",top([x for x in rows if x["slip"]==1 and x["closed"]>=10],ratio=True))
    report=report.replace("__LOCKOPTS__","".join(f"<option value='{x}'{' selected' if x==4 else ''}>{x}R</option>" for x in LOCKS))
    report=report.replace("__HEAD__","".join(f"<th data-col='{i}'>{escape(v)}</th>" for i,v in enumerate(heads)))
    report=report.replace("__ROWS__","".join(htmlrows)).replace("__DATA__",data).replace("__CURVES__",curve_json)
    path=OUT/"exit-entry-study-report.html";path.write_text(report,encoding="utf-8")
    print(f"Report={path}",flush=True);print(f"Completed {len(rows)} official replays; baseline 3R/one-entry/no-EMA/one-tick net={next(x['net'] for x in rows if x['key']=='3|1|none|1')}",flush=True)
    return 0

if __name__=="__main__": sys.exit(main())
