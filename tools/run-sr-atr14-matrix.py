"""Run the approved SR701 ATR14/exit-protection matrix and export its HTML report."""
from __future__ import annotations

import csv
import json
import math
import sqlite3
import statistics
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from html import escape
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DATA_ROOT = ROOT / "artifacts" / "sr2701-15m" / "wenhua-aligned-data"
OUTPUT = DATA_ROOT / "atr14-matrix-20261008"
CLI = ROOT / "src" / "Qh.Cli" / "bin" / "Release" / "net10.0" / "Qh.Cli.dll"
STRATEGY = ROOT / "strategies" / "ema_dynamic_long_trade.py"
START = "2026-02-02T21:00:00+08:00"
END = "2026-10-01T00:00:00+08:00"
INITIAL = 100000.0
MULTIPLIER = 10.0
RISK_BUDGET = 1000.0
PROFILES = {
    "current": "A 现有保护：1R保本；4R起锁n−3R；11R起锁n−1R",
    "two_r_old_ladder": "B 过渡对照：2R保本；现有阶梯锁盈",
    "two_r_n_minus_one": "C 新规则：2R保本；3R起锁n−1R",
}
TIMEFRAMES = {
    "15m": "snapshot-37c8f4717a61ae49",
    "30m": "snapshot-d79d4f83ed97b7fa",
    "60m": "snapshot-b226df470f4b4de3",
}


def load_sources() -> dict[str, Path]:
    with sqlite3.connect(DATA_ROOT / "index.sqlite") as connection:
        source_by_id = dict(connection.execute("SELECT snapshot_id, source_path FROM snapshots"))
    sources = {}
    for timeframe, snapshot_id in TIMEFRAMES.items():
        source = Path(source_by_id[snapshot_id])
        if not source.is_absolute():
            source = (ROOT / source).resolve()
        if not source.exists():
            raise FileNotFoundError(f"行情源文件不存在：{source}")
        sources[timeframe] = source
    return sources


def load_atr_map(path: Path, period: int = 14) -> dict[str, float]:
    with path.open(encoding="utf-8-sig", newline="") as stream:
        bars = list(csv.DictReader(stream))
    trs: list[float] = []
    previous_close = None
    atr = None
    result = {}
    for row in bars:
        high, low, close = (float(row[key]) for key in ("high", "low", "close"))
        tr = high - low if previous_close is None else max(high - low, abs(high - previous_close), abs(low - previous_close))
        trs.append(tr)
        if len(trs) == period:
            atr = sum(trs) / period
        elif len(trs) > period:
            atr = (atr * (period - 1) + tr) / period
        if atr is not None:
            raw_timestamp = row["timestamp"].replace("Z", "+00:00")
            try:
                ts = datetime.fromisoformat(raw_timestamp)
            except ValueError:
                ts = datetime.strptime(raw_timestamp, "%Y/%m/%d %H:%M:%S").replace(tzinfo=timezone(timedelta(hours=8)))
            result[ts.isoformat(timespec="seconds")] = atr
        previous_close = close
    return result


def configs():
    for timeframe, snapshot in TIMEFRAMES.items():
        for atr_period, multiplier in [(10, 2.0)] + [(14, value) for value in (1.0, 1.5, 2.0)]:
            variants = ["current"] if atr_period == 10 else list(PROFILES)
            for profile in variants:
                yield {
                    "timeframe": timeframe,
                    "snapshot_id": snapshot,
                    "atr_period": atr_period,
                    "atr_multiplier": multiplier,
                    "profile": profile,
                }


def config_key(config: dict, slippage: int) -> str:
    return "|".join((config["timeframe"], str(config["atr_period"]), f"{config['atr_multiplier']:g}", config["profile"], str(slippage)))


def run_one(config: dict, slippage: int) -> dict:
    key = config_key(config, slippage)
    params = {
        "ema_period": 21,
        "trend_ema_period": 55,
        "entry_ema_period": 55,
        "atr_period": config["atr_period"],
        "atr_stop_multiplier": config["atr_multiplier"],
        "protection_variant": config["profile"],
        "max_entries_per_trend": 1,
        "min_tick": 1,
        "dynamic_fee_offset_points": 1,
        "risk_budget": RISK_BUDGET,
        "contract_multiplier": MULTIPLIER,
    }
    args = [
        "dotnet", str(CLI), "trade-replay", str(DATA_ROOT), config["snapshot_id"], str(STRATEGY), START, END,
        "--initial-equity=100000", "--lots=1", "--multiplier=10", "--min-tick=1", "--margin-rate=0.1",
        "--fee-mode=Fixed", "--open-fee=2", "--today-fee=3", "--ordinary-fee=1", f"--slippage-ticks={slippage}",
        *[f"{name}={value}" for name, value in params.items()],
    ]
    completed = subprocess.run(args, cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=240)
    if completed.returncode != 0:
        log_path = OUTPUT / f"failed-{key.replace('|', '-')}.log"
        log_path.write_text(completed.stdout + "\n--- STDERR ---\n" + completed.stderr, encoding="utf-8")
        raise RuntimeError(f"配置 {key} 运行失败，详见 {log_path}: {completed.stderr[-1000:]}")
    run_id = next((line.split("run_id=", 1)[1].split(";", 1)[0] for line in completed.stdout.splitlines() if "run_id=" in line), None)
    if not run_id:
        raise RuntimeError(f"配置 {key} 没有返回 run ID：{completed.stdout[-1500:]}")
    run_dir = DATA_ROOT / "trade-runs" / run_id
    record = json.loads((run_dir / "run.json").read_text(encoding="utf-8-sig"))
    if record.get("Status") != "completed":
        raise RuntimeError(f"配置 {key} 状态不是 completed：{record.get('Status')} {record.get('ErrorMessage')}")
    return {"key": key, "config": config, "slippage_ticks": slippage, "run_id": run_id, "run_dir": str(run_dir), "record": record}


def read_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    return [json.loads(line) for line in path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]


def iso_dt(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def max_drawdown_stats(equity: list[dict], initial_equity: float) -> dict:
    if not equity:
        return {"max_drawdown": 0.0, "drawdown_pct": 0.0, "trough": None, "peak": None, "recovery_days": None}
    peak_value = initial_equity
    peak_point = None
    best_peak = None
    trough = None
    max_dd = -1.0
    peak_for_max = None
    for point in equity:
        value = float(point["Equity"])
        if value > peak_value:
            peak_value = value
            peak_point = point
        dd = peak_value - value
        if dd > max_dd:
            max_dd = dd
            trough = point
            peak_for_max = peak_point
    if peak_value < initial_equity:
        peak_value = initial_equity
    denominator = float(peak_for_max["Equity"]) if peak_for_max else initial_equity
    recovery_days = None
    if trough and peak_for_max:
        target = float(peak_for_max["Equity"])
        recovery = next((point for point in equity if iso_dt(point["BarEnd"]) > iso_dt(trough["BarEnd"]) and float(point["Equity"]) >= target), None)
        if recovery:
            recovery_days = (iso_dt(recovery["BarEnd"]) - iso_dt(peak_for_max["BarEnd"])).total_seconds() / 86400
    return {
        "max_drawdown": max(0.0, max_dd),
        "drawdown_pct": 0.0 if denominator == 0 else max(0.0, max_dd) / denominator * 100,
        "trough": trough,
        "peak": peak_for_max,
        "recovery_days": recovery_days,
    }


def summarize(item: dict, atr_map: dict[str, float], csv_path: Path) -> dict:
    run_dir = Path(item["run_dir"])
    record = item["record"]
    trades = read_jsonl(run_dir / "closed-trades.jsonl")
    fills = read_jsonl(run_dir / "fills.jsonl")
    equity = read_jsonl(run_dir / "equity.jsonl")
    positions = read_jsonl(run_dir / "positions.jsonl")
    intents = {row["Sequence"]: row for row in read_jsonl(run_dir / "intents.jsonl")}
    signals = read_jsonl(run_dir / "signals.jsonl")
    unfilled = read_jsonl(run_dir / "unfilled.jsonl")
    fill_by_seq = {row["FillSequence"]: row for row in fills}
    positive = [float(trade["NetPnl"]) for trade in trades if float(trade["NetPnl"]) > 0]
    negative = [float(trade["NetPnl"]) for trade in trades if float(trade["NetPnl"]) < 0]
    drawdown = max_drawdown_stats(equity, float(record["Config"]["InitialEquity"]))
    realized_peak = float(record["Config"]["InitialEquity"])
    realized_dd = 0.0
    for point in equity:
        realized = float(record["Config"]["InitialEquity"]) + float(point["RealizedGrossPnl"]) - float(point["Fees"])
        realized_peak = max(realized_peak, realized)
        realized_dd = max(realized_dd, realized_peak - realized)
    sizes = [int(row["Quantity"]) for row in fills if row["Action"] == "open"]
    risk_rows = []
    mfe_values = []
    for trade in trades:
        entry = fill_by_seq[trade["EntryFillSequence"]]
        exit_fill = fill_by_seq[trade["ExitFillSequence"]]
        entry_intent = intents.get(entry["IntentSequence"], {})
        atr_value = atr_map.get(entry_intent.get("SignalBarStart", ""))
        if atr_value is not None:
            entry_price = float(entry["Price"])
            stop = math.floor(entry_price - atr_value * float(item["config"]["atr_multiplier"]) + 1e-12)
            risk_points = max(entry_price - stop, 1.0)
            risk_amount = risk_points * MULTIPLIER * int(trade["Quantity"])
            reference = float(entry_intent["ReferencePrice"])
            planned_stop = math.floor(reference - atr_value * float(item["config"]["atr_multiplier"]) + 1e-12)
            planned_risk = max(reference - planned_stop, 1.0) * MULTIPLIER * int(trade["Quantity"])
            risk_rows.append({"risk": risk_amount, "planned_risk": planned_risk, "r": float(trade["NetPnl"]) / risk_amount, "net": float(trade["NetPnl"])})
            window = [p for p in positions if iso_dt(entry["BarEnd"]) <= iso_dt(p["BarEnd"]) <= iso_dt(exit_fill["BarEnd"])]
            if window:
                mfe = max(float(p["FloatingPnl"]) for p in window)
                mae = min(float(p["FloatingPnl"]) for p in window)
                mfe_values.append({"mfe": mfe, "mae": mae, "exit": float(trade["NetPnl"]), "retention": float(trade["NetPnl"]) / mfe if mfe > 0 else None})
    peak = drawdown["peak"] or {}
    trough = drawdown["trough"] or {}
    peak_realized = float(peak.get("RealizedGrossPnl", 0)) - float(peak.get("Fees", 0))
    trough_realized = float(trough.get("RealizedGrossPnl", 0)) - float(trough.get("Fees", 0))
    peak_floating = float(peak.get("FloatingPnl", 0))
    trough_floating = float(trough.get("FloatingPnl", 0))
    orders_failed = sum(1 for row in unfilled if row.get("reason") in {"insufficient_funds", "margin_exceeded"})
    risk_over = [row for row in trades if float(row["NetPnl"]) < -RISK_BUDGET]
    top_winners = sorted((float(row["NetPnl"]) for row in trades if float(row["NetPnl"]) > 0), reverse=True)[:3]
    net = float(record["NetPnl"])
    months = monthly_net(trades, fill_by_seq)
    loss_streak = max_loss_streak = 0
    loss_streak_amount = worst_loss_streak_amount = 0.0
    for trade in trades:
        if float(trade["NetPnl"]) < 0:
            loss_streak += 1
            loss_streak_amount += float(trade["NetPnl"])
            max_loss_streak = max(max_loss_streak, loss_streak)
            worst_loss_streak_amount = min(worst_loss_streak_amount, loss_streak_amount)
        else:
            loss_streak = 0
            loss_streak_amount = 0.0
    june_end = next((point for point in reversed(equity) if point["BarEnd"] < "2026-07-01"), None)
    return {
        "key": item["key"], "timeframe": item["config"]["timeframe"], "atr_period": item["config"]["atr_period"],
        "atr_multiplier": item["config"]["atr_multiplier"], "profile": item["config"]["profile"], "profile_label": PROFILES[item["config"]["profile"]],
        "slippage_ticks": item["slippage_ticks"], "run_id": item["run_id"], "run_dir": item["run_dir"],
        "formal_rows": int(record["FormalRows"]), "intent_count": int(record["FormalIntentCount"]), "fill_count": int(record["FillCount"]),
        "closed_trades": len(trades), "net_pnl": net, "gross_pnl": float(record["TotalGrossPnl"]), "fees": float(record["TotalFees"]),
        "final_equity": float(record["FinalEquity"]), "return_pct": float(record["ReturnRate"]), "max_drawdown": drawdown["max_drawdown"],
        "drawdown_pct": drawdown["drawdown_pct"], "realized_only_drawdown": realized_dd, "win_rate": float(record["WinRate"] or 0),
        "profit_factor": sum(positive) / abs(sum(negative)) if negative else None, "avg_win": statistics.mean(positive) if positive else None,
        "avg_loss": statistics.mean(negative) if negative else None, "max_loss": min(negative) if negative else 0.0,
        "avg_r": statistics.mean(row["r"] for row in risk_rows) if risk_rows else None,
        "avg_open_lots": statistics.mean(sizes) if sizes else None, "min_open_lots": min(sizes) if sizes else None, "max_open_lots": max(sizes) if sizes else None,
        "initial_price_risk_avg": statistics.mean(row["risk"] for row in risk_rows) if risk_rows else None,
        "planned_risk_avg": statistics.mean(row["planned_risk"] for row in risk_rows) if risk_rows else None,
        "planned_risk_max": max((row["planned_risk"] for row in risk_rows), default=None),
        "risk_over_1000_count": len(risk_over), "risk_over_1000_sum": sum(max(0.0, abs(float(row["NetPnl"])) - RISK_BUDGET) for row in risk_over),
        "margin_rejections": orders_failed,
        "risk_budget_skip_count": sum(1 for row in signals if row.get("Category") == "entry_skipped_risk_budget"),
        "mfe_avg": statistics.mean(row["mfe"] for row in mfe_values) if mfe_values else None,
        "mfe_retention_avg": statistics.mean(row["retention"] for row in mfe_values if row["retention"] is not None and row["exit"] > 0) if any(row["retention"] is not None and row["exit"] > 0 for row in mfe_values) else None,
        "giveback_to_loss_count": sum(1 for row in mfe_values if row["mfe"] > 0 and row["exit"] < 0),
        "giveback_to_loss_amount": sum(abs(row["exit"]) for row in mfe_values if row["mfe"] > 0 and row["exit"] < 0),
        "max_dd_peak_time": peak.get("BarEnd"), "max_dd_trough_time": trough.get("BarEnd"), "recovery_days": drawdown["recovery_days"],
        "dd_realized_change": peak_realized - trough_realized, "dd_floating_change": peak_floating - trough_floating,
        "equity": [[row["BarEnd"], float(row["Equity"])] for row in equity],
        "monthly_net": months, "top3_profit_share": sum(top_winners) / net * 100 if net > 0 else None,
        "top3_share_of_wins": sum(top_winners) / sum(positive) * 100 if positive else None,
        "top_winners_count": len(top_winners), "net_excluding_top3": net - sum(top_winners),
        "net_excluding_top1": net - max(positive, default=0.0), "negative_months": sum(value < 0 for value in months.values()),
        "max_loss_streak": max_loss_streak, "worst_loss_streak_amount": worst_loss_streak_amount,
        "feb_to_june_equity_pnl": float(june_end["Equity"]) - INITIAL if june_end else None,
        "july_to_sept_equity_pnl": float(equity[-1]["Equity"]) - float(june_end["Equity"]) if june_end else None,
        "end_open_pnl": float(equity[-1]["FloatingPnl"]) if equity else 0.0,
    }


def monthly_net(trades: list[dict], fills: dict[int, dict]) -> dict[str, float]:
    result: dict[str, float] = {}
    for trade in trades:
        exit_fill = fills[trade["ExitFillSequence"]]
        month = exit_fill["BarEnd"][:7]
        result[month] = result.get(month, 0.0) + float(trade["NetPnl"])
    return result


def finite(value, digits=2):
    if value is None or not math.isfinite(float(value)):
        return "—"
    return f"{float(value):,.{digits}f}"


def parameter_decision(rows: list[dict]) -> str:
    by_key = {row["key"]: row for row in rows}
    chosen = by_key["15m|14|2|two_r_n_minus_one|1"]
    winner = by_key["15m|14|1.5|current|1"]
    backup = by_key["30m|14|2|two_r_n_minus_one|1"]
    selected = [chosen, winner, by_key["30m|14|1.5|two_r_n_minus_one|1"], backup, by_key["60m|10|2|current|1"]]
    names = ["首选：15m / ATR14×2 / C", "收益最高：15m / ATR14×1.5 / A", "30m / ATR14×1.5 / C", "低频备选：30m / ATR14×2 / C", "60m / ATR10×2 / A"]
    def table(headers, values):
        return '<div class="scroll"><table><thead><tr>' + ''.join(f'<th>{escape(x)}</th>' for x in headers) + '</tr></thead><tbody>' + ''.join(
            '<tr>' + ''.join(f'<td>{escape(str(x))}</td>' for x in row) + '</tr>' for row in values) + '</tbody></table></div>'
    risk_table = table(
        ["候选", "净收益元", "最大回撤元", "收益/回撤", "最大单笔净亏元", "胜率", "平均手数", "平仓笔数"],
        [(name, finite(r["net_pnl"], 0), finite(r["max_drawdown"], 0), finite(r["net_pnl"] / r["max_drawdown"]),
          finite(abs(r["max_loss"]), 0), finite(r["win_rate"], 1) + "%", finite(r["avg_open_lots"]), r["closed_trades"]) for name, r in zip(names, selected)])
    concentration_table = table(
        ["候选", "前三盈利占净利", "前三盈利占全部盈利", "扣除前三盈利后的损益元", "亏损月份/8", "2–6月权益增量元", "7–9月权益增量元"],
        [(name, finite(r["top3_profit_share"], 1) + "%", finite(r["top3_share_of_wins"], 1) + "%", finite(r["net_excluding_top3"], 0),
          str(r["negative_months"]) + "/8", finite(r["feb_to_june_equity_pnl"], 0), finite(r["july_to_sept_equity_pnl"], 0)) for name, r in zip(names, selected)])
    interactions = []
    for multiplier in (1, 1.5, 2):
        group = [by_key[f"15m|14|{multiplier:g}|{profile}|1"] for profile in PROFILES]
        interactions.append([f"ATR14×{multiplier:g}"] + [f"{finite(r['net_pnl'], 0)} / {finite(r['max_drawdown'], 0)}" for r in group])
    interaction_table = table(["15分钟 / 净收益与回撤（元）", "A：1R保本+旧阶梯", "B：2R保本+旧阶梯", "C：2R保本+n−1R"], interactions)
    base = by_key["15m|14|2|two_r_n_minus_one|0"]
    return f'''<section id="parameter-decision"><h2>参数建议：先用 15 分钟 · ATR14×2 · C 新保护</h2>
<p class="callout"><b>我的选择：</b>EMA21/55 入场保持当前条件；ATR 周期 14、止损倍数 2；到 2R 加手续费偏移后保本，3R 起锁 n−1R；本金 100,000 元、每笔风险预算 1,000 元、按风险自动计算手数。选择依据是回撤、单笔尾部亏损、盈利分布与成本承受能力的综合表现。</p>
<p><b>下面的决策比较统一使用 1 tick 滑点结果。</b>已扣实际手续费，采用同一区间和成交方式。收益/回撤只是比较指标，不是年化指标；最大回撤包含持仓浮盈回吐。</p>
{risk_table}
<h3>为什么愿意少赚一些，选这组？</h3>
<p><b>收益与回撤的交换更划算。</b>相对收益最高的 15m×1.5+A，首选少赚 {finite(winner['net_pnl'] - chosen['net_pnl'], 0)} 元（{finite((1-chosen['net_pnl']/winner['net_pnl'])*100, 1)}%），最大回撤减少 {finite(winner['max_drawdown'] - chosen['max_drawdown'], 0)} 元（{finite((1-chosen['max_drawdown']/winner['max_drawdown'])*100, 1)}%）；收益/回撤由 {finite(winner['net_pnl']/winner['max_drawdown'])} 提高至 {finite(chosen['net_pnl']/chosen['max_drawdown'])}。已实现损益曲线的最大回撤也由 {finite(winner['realized_only_drawdown'], 0)} 降至 {finite(chosen['realized_only_drawdown'], 0)} 元，所以改善并不只来自浮盈口径。</p>
<p><b>更宽止损同时带来较小仓位。</b>平均手数 {finite(chosen['avg_open_lots'])}，范围 {chosen['min_open_lots']}–{chosen['max_open_lots']} 手；收益最高组平均 {finite(winner['avg_open_lots'])}，范围 {winner['min_open_lots']}–{winner['max_open_lots']} 手。在相同 1,000 元预算下，ATR×2 给价格更多波动空间，也降低了每一点跳空对应的账户损失；本样本最大净亏由 {finite(abs(winner['max_loss']), 0)} 降至 {finite(abs(chosen['max_loss']), 0)} 元。首选含成本预留的计划风险平均 {finite(chosen['planned_risk_avg'])} 元，预算利用率 {finite(chosen['planned_risk_avg']/RISK_BUDGET*100, 1)}%，没有明显闲置风险预算。所有 60 组均未出现不足一手跳过或保证金拒单，因此本轮没有发现“风险金太小错过开仓”的现象。</p>
<p><b>盈利更分散，对少数大行情的依赖较低。</b>首选最大的三笔盈利占全部盈利 {finite(chosen['top3_share_of_wins'], 1)}%，收益最高组为 {finite(winner['top3_share_of_wins'], 1)}%。仅在账面上扣除这三笔盈利，首选仍剩 {finite(chosen['net_excluding_top3'], 0)} 元，最高收益组剩 {finite(winner['net_excluding_top3'], 0)} 元；分阶段权益增加也更均衡。它仍依赖趋势盈利，但不像另外几组高度依赖一两次行情。</p>
{concentration_table}
<p class="note">“扣除前三盈利”仅用于衡量盈利集中度，并未删除交易后重跑策略，不能视为另一份回测。60 分钟只有两笔盈利，因此表中“前三”实际包含两笔。月份按平仓日期统计；分阶段增量按含浮盈的权益统计，两者不是同一种口径。7 月分界是事后诊断，未构成独立样本外验证。</p>
<p><b>滑点后的优势仍在。</b>首选从零滑点的 {finite(base['net_pnl'], 0)} 降到 {finite(chosen['net_pnl'], 0)} 元，变化 {finite(base['net_pnl']-chosen['net_pnl'], 0)} 元，约 {finite((base['net_pnl']-chosen['net_pnl'])/base['net_pnl']*100, 1)}%。收益最高组减少 1,670 元，约 9.1%。不同滑点会改变保护触发及退出路径，所以这不是单纯从原盈亏减一笔费用的估算。</p>
<h3>ATR 与保护规则需要一起看</h3>{interaction_table}
<p>在 ATR×2 下，C 相对 A 收益提高 3,498 元、回撤减少 3,289 元，较早锁住趋势利润有价值。但 ATR×1.5 下，C 的收益明显低于 A，回撤改善有限；ATR×1 下三种方案都表现较弱。<b>支持的是“×2 与 C 的组合”，不能据此认定 C 在所有止损距离上更好。</b>×1.5+C 仍盈利，但回撤达 6,324 元；×2 又是本轮网格的上边界，因此尚未证明这是宽阔、稳定的最优参数区。后续打磨可优先核查 ×1.75、×2、×2.25 的邻域，并用新增行情复核，而不是继续只挑最高收益点。</p>
<h3>30 分钟与 60 分钟怎么安排？</h3>
<p><b>30 分钟可作为低频备选，优先 ×2+C。</b>相比 ×1.5+C，它少赚 1,488 元，但最大单笔净亏从 2,115 降到 1,269 元，符合你“以损定量，仓位要合适”的侧重点。两组都只有 14 笔，盈利集中度比 15 分钟首选更高；×2+C 的最大回撤高点到期末还没恢复。它可以降低交易频率，但目前证据不足以取代 15 分钟作为主方案。</p>
<p><b>60 分钟先保留研究，不作为主选。</b>ATR14+C 三档均亏损；旧基准虽然盈利，但只有 7 笔、其中仅两笔盈利，7–9 月权益下降 1,505 元。回撤小不能独立证明策略更稳，它也可能只是交易次数少、行情暴露少。</p>
<h3>首选仍有两项明确代价</h3>
<p><b>1,000 元是计划风险预算，不是实际亏损上限。</b>首选有 {chosen['risk_over_1000_count']} 笔净亏超过预算，超出部分合计 {finite(chosen['risk_over_1000_sum'], 0)} 元；最大单笔净亏 {finite(abs(chosen['max_loss']), 0)} 元。当前软件收盘确认触及保护后，下一根有效 K 线开盘才成交；跳空、休市和这段延迟仍会使止损超预算。更宽 ATR 降低了本样本的单笔尾部亏损，但没有修复这一成交风险。</p>
<p><b>回撤减少不等于恢复很快。</b>首选最大回撤 {finite(chosen['max_drawdown'], 0)} 元，相当于本金 {finite(chosen['max_drawdown']/INITIAL*100)}%；该段由已实现损益下滑 {finite(chosen['dd_realized_change'], 0)} 元与持仓浮盈变化 {finite(chosen['dd_floating_change'], 0)} 元共同构成。从高点重新恢复用了 {finite(chosen['recovery_days'], 0)} 天，与最高收益组的 {finite(winner['recovery_days'], 0)} 天接近。25 笔交易及一个具体合约的单段历史，只够给出当前候选，不能证明长期表现。</p>
<p class="callout"><b>具体取舍：</b>先以 15m / ATR14×2 / C 为后续打磨的基准，保留 ×1.5+A 作收益对照，30m×2+C 作低频对照。推荐值已写入软件策略默认参数；本报告的参数比较来自原 60 组矩阵，另外用新默认值复现了首选一组，全部成交及权益点与原结果一致。</p>
<h3>每波行情，开仓几次？</h3>
<p><b>当前每个 EMA 多头阶段最多开仓 1 次。</b>参数为 <code>max_entries_per_trend=1</code>。开仓实际成交后才计一次；未成交限价单逐根撤销重挂不计入次数。止损或移动保护平仓后，只要 EMA21 仍在 EMA55 上方，就不会再次入场；EMA21≤EMA55 时计数归零，之后新的多头阶段才有下一次开仓机会。收盘价跌到 EMA55 下方仅使入场条件失效，不单独重置次数。</p>
<p>这是一段均线状态定义的“波”，不是肉眼判断的一整轮涨势；一轮大行情中反复穿越均线，可能形成多段可入场阶段。本次 25 笔实际开仓对应 25 个不同多头阶段，每阶段最多一次，已按开仓委托的信号 K 线核对。软件里的“每趋势最多入场次数”可调整；提高次数意味着平仓后可在同一阶段重开，当前参数没有持仓中加仓。</p>
<h3>软件 K 线复盘已就绪</h3>
<p>桌面入口直接打开白糖 <b>15 分钟、ATR14×2、C 新保护、风险预算 1,000 元、本金 100,000 元、手数倍率 1、滑点 1 tick</b> 的结果，正式区间为 2026-02-02 21:00 至 2026-10-01 00:00。复盘结果：净收益 13,791 元，最大回撤 4,202 元，25 笔已平仓、50 笔成交。软件 EMA 策略默认 ATR 已改为 14、保护默认已改为 C，其余入场逻辑保持一致。</p>
<ol><li>下方“成交流水”点击某笔开仓或平仓，K 线图会定位到对应成交；图上有“买开 / 卖平”标记，EMA21 为橙色、EMA55 为蓝色。</li><li>“已平仓交易”点击一行，会定位到该笔平仓附近；再从成交流水查看开仓位置。用滚轮缩放、拖动平移或“展开图表”复盘。</li><li>点“修改策略”会复制当前参数、区间和交易设置到可编辑配置。调整后开始回测会生成新结果；旧结果保留。打开历史结果时，手续费及滑点输入框也会同步，避免高级配置与表单不一致。</li></ol>
<p class="note">当前桌面复盘 run：<code>trd-20261008140800850-b1be9de2a</code>。此组采用新默认参数，50 笔成交、25 笔已平仓及逐根权益与矩阵首选完全一致。复盘图上的退出成交遵循软件原有的下一根有效 K 线开盘模型，不代表保护价即刻成交。</p>
<details class="note"><summary>软件验证范围</summary><p>编译 0 错误、0 警告；新默认参数与原矩阵首选逐笔、逐根一致。历史结果恢复、手续费/滑点表单同步、修改策略保留参数与区间、新结果复制、成交定位、权益曲线等相关自检通过。实际复盘窗口的启动日志确认：15 分钟 3,893 根，EMA21/55，50 笔成交，净收益 13,791 元，回撤 4,202 元。已有全量自检仍有“运行中关闭的进程探测”和“截图区域可见性”两项未通过，尚未宣称全量自检通过；自检 PNG 中能看到权益曲线与结果，但区域检测返回未通过。本次未扩展修改这些检查。</p></details>
<h3>首选与最高收益组的资金曲线（1 tick）</h3><div id="choice-legend" class="legend"></div><svg id="choice-chart" viewBox="0 0 1100 420" role="img" aria-label="推荐参数与最高收益参数的资金曲线比较"></svg><div id="choice-hover" class="small">移到图表可同时查看三组权益。</div><p class="note">按相同有效 K 线序号对齐，休市时段不留空白；权益含浮盈。紫色线为 ATR10×2+A 原基准。</p>
</section>'''


def make_report(rows: list[dict], sources: dict[str, Path], source_atrs: dict[tuple[str, int], dict[str, float]]) -> str:
    matrix = [row for row in rows if row["slippage_ticks"] == 0]
    stress = [row for row in rows if row["slippage_ticks"] == 1]
    profile_labels = json.dumps(PROFILES, ensure_ascii=False, separators=(",", ":"))
    curve_data = []
    for row in rows:
        if row["atr_period"] not in (10, 14):
            continue
        curve_data.append({"key": row["key"], "label": f"{row['profile']} / ATR{row['atr_period']}×{row['atr_multiplier']:g}", "timeframe": row["timeframe"],
                           "atr_multiplier": row["atr_multiplier"], "profile": row["profile"], "atr_period": row["atr_period"], "slippage_ticks": row["slippage_ticks"],
                           "equity": [point for point in row["equity"] if iso_dt(START) < iso_dt(point[0]) <= iso_dt(END)]})
    ranked = sorted(matrix, key=lambda row: row["net_pnl"], reverse=True)
    low_dd = sorted((row for row in matrix if row["closed_trades"] >= 7), key=lambda row: row["max_drawdown"])
    ratio = sorted((row for row in matrix if row["max_drawdown"] > 0), key=lambda row: row["net_pnl"] / row["max_drawdown"], reverse=True)
    top_rows = ranked[:5]
    low_rows = low_dd[:5]
    ratio_rows = ratio[:5]
    html_rows = []
    for row in sorted(rows, key=lambda item: (item["slippage_ticks"], item["timeframe"], item["atr_period"], item["atr_multiplier"], item["profile"])):
        html_rows.append("<tr>" + "".join(f"<td>{escape(str(value))}</td>" for value in (
            row["timeframe"], f"ATR{row['atr_period']}×{row['atr_multiplier']:g}", row["profile"], f"{row['slippage_ticks']} tick",
            f"{row['closed_trades']}", finite(row["win_rate"]), finite(row["net_pnl"]), finite(row["max_drawdown"]),
            finite(row["drawdown_pct"], 2) + "%", finite(row["profit_factor"], 2), finite(row["avg_r"], 2),
            finite(row["avg_open_lots"], 2), finite(row["max_loss"]), finite(row["fees"]), row["run_id"]
        )) + "</tr>")
    def recommendation_table(items):
        return "<table><thead><tr><th>周期</th><th>参数</th><th>方案</th><th>净收益</th><th>最大回撤</th><th>笔数</th><th>盈亏因子</th><th>收益/回撤</th><th>run ID</th></tr></thead><tbody>" + "".join(
            "<tr>" + "".join(f"<td>{escape(str(value))}</td>" for value in (
                row["timeframe"], f"ATR{row['atr_period']}×{row['atr_multiplier']:g}", row["profile"], finite(row["net_pnl"]), finite(row["max_drawdown"]),
                row["closed_trades"], finite(row["profit_factor"], 2), finite(row["net_pnl"] / row["max_drawdown"] if row["max_drawdown"] else None, 2), row["run_id"]
            )) + "</tr>" for row in items) + "</tbody></table>"
    stress_lookup = {row["key"].rsplit("|", 1)[0]: row for row in stress}
    sensitivities = []
    for row in matrix:
        other = stress_lookup.get(row["key"].rsplit("|", 1)[0])
        if other:
            sensitivities.append((row["net_pnl"] - other["net_pnl"], row))
    most_cost_sensitive = sorted(sensitivities, reverse=True, key=lambda pair: pair[0])[:1]
    worst_cost_sensitive = sorted(sensitivities, key=lambda pair: pair[0])[:1]
    month_data = json.dumps({row["key"]: row["monthly_net"] for row in rows}, ensure_ascii=False, separators=(",", ":"))
    matrix_json = json.dumps([{key: value for key, value in row.items() if key != "equity"} for row in rows], ensure_ascii=False, separators=(",", ":"))
    curves_json = json.dumps(curve_data, ensure_ascii=False, separators=(",", ":"))
    quality_rows = []
    for timeframe, snapshot_id in TIMEFRAMES.items():
        info = json.loads((DATA_ROOT / "snapshots" / snapshot_id / "quality.json").read_text(encoding="utf-8-sig"))
        quality_rows.append(f"<tr><td>{timeframe}</td><td>{snapshot_id}</td><td>{info.get('PublishedRows', 0):,}</td><td>{len(info.get('SuspectedGaps', []))}</td><td>{len(info.get('Issues', []))}</td></tr>")
    generated = datetime.now().astimezone().isoformat(timespec="seconds")
    return f'''<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>白糖 SR701 ATR14 移动保护矩阵回测分析</title>
<style>
#choice-chart{{width:100%;height:420px;display:block}}@media(max-width:700px){{#choice-chart{{height:310px}}}}
:root{{--bg:#f4f7fb;--card:#fff;--ink:#17253d;--muted:#64748b;--line:#d9e2ef;--blue:#2563eb;--green:#0f9d73;--red:#e45757}}*{{box-sizing:border-box}}body{{margin:0;background:var(--bg);color:var(--ink);font:15px/1.58 "Segoe UI","Microsoft YaHei",sans-serif}}header{{padding:30px max(24px,calc((100vw - 1320px)/2));background:linear-gradient(115deg,#102c53,#245f98);color:#fff}}header h1{{margin:0 0 6px;font-size:30px}}header p{{margin:4px 0;opacity:.9}}main{{max-width:1320px;margin:24px auto;padding:0 20px 50px}}section,.card{{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:20px;margin:0 0 18px;box-shadow:0 3px 14px #16345b0c}}h2{{font-size:21px;margin:0 0 12px}}h3{{font-size:16px;margin:18px 0 8px}}.grid{{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}}.kpi{{border:1px solid var(--line);padding:14px;border-radius:10px}}.kpi b{{display:block;color:var(--muted);font-size:13px;font-weight:500}}.kpi strong{{display:block;font-size:24px;margin-top:4px}}.controls{{display:flex;gap:14px;flex-wrap:wrap;align-items:center;margin:14px 0}}select{{padding:8px 10px;border:1px solid #b8c7d9;border-radius:8px;background:white;color:var(--ink)}}#chart{{width:100%;height:420px;display:block;background:#fff}}.legend{{display:flex;gap:16px;flex-wrap:wrap;font-size:13px}}.legend span::before{{content:"";display:inline-block;width:15px;height:3px;background:var(--c);vertical-align:middle;margin-right:6px}}table{{width:100%;border-collapse:collapse;font-size:13px}}th,td{{padding:8px 9px;border-bottom:1px solid var(--line);text-align:right;white-space:nowrap}}th{{position:sticky;top:0;background:#edf3fa;color:#334155}}td:nth-child(-n+3),th:nth-child(-n+3){{text-align:left}}.scroll{{overflow:auto;max-height:570px;border:1px solid var(--line);border-radius:8px}}.note{{color:var(--muted);font-size:13px}}.good{{color:var(--green)}}.bad{{color:var(--red)}}.callout{{border-left:4px solid var(--blue);padding:10px 14px;background:#eff6ff;border-radius:4px}}.pill{{background:#e9f2ff;color:#1d4ed8;border-radius:99px;padding:3px 9px;font-size:12px}}.small{{font-size:12px;color:var(--muted)}}@media(max-width:700px){{header h1{{font-size:23px}}#chart{{height:310px}}section{{padding:14px}}}}
</style></head><body>
<header><h1>白糖 SR701 · ATR14 风险止损与移动保护回测</h1><p>30 组主矩阵 + 30 组 1 tick 成本压力测试 · 生成于 {escape(generated)}</p><p>固定 EMA21/55 入场、10 万元本金、每笔 1,000 元风险预算；不构成实盘收益保证。</p></header>
<main>
{parameter_decision(rows)}
<section><h2>结论摘要</h2><div class="grid"><div class="kpi"><b>运行配置</b><strong>60 / 60</strong></div><div class="kpi"><b>共同正式区间</b><strong style="font-size:16px">2026-02-02 ～ 09-30</strong></div><div class="kpi"><b>主矩阵成本</b><strong>零滑点</strong></div><div class="kpi"><b>压力测试</b><strong>1 tick</strong></div></div>
<p>下方零滑点排序供检索；参数建议见报告开头，并采用 1 tick 结果综合比较。单项排名不能自动决定参数。</p>
<h3>净收益最高的 5 组</h3>{recommendation_table(top_rows)}<h3>回撤较低的 5 组（至少 7 笔平仓）</h3>{recommendation_table(low_rows)}<h3>净收益 / 最大回撤较高的 5 组</h3>{recommendation_table(ratio_rows)}
<p class="note">1 tick 压力测试净收益最大变化：{(escape(most_cost_sensitive[0][1]['timeframe'])+' / '+escape(most_cost_sensitive[0][1]['profile'])+'，下降 '+finite(most_cost_sensitive[0][0])+' 元') if most_cost_sensitive else '无'}；压力测试影响最小：{(escape(worst_cost_sensitive[0][1]['timeframe'])+' / '+escape(worst_cost_sensitive[0][1]['profile'])+'，净收益变化 '+finite(worst_cost_sensitive[0][0])+' 元') if worst_cost_sensitive else '无'}。</p>
</section>
<section><h2>资金曲线</h2><p class="note">选择周期和 ATR 止损倍数。曲线叠加 ATR14 的 A/B/C 三种保护，并显示 ATR10×2 现有基准。余额按每根已完成 K 线收盘价盯市；期末持仓另列浮动盈亏。</p>
<div class="controls"><label>周期 <select id="tf"><option>15m</option><option>30m</option><option>60m</option></select></label><label>ATR14 倍数 <select id="mult"><option value="1">1</option><option value="1.5">1.5</option><option value="2" selected>2</option></select></label><label>滑点 <select id="slip"><option value="0">0 tick</option><option value="1" selected>1 tick</option></select></label></div>
<div id="legend" class="legend"></div><svg id="chart" viewBox="0 0 1100 420" role="img" aria-label="账户资金曲线"></svg><div id="hover" class="small">将鼠标移到图表查看日期与权益。</div>
</section>
<section><h2>回测口径与开平仓条件</h2><p><b>数据区间：</b>2026-02-02 21:00 至 2026-10-01 00:00（北京时间，左闭右开），预热 55 根。15/30/60 分钟使用当前工作区固定快照；白糖 SR701 单一具体合约。</p>
<p><b>开仓：</b>EMA21 &gt; EMA55 且收盘站在 EMA55 上方，按 EMA55 挂限价多单，未成交则逐根更新挂单价；每个趋势最多入场一次，EMA21 回到 EMA55 下方重置次数。</p>
<p><b>仓位：</b>手数 = floor[风险预算 1,000 ÷（计划止损点数×10 元/点/手 + 每手 25 元成本预留）]。不足一手的信号记入诊断，不下单。25 元只作手数预算预留，不从账面盈亏重复扣除。</p>
<p><b>止损与保护：</b>ATR 使用 Wilder ATR；止损距离为信号 ATR×倍数，成交后按实际开仓价确定初始止损并冻结 R。到达 E+nR 即触发档位；移动保护价时才加双向手续费点数和1跳缓冲，按选定 A/B/C 规则只升不降。新开仓所在 K 线不管理该持仓；既有保护在完整 K 线确认触及时，下一根有效 K 线开盘平仓。期末不强制平仓。</p>
<p><b>账户设置：</b>初始权益 100,000 元，乘数 10，tick 1 点，保证金率 10%，开仓费 2 元/手、平今 3 元/手、平昨 1 元/手。滑点方案分别为 0 和 1 tick。</p>
<div class="scroll"><table><thead><tr><th>周期</th><th>快照</th><th>输入根数</th><th>疑似缺口</th><th>质量问题</th></tr></thead><tbody>{''.join(quality_rows)}</tbody></table></div>
<p class="callout"><b>时间边界修正：</b>回放将 K 线结束时间限制在已验证白糖交易日历的实际时段结束。30/60 分钟短尾 K 不再按名义周期越过休市；例如 14:30 尾 K 结束于 15:00。该时间仅修正事件时间和下一根委托的时间记录，K 线顺序、开盘价成交规则不变。</p></section>
<section><h2>全部 60 组结果</h2><p class="note">点击表头排序。收益/回撤比较应结合笔数、胜率、盈亏因子及 1 tick 结果阅读。</p><div class="scroll"><table id="results"><thead><tr>{''.join(f'<th data-col="{i}">{escape(label)}</th>' for i, label in enumerate(['周期','参数','方案','成本','平仓数','胜率%','净收益元','最大回撤元','回撤占峰值%','盈亏因子','平均R','均手数','最大单笔亏损','手续费','run ID']))}</tr></thead><tbody>{''.join(html_rows)}</tbody></table></div></section>
<section><h2>资金与退出诊断（当前周期 / 倍数 / 滑点的 C 新保护）</h2><p>指标卡和矩阵展示风险预算实际利用、超预算亏损、保证金拒单、最大回撤高点与低点、最大浮盈平均保留情况及前三笔盈利集中度。浮盈保留以已完成 K 线收盘盯市估算，不能复原 15 分钟 K 线内高低价的发生先后。</p><div id="diagnostics" class="grid"></div><h3>逐月已平仓净损益（当前资金曲线配置）</h3><div id="monthly" class="scroll"></div></section>
<footer class="small">数据快照质量、策略参数、运行 ID 和逐笔结果均留存在工作区。报告不对手续费、滑点、夜盘跳空或实际成交能力作保证。</footer>
</main>
<script>
const INITIAL=100000; const rows={matrix_json}; const curves={curves_json}; const profileLabels={profile_labels}; const monthlyData={month_data};
const colors={{current:'#2563eb',two_r_old_ladder:'#0f9d73',two_r_n_minus_one:'#e45757',baseline:'#7b61a8'}};
const tf=document.getElementById('tf'), mult=document.getElementById('mult'), slip=document.getElementById('slip');
function drawChoice(){{
 const choices=[['15m|14|2|two_r_n_minus_one|1','首选 ATR14×2+C','#e45757'],['15m|14|1.5|current|1','最高收益 ATR14×1.5+A','#2563eb'],['15m|10|2|current|1','原基准 ATR10×2+A','#7b61a8']];
 const series=choices.map(([key,label,color])=>({{row:rows.find(r=>r.key===key),points:curves.find(c=>c.key===key).equity,label,color}}));
 const svg=document.getElementById('choice-chart'), W=1100,H=420,L=78,R=18,T=20,B=40,values=series.flatMap(s=>s.points.map(p=>p[1]));
 let lo=Math.min(INITIAL,...values),hi=Math.max(INITIAL,...values),pad=(hi-lo)*.08||1;lo-=pad;hi+=pad;
 const x=i=>L+i*(W-L-R)/(series[0].points.length-1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo);let out=[];
 for(let n=0;n<5;n++){{let v=lo+(hi-lo)*n/4,yy=y(v),i=Math.round((series[0].points.length-1)*n/4);out.push(`<line x1="${{L}}" x2="${{W-R}}" y1="${{yy}}" y2="${{yy}}" stroke="#e7edf5"/><text x="${{L-9}}" y="${{yy+4}}" text-anchor="end" fill="#64748b" font-size="12">${{Math.round(v).toLocaleString()}}</text><text x="${{x(i)}}" y="${{H-10}}" text-anchor="middle" fill="#64748b" font-size="12">${{series[0].points[i][0].slice(0,10)}}</text>`)}}
 document.getElementById('choice-legend').innerHTML=series.map(s=>`<span style="--c:${{s.color}}">${{s.label}} · 净利 ${{fmt(s.row.net_pnl)}} / 回撤 ${{fmt(s.row.max_drawdown)}}</span>`).join('');
 svg.onmousemove=e=>{{let rect=svg.getBoundingClientRect(),i=Math.round(((e.clientX-rect.left)/rect.width*W-L)/(W-L-R)*(series[0].points.length-1));i=Math.max(0,Math.min(i,series[0].points.length-1));document.getElementById('choice-hover').textContent=series[0].points[i][0]+' · '+series.map(s=>s.label+'：'+fmt(s.points[i][1])+' 元').join(' / ')}};
}}
function draw(){{const timeframe=tf.value, m=Number(mult.value), s=Number(slip.value);let picked=rows.filter(r=>r.timeframe===timeframe&&r.slippage_ticks===s&&((r.atr_period===14&&r.atr_multiplier===m)||r.atr_period===10));let svg=document.getElementById('chart'),W=1100,H=420,L=78,R=18,T=20,B=40;let series=picked.map(r=>{{let c=curves.find(x=>x.key===r.key);return{{r,c,points:c?.equity||[]}}}}).filter(x=>x.points.length);if(!series.length){{svg.innerHTML='<text x="50" y="80">没有曲线数据</text>';return}}let all=series.flatMap(x=>x.points.map(p=>p[1])),lo=Math.min(...all,INITIAL),hi=Math.max(...all,INITIAL),pad=(hi-lo)*.08||1;lo-=pad;hi+=pad;let x=i=>L+i*(W-L-R)/Math.max(series[0].points.length-1,1),y=v=>T+(hi-v)*(H-T-B)/(hi-lo);let out=[];for(let n=0;n<5;n++){{let val=lo+(hi-lo)*n/4,yy=y(val);out.push(`<line x1="${{L}}" x2="${{W-R}}" y1="${{yy}}" y2="${{yy}}" stroke="#e7edf5"/><text x="${{L-9}}" y="${{yy+4}}" text-anchor="end" fill="#64748b" font-size="12">${{Math.round(val).toLocaleString()}}</text>`);let idx=Math.round((series[0].points.length-1)*n/4),label=new Date(series[0].points[idx][0]).toLocaleDateString('zh-CN');out.push(`<text x="${{x(idx)}}" y="${{H-10}}" text-anchor="middle" fill="#64748b" font-size="12">${{label}}</text>`)}}series.forEach(q=>{{let color=q.r.atr_period===10?colors.baseline:colors[q.r.profile];let path=q.points.map((p,i)=>(i?'L':'M')+x(i).toFixed(2)+','+y(p[1]).toFixed(2)).join(' ');out.push(`<path d="${{path}}" fill="none" stroke="${{color}}" stroke-width="2"/>`)}});svg.innerHTML=out.join('');document.getElementById('legend').innerHTML=series.map(q=>`<span style="--c:${{q.r.atr_period===10?colors.baseline:colors[q.r.profile]}}">${{q.r.atr_period===10?'ATR10×2现有基准':profileLabels[q.r.profile]+' · ATR14×'+m}} | 净利 ${{fmt(q.r.net_pnl)}}，回撤 ${{fmt(q.r.max_drawdown)}}</span>`).join('');svg.onmousemove=e=>{{let rect=svg.getBoundingClientRect(),relative=(e.clientX-rect.left)/rect.width*W,idx=Math.round((relative-L)/(W-L-R)*(series[0].points.length-1));idx=Math.max(0,Math.min(idx,series[0].points.length-1));let p=series[0].points[idx];document.getElementById('hover').textContent=p[0]+' · '+new Date(p[0]).toLocaleString('zh-CN')+' · 权益 '+fmt(p[1])+' 元'}};const c=series.find(q=>q.r.atr_period===14&&q.r.profile==='two_r_n_minus_one')?.r||series[0].r;document.getElementById('diagnostics').innerHTML=[['计划风险均值（含成本预留）',fmt(c.planned_risk_avg)+' 元'],['初始价格风险均值',fmt(c.initial_price_risk_avg)+' 元'],['实际手数',fmt(c.min_open_lots,0)+'–'+fmt(c.max_open_lots,0)+' 手'],['超过 1,000 元净亏损',fmt(c.risk_over_1000_count,0)+' 笔 / 超出 '+fmt(c.risk_over_1000_sum)+' 元'],['保证金拒单',fmt(c.margin_rejections,0)+' 笔'],['回撤高点',c.max_dd_peak_time||'—'],['回撤低点',c.max_dd_trough_time||'—'],['高点恢复',c.recovery_days==null?'期末未恢复':fmt(c.recovery_days)+' 天'],['回撤中已实现下滑',fmt(c.dd_realized_change)+' 元'],['回撤中浮盈变化',fmt(c.dd_floating_change)+' 元'],['已实现曲线回撤',fmt(c.realized_only_drawdown)+' 元'],['盈利交易平均浮盈保留',fmt(c.mfe_retention_avg==null?null:c.mfe_retention_avg*100)+'%'],['曾有浮盈但最终亏损',fmt(c.giveback_to_loss_count,0)+' 笔 / '+fmt(c.giveback_to_loss_amount)+' 元'],['前 3 笔盈利占净利',fmt(c.top3_profit_share)+'%'],['期末未平仓浮盈',fmt(c.end_open_pnl)+' 元']].map(x=>`<div class="kpi"><b>${{x[0]}}</b><strong style="font-size:17px">${{x[1]}}</strong></div>`).join('');let mm=c.key;let mon=monthlyData[mm]||{{}};document.getElementById('monthly').innerHTML='<table><thead><tr><th>月份</th><th>已平仓净损益</th></tr></thead><tbody>'+Object.entries(mon).sort().map(([k,v])=>`<tr><td>${{k}}</td><td>${{fmt(v)}} 元</td></tr>`).join('')+'</tbody></table>'}}
function fmt(v,d=2){{return v==null||!Number.isFinite(Number(v))?'—':Number(v).toLocaleString('zh-CN',{{minimumFractionDigits:d,maximumFractionDigits:d}})}}
[tf,mult,slip].forEach(x=>x.addEventListener('change',draw));draw();drawChoice();
document.querySelectorAll('#results th').forEach(th=>th.addEventListener('click',()=>{{const i=Number(th.dataset.col),tb=th.closest('table').tBodies[0],rs=[...tb.rows],asc=th.dataset.asc!=='true';rs.sort((a,b)=>{{let x=a.cells[i].textContent.replace(/[,%]/g,''),y=b.cells[i].textContent.replace(/[,%]/g,'');let nx=Number(x.replace(/,/g,'')),ny=Number(y.replace(/,/g,''));return(Number.isFinite(nx)&&Number.isFinite(ny)?nx-ny:x.localeCompare(y,'zh-CN'))*(asc?1:-1)}});rs.forEach(r=>tb.appendChild(r));th.dataset.asc=String(asc)}}));
</script></body></html>'''


def main() -> int:
    if not CLI.exists():
        raise FileNotFoundError(f"请先构建 CLI：{CLI}")
    OUTPUT.mkdir(parents=True, exist_ok=True)
    sources = load_sources()
    atr_maps = {(timeframe, period): load_atr_map(path, period) for timeframe, path in sources.items() for period in (10, 14)}
    progress_path = OUTPUT / "progress.json"
    progress = json.loads(progress_path.read_text(encoding="utf-8")) if progress_path.exists() else {"completed": []}
    known = {item["key"]: item for item in progress["completed"]}
    plan = list(configs())
    work = [(config, slip) for slip in (0, 1) for config in plan]
    for index, (config, slip) in enumerate(work, start=1):
        key = config_key(config, slip)
        if key in known:
            continue
        print(f"[{index}/60] {key}", flush=True)
        item = run_one(config, slip)
        known[key] = item
        progress["completed"] = list(known.values())
        progress["updated_at"] = datetime.now().astimezone().isoformat(timespec="seconds")
        progress_path.write_text(json.dumps(progress, ensure_ascii=False, indent=2), encoding="utf-8")
        print(f"  净利={item['record']['NetPnl']} 最大回撤={item['record']['MaxDrawdown']} 已平={item['record']['ClosedTradeCount']} run={item['run_id']}", flush=True)
    summaries = [summarize(item, atr_maps[(item["config"]["timeframe"], item["config"]["atr_period"])], sources[item["config"]["timeframe"]]) for _, item in sorted(known.items())]
    (OUTPUT / "matrix-summary.json").write_text(json.dumps(summaries, ensure_ascii=False, indent=2), encoding="utf-8")
    html = make_report(summaries, sources, atr_maps)
    html_path = OUTPUT / "atr14-matrix-report.html"
    html_path.write_text(html, encoding="utf-8")
    print(f"报告={html_path}", flush=True)
    print(f"完成：{len(summaries)} 组；正式区间 {START} 至 {END}；15/30/60 分钟；零滑点和 1 tick。", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
