import datetime as dt
import importlib.util
import json

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_calendar", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

WINDOWS = ["09:00-10:15", "10:30-11:30", "13:30-15:00", "21:00-23:00", "23:00-02:30"]
DAY_RANGES = [
    {"trading_day": "2026-09-29", "start": "2026-09-29T09:00:00+08:00", "end": "2026-09-29T10:15:00+08:00", "symbol_scope": "TEST.FAKE"},
    {"trading_day": "2026-09-29", "start": "2026-09-29T10:30:00+08:00", "end": "2026-09-29T11:30:00+08:00", "symbol_scope": "TEST.FAKE"},
]


def bar(timestamp, duration):
    return bridge.bar_from_values("TEST.FAKE", duration, timestamp, {"open": 100, "high": 102, "low": 99, "close": 101, "volume": 10, "close_oi": 6}, 0, True)


lunch_start = bridge.parse_datetime("2026-09-29T10:15:00+08:00")
lunch_end = bridge.parse_datetime("2026-09-29T10:32:00+08:00")
lunch = bridge.coverage_payload([bar("2026-09-29T10:30:00+08:00", 60), bar("2026-09-29T10:31:00+08:00", 60)], lunch_start, lunch_end, False, "structured fixture session metadata", 60, provider_trading_days=["2026-09-29"], provider_session_windows=WINDOWS, provider_calendar_source="fixture", provider_session_ranges=DAY_RANGES, symbol="TEST.FAKE")
daily_start = bridge.parse_datetime("2026-09-25T09:00:00+08:00")
daily_end = bridge.parse_datetime("2026-09-29T09:00:00+08:00")
daily = bridge.coverage_payload([bar("2026-09-25T09:00:00+08:00", 86400), bar("2026-09-28T09:00:00+08:00", 86400)], daily_start, daily_end, False, "plain note must not act as calendar evidence", 86400, provider_trading_days=["2026-09-25", "2026-09-28"])
keyword_only = bridge.coverage_payload([bar("2026-09-28T09:00:00+08:00", 86400)], daily_start, daily_end, False, "fixture calendar unavailable", 86400)
gap = bridge.coverage_payload([bar("2026-09-29T09:00:00+08:00", 60), bar("2026-09-29T09:02:00+08:00", 60)], bridge.parse_datetime("2026-09-29T09:00:00+08:00"), bridge.parse_datetime("2026-09-29T09:04:00+08:00"), False, "structured fixture session metadata", 60, provider_trading_days=["2026-09-29"], provider_session_windows=WINDOWS, provider_calendar_source="fixture", provider_session_ranges=DAY_RANGES, symbol="TEST.FAKE")

tail_start = bridge.parse_datetime("2026-09-29T09:00:00+08:00")
tail_rows = [bar(tail_start + dt.timedelta(minutes=index), 60) for index in range(75)]
tail = bridge.coverage_payload(tail_rows, tail_start, bridge.parse_datetime("2026-09-29T10:20:00+08:00"), False, "effective last valid minute", 60, provider_trading_days=["2026-09-29"], provider_session_windows=WINDOWS, provider_calendar_source="fixture", provider_session_ranges=DAY_RANGES, symbol="TEST.FAKE")
night_start = bridge.parse_datetime("2026-09-28T21:00:00+08:00")
night_ranges = [{"trading_day": "2026-09-29", "start": "2026-09-28T21:00:00+08:00", "end": "2026-09-28T23:00:00+08:00", "symbol_scope": "TEST.FAKE"}]
night = bridge.coverage_payload([bar("2026-09-28T21:00:00+08:00", 60), bar("2026-09-28T21:01:00+08:00", 60)], night_start, night_start + dt.timedelta(minutes=2), False, "explicit trading-day session range", 60, provider_trading_days=["2026-09-29"], provider_session_windows=WINDOWS, provider_calendar_source="fixture", provider_session_ranges=night_ranges, symbol="TEST.FAKE")

long_start = bridge.parse_datetime("2026-09-01T09:00:00+08:00")
long_end = bridge.parse_datetime("2026-09-11T09:00:00+08:00")
early = [bar(long_start + dt.timedelta(minutes=index), 60) for index in range(3 * 24 * 60) if bridge.is_tradable_timestamp(long_start + dt.timedelta(minutes=index))]
early.append(bar("2026-09-10T09:00:00+08:00", 60))
long_range = bridge.coverage_payload(early, long_start, long_end, False, "ten-day sparse range", 60, provider_trading_days=[(long_start.date() + dt.timedelta(days=index)).isoformat() for index in range(10)], provider_session_windows=WINDOWS, provider_calendar_source="fixture")

report = {
    "scope": "bridge structured calendar metadata audit; no account login or market data",
    "lunch_effective_boundary_passes": lunch["complete"],
    "weekday_daily_provider_calendar_passes": daily["complete"],
    "keyword_only_daily_evidence_blocks": not keyword_only["complete"],
    "real_minute_gap_blocks": not gap["complete"],
    "effective_last_valid_minute_passes": tail["complete"],
    "cross_midnight_trading_day_passes": night["complete"],
    "ten_day_sparse_range_blocks": not long_range["complete"],
    "lunch": lunch,
    "daily": daily,
    "keyword_only": keyword_only,
    "gap": gap,
    "tail": {"rows": tail["rows"], "complete": tail["complete"]},
    "night": night,
    "long_range": {"rows": long_range["rows"], "complete": long_range["complete"], "note": long_range["coverage_note"]},
}
print(json.dumps(report, ensure_ascii=False, indent=2))
