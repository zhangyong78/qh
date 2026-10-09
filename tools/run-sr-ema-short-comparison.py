"""Replay the two requested short entry EMAs and report against the long reference."""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import re
import runpy
import sqlite3
import statistics
import subprocess
from datetime import datetime, timedelta, timezone
from html import escape
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
HELPERS = runpy.run_path(str(ROOT / "tools" / "run-sr-atr14-matrix.py"))
read_jsonl = HELPERS["read_jsonl"]
iso_dt = HELPERS["iso_dt"]
finite = HELPERS["finite"]
START, END = HELPERS["START"], HELPERS["END"]
SNAPSHOT = HELPERS["TIMEFRAMES"]["15m"]
CLI = ROOT / "src" / "Qh.Cli" / "bin" / "Release" / "net10.0" / "Qh.Cli.dll"
BASE_PARAMS = {
    "ema_period": 21, "trend_ema_period": 55, "atr_period": 14, "atr_stop_multiplier": 2,
    "protection_variant": "two_r_n_minus_one", "max_entries_per_trend": 1, "min_tick": 1,
    "dynamic_fee_offset_points": 1, "risk_budget": 1000, "contract_multiplier": 10,
    "profit_lock_start_r": 3, "ema_exit_period": 0, "ema_exit_activation_r": 2,
}


def timestamp(raw):
    try:
        return iso_dt(raw)
    except ValueError:
        return datetime.strptime(raw, "%Y/%m/%d %H:%M:%S").replace(tzinfo=timezone(timedelta(hours=8)))


def run_one(data_root, output, direction, ema, slippage):
    strategy = ROOT / "strategies" / f"ema_dynamic_{direction}_trade.py"
    args = ["dotnet", str(CLI), "trade-replay", str(data_root), SNAPSHOT, str(strategy), START, END,
            "--initial-equity=100000", "--lots=1", "--multiplier=10", "--min-tick=1", "--margin-rate=0.1",
            "--fee-mode=Fixed", "--open-fee=2", "--today-fee=3", "--ordinary-fee=1", f"--slippage-ticks={slippage}",
            *[f"{key}={value}" for key, value in BASE_PARAMS.items()], f"entry_ema_period={ema}"]
    result = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=240)
    match = re.search(r"run_id=([^;\s]+)", result.stdout)
    if result.returncode or match is None:
        log = output / f"failed-{direction}-{ema}-{slippage}.log"
        log.write_text(result.stdout + "\n" + result.stderr, encoding="utf-8")
        raise RuntimeError(f"回放失败：{log}")
    record = json.loads((data_root / "trade-runs" / match[1] / "run.json").read_text(encoding="utf-8-sig"))
    if record["Status"] != "completed":
        raise RuntimeError(f"回放未完成：{match[1]} {record.get('ErrorMessage')}")
    return {"direction": direction, "entry_ema": ema, "slippage_ticks": slippage, "run_id": match[1], "record": record}


def summarize(item, data_root, atr_map, bar_by_end, entry_mode="limit"):
    run_dir = data_root / "trade-runs" / item["run_id"]
    record = item["record"]
    fills = read_jsonl(run_dir / "fills.jsonl")
    trades = read_jsonl(run_dir / "closed-trades.jsonl")
    intents = {row["Sequence"]: row for row in read_jsonl(run_dir / "intents.jsonl")}
    equities = read_jsonl(run_dir / "equity.jsonl")
    fills_by_seq = {row["FillSequence"]: row for row in fills}
    short = item["direction"] == "short"
    sign = -1 if short else 1
    planned, risks, sizes = [], {}, []
    limit_modes = {"at_open": 0, "intrabar_touch": 0}
    for fill in fills:
        assert fill["Direction"] == item["direction"]
        bar = bar_by_end[iso_dt(fill["BarEnd"])]
        op, hi, lo = (float(bar[key]) for key in ("open", "high", "low"))
        if fill["Action"] == "open":
            intent = intents[fill["IntentSequence"]]
            reference = float(intent["ReferencePrice"])
            atr_distance = atr_map[intent["SignalBarStart"]] * float(record["Parameters"]["atr_stop_multiplier"])
            planned_distance = math.ceil(reference + atr_distance - 1e-12) - reference if short else reference - math.floor(reference - atr_distance + 1e-12)
            quantity = int(fill["Quantity"])
            assert quantity == math.floor(1000 / (max(planned_distance, 1) * 10))
            planned.append(quantity * (max(planned_distance, 1) * 10))
            sizes.append(quantity)
            entry_price = float(fill["Price"])
            stop = math.ceil(entry_price + atr_distance - 1e-12) if short else math.floor(entry_price - atr_distance + 1e-12)
            risks[fill["FillSequence"]] = max(abs(stop - entry_price), 1) * 10 * quantity
            at_open = op >= reference if short else op <= reference
            touched = hi >= reference if short else lo <= reference
            if entry_mode == "market":
                expected = op + sign * item["slippage_ticks"]
                assert entry_price == expected
                at_open = True
            else:
                assert at_open or touched
                expected = (max(op - item["slippage_ticks"], reference) if short else min(op + item["slippage_ticks"], reference)) if at_open else reference
                assert entry_price == expected and (entry_price >= reference if short else entry_price <= reference)
            assert float(fill["Fee"]) == quantity * 2
            limit_modes["at_open" if at_open else "intrabar_touch"] += 1
        else:
            intent = intents[fill["IntentSequence"]]
            base = op
            if intent["Action"] == "close_stop":
                evidence = json.loads(intent["RawJson"])
                stop = evidence["protectedStop"]
                assert hi >= stop if short else lo <= stop
                source = intents[evidence["protectionIntentSequence"]]
                assert iso_dt(source["SignalBarEnd"]) <= timestamp(bar["timestamp"])
                base = max(op, stop) if short else min(op, stop)
                assert float(intent["ReferencePrice"]) == base
            assert float(fill["Price"]) == base - sign * item["slippage_ticks"]
    for trade in trades:
        quantity = int(trade["Quantity"])
        gross = sign * (float(trade["ExitPrice"]) - float(trade["EntryPrice"])) * 10 * quantity
        assert gross == float(trade["GrossPnl"])
        fee = quantity * (2 + (3 if trade["EntryTradingDay"] == trade["ExitTradingDay"] else 1))
        assert float(trade["Fees"]) == fee and float(trade["NetPnl"]) == gross - fee
    wins = sorted([float(row["NetPnl"]) for row in trades if row["NetPnl"] > 0], reverse=True)
    losses = [float(row["NetPnl"]) for row in trades if row["NetPnl"] < 0]
    dd = HELPERS["max_drawdown_stats"](equities, 100000)
    assert dd["max_drawdown"] == float(record["MaxDrawdown"])
    assert len(trades) == int(record["ClosedTradeCount"])
    assert max(planned, default=0) <= 1000
    monthly = HELPERS["monthly_net"](trades, fills_by_seq)
    trade_rows = []
    for trade in trades:
        entry = fills_by_seq[trade["EntryFillSequence"]]
        exit_fill = fills_by_seq[trade["ExitFillSequence"]]
        trade_rows.append({
            "entry_bar": timestamp(bar_by_end[iso_dt(entry["BarEnd"])]["timestamp"]).isoformat(),
            "exit_bar": timestamp(bar_by_end[iso_dt(exit_fill["BarEnd"])]["timestamp"]).isoformat(),
            "quantity": trade["Quantity"], "entry_price": trade["EntryPrice"], "exit_price": trade["ExitPrice"],
            "net_pnl": trade["NetPnl"], "risk_amount": risks[trade["EntryFillSequence"]],
            "r": float(trade["NetPnl"]) / risks[trade["EntryFillSequence"]],
        })
    unfilled = read_jsonl(run_dir / "unfilled.jsonl")
    closed_entries = {trade["EntryFillSequence"] for trade in trades}
    live_entries = [fill for fill in fills if fill["Action"] == "open" and fill["FillSequence"] not in closed_entries]
    closed_net = sum(float(trade["NetPnl"]) for trade in trades)
    live_entry_fees = sum(float(fill["Fee"]) for fill in live_entries)
    end_float = float(equities[-1]["FloatingPnl"])
    assert closed_net + end_float - live_entry_fees == float(record["NetPnl"])
    return {
        "key": f"{item['direction']}|{item['entry_ema']}|{item['slippage_ticks']}",
        "direction": item["direction"], "entry_ema": item["entry_ema"], "slippage_ticks": item["slippage_ticks"], "run_id": item["run_id"],
        "net_pnl": float(record["NetPnl"]), "final_equity": float(record["FinalEquity"]), "fees": float(record["TotalFees"]),
        "max_drawdown": dd["max_drawdown"], "drawdown_pct": dd["drawdown_pct"], "recovery_days": dd["recovery_days"],
        "closed_trades": len(trades), "win_rate": float(record["WinRate"] or 0),
        "open_count": len(sizes), "closed_net_pnl": closed_net, "live_entry_fees": live_entry_fees,
        "end_open_quantity": sum(int(fill["Quantity"]) for fill in live_entries),
        "profit_factor": sum(wins) / abs(sum(losses)) if losses else None,
        "max_loss": min(losses, default=0), "avg_r": statistics.mean(t["r"] for t in trade_rows) if trade_rows else None,
        "risk_over_count": sum(value < -1000 for value in losses), "risk_over_amount": sum(max(0, -value - 1000) for value in losses),
        "planned_risk_avg": statistics.mean(planned) if planned else None, "avg_lots": statistics.mean(sizes) if sizes else None,
        "min_lots": min(sizes, default=0), "max_lots": max(sizes, default=0),
        "risk_budget_skips": sum(s.get("Category") == "entry_skipped_risk_budget" for s in read_jsonl(run_dir / "signals.jsonl")),
        "margin_rejections": sum(row.get("reason") in {"insufficient_funds", "margin_exceeded"} for row in unfilled),
        "unfilled_limits": sum(row.get("reason") == "limit_not_touched" for row in unfilled),
        "limit_fill_modes": limit_modes, "monthly_net": monthly, "negative_months": sum(v < 0 for v in monthly.values()),
        "top3_winner_share": sum(wins[:3]) / sum(wins) * 100 if wins else None,
        "net_excluding_top3": float(record["NetPnl"]) - sum(wins[:3]),
        "end_open_pnl": end_float,
        "equity": [[row["BarEnd"], float(row["Equity"])] for row in equities if iso_dt(START) < iso_dt(row["BarEnd"]) <= iso_dt(END)],
        "trades": trade_rows,
    }


def report_html(rows):
    def name(row):
        return f"{'做空' if row['direction']=='short' else '做多参考'} EMA{row['entry_ema']}"
    def table(headers, values):
        return '<div class="scroll"><table><thead><tr>' + ''.join(f'<th>{escape(x)}</th>' for x in headers) + '</tr></thead><tbody>' + ''.join(
            '<tr>' + ''.join(f'<td>{escape(str(v))}</td>' for v in line) + '</tr>' for line in values) + '</tbody></table></div>'
    pressure = [r for r in rows if r["slippage_ticks"] == 1]
    risk_table = table(["策略", "净收益元", "最大回撤元", "平仓数", "胜率%", "盈亏因子", "最大单笔净亏元", "平均手数", "前三笔占盈利%"],
                       [(name(r), finite(r["net_pnl"], 0), finite(r["max_drawdown"], 0), r["closed_trades"], finite(r["win_rate"], 1), finite(r["profit_factor"]),
                         finite(abs(r["max_loss"]), 0), finite(r["avg_lots"]), finite(r["top3_winner_share"], 1)) for r in pressure])
    all_table = table(["策略", "滑点", "净收益元", "回撤元", "平均R", "平均计划风险元", "手数范围", "超预算亏损笔数", "不足一手跳过", "保证金拒单", "期末浮盈元", "run ID"],
                      [(name(r), r["slippage_ticks"], finite(r["net_pnl"], 0), finite(r["max_drawdown"], 0), finite(r["avg_r"]), finite(r["planned_risk_avg"]),
                        f"{r['min_lots']}–{r['max_lots']}", r["risk_over_count"], r["risk_budget_skips"], r["margin_rejections"], finite(r["end_open_pnl"], 0), r["run_id"]) for r in rows])
    short_rows = [r for r in pressure if r["direction"] == "short"]
    s21 = next(r for r in short_rows if r["entry_ema"] == 21)
    s55 = next(r for r in short_rows if r["entry_ema"] == 55)
    decision = f'''<p class="callout"><b>本样本的取舍：</b>若以减少资金回撤为重点，先把 EMA55 挂单作为空头候选，EMA21 保留作对照。1 tick 下 EMA55 少赚 {finite(s21['net_pnl']-s55['net_pnl'], 0)} 元，回撤减少 {finite(s21['max_drawdown']-s55['max_drawdown'], 0)} 元（{finite((1-s55['max_drawdown']/s21['max_drawdown'])*100, 1)}%）；净收益/最大回撤由 {finite(s21['net_pnl']/s21['max_drawdown'])} 提升至 {finite(s55['net_pnl']/s55['max_drawdown'])}。</p>
<p><b>但 EMA55 并非每项风险都更好。</b>它超预算亏损 {s55['risk_over_count']} 笔，EMA21 为 {s21['risk_over_count']} 笔；最大单笔亏损仅少 {finite(abs(s21['max_loss'])-abs(s55['max_loss']), 0)} 元，胜率也较低。两组各开仓 {s21['open_count']} 次，EMA55 期末仍有 {s55['end_open_quantity']} 手空单，因此只有 {s55['closed_trades']} 笔平仓；不能将这个差异理解为少开了一次仓。两组账面扣除前三笔盈利后均为负，做空盈利对大趋势交易的依赖比推荐多头更高。</p>'''
    presets = {str(ema): {**BASE_PARAMS, "entry_ema_period": ema} for ema in (21, 55)}
    sections = []
    for r in short_rows:
        zero = next(x for x in rows if x["key"] == f"short|{r['entry_ema']}|0")
        sections.append(f"<h3>{name(r)}</h3><p>1 tick 下净收益 {finite(r['net_pnl'], 0)} 元，回撤 {finite(r['max_drawdown'], 0)} 元；相对零滑点净收益变化 {finite(r['net_pnl']-zero['net_pnl'], 0)} 元。平仓 {r['closed_trades']} 笔，亏损月份 {r['negative_months']}/8，最大单笔净亏 {finite(abs(r['max_loss']), 0)} 元，{r['risk_over_count']} 笔超过 1,000 元风险预算。前三笔盈利占全部盈利 {finite(r['top3_winner_share'], 1)}%；账面扣除前三笔盈利后为 {finite(r['net_excluding_top3'], 0)} 元。回撤高点恢复：{finite(r['recovery_days'], 1)+' 天' if r['recovery_days'] is not None else '期末尚未恢复'}。</p>")
    monthly = table(["月份"] + [name(r) for r in pressure],
                    [(f"2026-{month:02}", *[finite(r["monthly_net"].get(f"2026-{month:02}", 0), 0) for r in pressure]) for month in range(2, 10)])
    trade_tables = []
    for r in short_rows:
        trade_tables.append(f'<h3>{name(r)} · 1 tick 逐笔平仓</h3>' + table(["入场K线起点", "退出K线起点", "手数", "开仓价", "平仓价", "单笔1R元", "净损益元", "净R"],
            [(t["entry_bar"], t["exit_bar"], t["quantity"], t["entry_price"], t["exit_price"], finite(t["risk_amount"], 0), finite(t["net_pnl"], 0), finite(t["r"])) for t in r["trades"]]))
    data = json.dumps(rows, ensure_ascii=False, separators=(",", ":"))
    return f'''<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖15分钟：EMA21 / EMA55 做空对比</title>
<style>body{{margin:0;background:#f3f6fb;color:#18304b;font:15px/1.65 "Microsoft YaHei",sans-serif}}header{{padding:28px max(20px,calc((100vw - 1240px)/2));background:#143659;color:white}}h1{{margin:0;font-size:27px}}main{{max-width:1240px;margin:22px auto;padding:0 18px 40px}}section{{padding:22px;background:white;border:1px solid #d6e1ef;border-radius:12px;margin-bottom:18px}}h2{{font-size:21px;margin:0 0 12px}}h3{{font-size:17px}}.callout{{background:#ecf4ff;border-left:4px solid #2563eb;padding:12px}}.note{{font-size:13px;color:#61758d}}.scroll{{overflow:auto;max-height:560px}}table{{width:100%;border-collapse:collapse;font-size:13px}}th,td{{padding:8px;border-bottom:1px solid #dce5f1;white-space:nowrap;text-align:right}}th{{background:#edf3fb;position:sticky;top:0}}td:first-child,th:first-child{{text-align:left}}svg{{width:100%;height:350px}}.legend{{display:flex;gap:18px;flex-wrap:wrap}}.legend span{{font-size:13px}}select{{padding:7px;margin:10px;border:1px solid #a4b9d0;border-radius:5px}}</style></head>
<body><header><h1>白糖 SR701 · 15 分钟做空逻辑与挂单 EMA 对比</h1><p>EMA21 / EMA55 两组 · ATR14×2 · 2R 保本 / 3R 起锁 n−1R · 风险预算 1,000 元</p></header><main>
<section><h2>两组已实现：只改变挂单参考 EMA</h2><p class="callout">空头趋势条件均为 EMA21&lt;EMA55 且收盘&lt;EMA55；第一组在 EMA21 挂卖出限价单，第二组在 EMA55 挂卖出限价单。未触及则下一根按新的 EMA 更新委托，每个空头趋势最多开仓一次，EMA21≥EMA55 后重置次数。</p>
<p>保持主选逻辑：ATR14×2、2R 保本、3R 起锁 n−1R、本金 100,000 元、风险金 1,000 元、合约乘数 10、最小跳动 1 点。开仓按止损点数×乘数确定手数，手续费不从开仓风险预算中预留；移动保护止损时加入双向手续费估算和1跳缓冲。</p>
<p><b>使用软件：</b>选择“EMA 动态委托做空（EMA21 / EMA55）”，加载并校验；“入场 EMA 根数”分别选择 21、55，手数倍率保持 1。策略默认 ATR14×2 和新保护，默认挂单 EMA55。交易设置的本金填 100,000 元；滑点填 1 跳复现压力结果，填 0 跳复现零滑点结果；区间和手续费按下方回测口径设置。</p>
<p><a href="ema21-parameters.json">EMA21 参数 JSON</a> · <a href="ema55-parameters.json">EMA55 参数 JSON</a></p><details><summary>展开两组策略参数</summary><pre>{escape(json.dumps(presets, ensure_ascii=False, indent=2))}</pre></details>
</section><section><h2>空头止损与保护公式</h2>{table(['条件','空头规则'], [('初始止损','S₀ = ceil_tick(E + ATR14×2)，R = S₀−E，成交后冻结'),('仓位','floor[1,000 ÷（计划止损点数×10 + 25）]；不足一手跳过'),('到2R','最低价 ≤ E−2R−F；保护价降到 floor_tick(E−F)'),('从3R起','n = floor[(E−最低价−F)/R]，n≥3 时保护价降到 floor_tick[E−(n−1)R−F]'),('触及保护','已完成K线最高价≥此前生效保护价；下一根有效K线开盘买入平仓'),('单向保护','保护价只降不升；新开仓所在K线不管理；旧保护触及优先于当根提升')])}
<p class="note">E 为实际入场价，F=1 点的手续费偏移；2R / 3R 触发同样含 F。例如 E=5,300、R=20：低点到 5,259 触发保本，保护降到 5,299；低点到 5,239 触发 3R，保护降到 5,259。保护更新在完整K线后生效，不能推断该K线内高低价先后。停盘后的下一根开盘成交仍可能超预算，保本价不保证实际净盈亏非负。</p></section>
<section><h2>同一口径的 1 tick 回测对比</h2>{risk_table}<p class="note">正式区间 2026-02-02 21:00 至 2026-10-01 00:00，固定 15 分钟快照 {SNAPSHOT}，共 3,640 根正式K线。开仓费 2 元/手，平今 3 元/手、平昨 1 元/手，保证金率 10%。每条曲线是独立的 10 万元账户，不代表多空组合账户。</p>{decision}
<p><b>EMA55 期末持仓口径：</b>已平仓净收益 {finite(s55['closed_net_pnl'], 0)} 元，期末浮盈亏 {finite(s55['end_open_pnl'], 0)} 元，未平仓单已付开仓费 {finite(s55['live_entry_fees'], 0)} 元，合计账户净收益 {finite(s55['net_pnl'], 0)} 元。未强制平仓，未计尚未发生的平仓费；此后持仓盈亏不在本轮区间内。</p>
{''.join(sections)}<p>对称规则是否适合做空，要看空头样本本身；多头参数表现较好并不证明反向也有优势。EMA21 位于下降趋势中更靠近当前价格，EMA55 通常要求更深回抽；两者会改变实际成交次数、入场价、止损和后续趋势内的入场机会。不能把未成交委托数量直接解释为有效机会损失。</p>
<p class="note">前三笔扣除是盈利集中度诊断，未删除交易后重跑；每月按平仓日期归集，权益曲线含期末浮盈。本报告只检验用户要求的两组，未优化新的空头 ATR 或止盈参数。</p></section>
<section><h2>资金曲线与回撤曲线</h2><label>滑点<select id="slip"><option value="1">1 tick</option><option value="0">0 tick</option></select></label><div id="legend" class="legend"></div><svg id="equity" viewBox="0 0 1100 350" role="img" aria-label="做空两组与做多参考资金曲线"></svg><div id="hover" class="note">鼠标移动显示同根K线的三组权益。</div><h3>权益从历史高点回撤金额</h3><svg id="dd" viewBox="0 0 1100 350" role="img" aria-label="最大回撤过程"></svg></section>
<section><h2>逐月已平仓净损益 · 1 tick</h2>{monthly}</section><section><h2>全部参数结果及复现记录</h2>{all_table}</section><section><h2>逐笔交易 · 1 tick</h2><p class="note">“入场K线起点”是发生限价成交的K线时间；触价成交在该K线内的具体时刻无法由OHLC确定。“退出K线起点”是实际模拟平仓的开盘时刻。</p>{''.join(trade_tables)}</section>
<p class="note">新增短策略及限价撮合已在当前工作树构建并验证；旧做多代表配置复跑的净收益、最大回撤和成交记录已核对一致。桌面旧入口指向原目录时，需要使用本工作树构建版本才能选择新增策略。</p></main>
<script>const rows={data};const colors={{'long|55':'#7b61a8','short|21':'#2563eb','short|55':'#e45757'}};
const fmt=v=>Number(v).toLocaleString('zh-CN',{{maximumFractionDigits:2}});
function plot(id,series){{let svg=document.getElementById(id),W=1100,H=350,L=85,R=20,T=20,B=38;let vals=series.flatMap(s=>s.points.map(p=>p[1])),lo=Math.min(0,...vals),hi=Math.max(...vals);if(id==='equity')lo=Math.min(...vals,100000);let pad=(hi-lo)*.07||1;lo=id==='dd'?0:lo-pad;hi+=pad;let x=i=>L+i*(W-L-R)/(series[0].points.length-1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo),out=[];
for(let n=0;n<5;n++){{let v=lo+(hi-lo)*n/4,yy=y(v),i=Math.round((series[0].points.length-1)*n/4);out.push(`<line x1="${{L}}" x2="${{W-R}}" y1="${{yy}}" y2="${{yy}}" stroke="#e0e8f2"/><text x="${{L-8}}" y="${{yy+4}}" text-anchor="end" fill="#61758d" font-size="12">${{fmt(Math.round(v))}}</text><text x="${{x(i)}}" y="${{H-10}}" text-anchor="middle" fill="#61758d" font-size="12">${{series[0].points[i][0].slice(0,10)}}</text>`)}}
for(const s of series){{let d=s.points.map((p,i)=>(i?'L':'M')+x(i).toFixed(2)+','+y(p[1]).toFixed(2)).join(' ');out.push(`<path d="${{d}}" fill="none" stroke="${{s.color}}" stroke-width="2"/>`)}}svg.innerHTML=out.join('');if(id==='equity')svg.onmousemove=e=>{{let rect=svg.getBoundingClientRect(),i=Math.round(((e.clientX-rect.left)/rect.width*W-L)/(W-L-R)*(series[0].points.length-1));i=Math.max(0,Math.min(i,series[0].points.length-1));document.getElementById('hover').textContent=series[0].points[i][0]+' · '+series.map(s=>s.label+' '+fmt(s.points[i][1])).join(' / ')}};}}
function draw(){{let picked=rows.filter(r=>r.slippage_ticks===Number(document.getElementById('slip').value)),series=picked.map(r=>({{label:(r.direction==='short'?'做空':'做多参考')+' EMA'+r.entry_ema,color:colors[r.direction+'|'+r.entry_ema],points:r.equity,row:r}}));document.getElementById('legend').innerHTML=series.map(s=>`<span style="color:${{s.color}}">${{s.label}} · 净利 ${{fmt(s.row.net_pnl)}} / 回撤 ${{fmt(s.row.max_drawdown)}}</span>`).join('');plot('equity',series);plot('dd',series.map(s=>{{let peak=100000;return{{...s,points:s.points.map(([t,v])=>{{peak=Math.max(peak,v);return[t,peak-v]}})}}}}));}}
document.getElementById('slip').addEventListener('change',draw);draw();</script></body></html>'''


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--data-root", type=Path, required=True)
    args = parser.parse_args()
    data_root = args.data_root.resolve()
    output = data_root / "short-ema-comparison-20261008"
    output.mkdir(parents=True, exist_ok=True)
    with sqlite3.connect(data_root / "index.sqlite") as connection:
        source = Path(connection.execute("SELECT source_path FROM snapshots WHERE snapshot_id=?", (SNAPSHOT,)).fetchone()[0])
    if not source.is_absolute():
        source = data_root.parents[2] / source
    atr_map = HELPERS["load_atr_map"](source, 14)
    with source.open(encoding="utf-8-sig", newline="") as stream:
        bars = list(csv.DictReader(stream))
    bar_by_end = {timestamp(row["timestamp"]) + timedelta(minutes=15): row for row in bars}
    fingerprint = hashlib.sha256((ROOT / "strategies" / "ema_dynamic_short_trade.py").read_bytes() +
                                 (ROOT / "strategies" / "ema_dynamic_long_trade.py").read_bytes() +
                                 (ROOT / "src" / "Qh.Core" / "bin" / "Release" / "net10.0" / "Qh.Core.dll").read_bytes()).hexdigest()
    progress_path = output / "progress.json"
    progress = json.loads(progress_path.read_text(encoding="utf-8")) if progress_path.exists() else {}
    known = progress.get("completed", {}) if progress.get("fingerprint") == fingerprint else {}
    plan = [("short", ema, slip) for slip in (0, 1) for ema in (21, 55)] + [("long", 55, 1)]
    for direction, ema, slip in plan:
        key = f"{direction}|{ema}|{slip}"
        if key not in known:
            print(f"Replay {key}", flush=True)
            known[key] = run_one(data_root, output, direction, ema, slip)
            progress_path.write_text(json.dumps({"fingerprint": fingerprint, "completed": known}, ensure_ascii=False, indent=2), encoding="utf-8")
            print(f"  net={known[key]['record']['NetPnl']} dd={known[key]['record']['MaxDrawdown']} trades={known[key]['record']['ClosedTradeCount']}", flush=True)
    previous = json.loads((data_root / "atr14-matrix-20261008" / "progress.json").read_text(encoding="utf-8"))
    references = {r["key"]: r for r in previous["completed"]}
    original = references["15m|14|2|two_r_n_minus_one|1"]
    rerun = known["long|55|1"]
    for metric in ("NetPnl", "MaxDrawdown", "ClosedTradeCount", "FinalEquity", "TotalFees"):
        assert original["record"][metric] == rerun["record"][metric], f"long regression: {metric}"
    for filename in ("fills.jsonl", "closed-trades.jsonl", "equity.jsonl"):
        old = [{k: v for k, v in r.items() if k != "RunId"} for r in read_jsonl(data_root / "trade-runs" / original["run_id"] / filename)]
        new = [{k: v for k, v in r.items() if k != "RunId"} for r in read_jsonl(data_root / "trade-runs" / rerun["run_id"] / filename)]
        assert old == new, f"long regression: {filename}"
    baseline_zero = references["15m|14|2|two_r_n_minus_one|0"]
    items = [{"direction": "long", "entry_ema": 55, "slippage_ticks": 0, "run_id": baseline_zero["run_id"], "record": baseline_zero["record"]}] + list(known.values())
    rows = sorted([summarize(item, data_root, atr_map, bar_by_end) for item in items], key=lambda r: (r["slippage_ticks"], r["direction"], r["entry_ema"]))
    assert all(len(r["equity"]) == 3640 for r in rows)
    (output / "comparison-summary.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
    for ema in (21, 55):
        (output / f"ema{ema}-parameters.json").write_text(json.dumps({**BASE_PARAMS, "entry_ema_period": ema}, ensure_ascii=False, indent=2), encoding="utf-8")
    report = output / "short-ema-report.html"
    report.write_text(report_html(rows), encoding="utf-8")
    print("PASS: short fill prices, risk sizing, gross/net PnL, fees, drawdown; long fills/trades/equity unchanged.")
    print(f"Report: {report}")


if __name__ == "__main__":
    main()
