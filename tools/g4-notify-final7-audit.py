import datetime as dt
import importlib.util
import json
import types

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_final7_notify", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

tz = dt.timezone(dt.timedelta(hours=8))
symbol = "TEST.FAKE"


def row(index, complete=None):
    timestamp = dt.datetime(2026, 10, 9, 9, index, tzinfo=tz)
    value = {"datetime": int(timestamp.timestamp() * 1_000_000_000), "open": 100, "high": 102, "low": 99, "close": 101, "volume": 10, "close_oi": 6, "trading_day": "2026-10-09"}
    if complete is not None:
        value["is_complete"] = complete
    return value


class Frame:
    def __init__(self, rows):
        self.rows = rows
        self.iloc = self

    @property
    def index(self):
        return list(range(len(self.rows)))

    def __getitem__(self, key):
        return Frame(self.rows[key]) if isinstance(key, slice) else self.rows[key]

    def iterrows(self):
        return enumerate(self.rows)


def run(mode):
    events = []
    sessions = []

    class Api:
        def __init__(self, **kwargs):
            if sessions:
                raise PermissionError("bounded fixture ends stream")
            self.calls = 0
            self._data = {"notify": {}}
            self.frame = Frame([row(0), row(1, False)] if mode == "unconfirmed_tail" else [row(0), row(1)])
            sessions.append(self)

        def get_kline_serial(self, *args, **kwargs):
            return self.frame

        def notify(self, key, code, conn_id="md"):
            self._data["notify"][key] = {"code": code, "conn_id": conn_id, "url": "offline://fixture"}

        def wait_update(self, **kwargs):
            self.calls += 1
            if mode == "known_calendar_last_bar" and self.calls == 1:
                self.frame.rows = [row(0), row(1), row(2)]
                return True
            if mode == "unconfirmed_tail" and self.calls == 1:
                self.frame.rows = [row(0), row(1, False), row(2, False)]
                return True
            if mode == "unknown_new_bar" and self.calls == 1:
                self.frame.rows = [row(0), row(1), row(2), row(3)]
                return True
            if mode == "trade_disconnect" and self.calls == 1:
                self.notify("trade-down", 2019112911, "trade")
                self.frame.rows = [row(0), row(1), row(2)]
                return True
            if mode == "same_batch_up_then_down" and self.calls == 1:
                self.notify("up", 2019112902, "md")
                self.notify("down", 2019112911, "md")
                self.frame.rows = [row(0), row(1), row(2)]
                return True
            if mode in ("md_down_up", "zero_gap") and self.calls == 1:
                self.notify("down", 2019112911, "md")
                return True
            if mode in ("md_down_up", "zero_gap") and self.calls == 2:
                self.notify("up", 2019112902, "md")
                self.frame.rows = [row(0), row(1), row(2), row(3)] if mode == "md_down_up" else [row(0), row(1), row(2)]
                return True
            if mode == "md_down_up" and self.calls == 3:
                self.frame.rows = [row(0), row(1), row(2), row(3), row(4)]
                return True
            if mode == "zero_gap" and self.calls == 3:
                self.frame.rows = [row(0), row(1), row(2), row(3)]
                return True
            raise PermissionError("bounded fixture ends stream")

        def is_changing(self, *args):
            return True

        def close(self):
            return None

    bridge.reply = events.append
    bridge.time.sleep = lambda _: None
    bridge.import_sdk = lambda: (types.SimpleNamespace(__version__="3.8.7-surrogate"), Api, lambda *args: None)
    bridge.importlib.util.find_spec = lambda _: object()
    bridge.live({
        "account": "fixture",
        "password": "fixture",
        "symbols": [symbol],
        "duration_seconds": 60,
        "provider_trading_days": ["2026-10-09"],
        "provider_session_ranges": [{
            "trading_day": "2026-10-09",
            "start": "2026-10-09T09:00:00+08:00",
            "end": "2026-10-09T09:02:00+08:00" if mode in ("known_calendar_last_bar", "unknown_new_bar") else "2026-10-09T11:30:00+08:00",
            "symbol_scope": symbol,
        }],
    })
    names = [item.get("event") for item in events]
    bars = [item["bar"] for item in events if item.get("event") == "bar"]
    return {"paused": names.count("paused"), "recovered": names.count("recovered"), "bars": [{"timestamp": item["timestamp"], "is_recovery": item["is_recovery"]} for item in bars], "events": events}


reports = {mode: run(mode) for mode in ("md_down_up", "trade_disconnect", "same_batch_up_then_down", "zero_gap", "known_calendar_last_bar", "unconfirmed_tail", "unknown_new_bar")}
weekend_expected = bridge.expected_live_timestamps(
    60,
    ["2026-10-09", "2026-10-12"],
    [
        {"trading_day": "2026-10-09", "start": "2026-10-09T09:00:00+08:00", "end": "2026-10-09T15:00:00+08:00", "symbol_scope": symbol},
        {"trading_day": "2026-10-12", "start": "2026-10-12T09:00:00+08:00", "end": "2026-10-12T11:30:00+08:00", "symbol_scope": symbol},
    ],
    symbol,
)
friday_boundary = dt.datetime(2026, 10, 9, 15, 0, tzinfo=tz)
weekend_next = next((candidate for candidate in weekend_expected or [] if candidate >= friday_boundary), None)
result = {
    "scope": "bounded TqSdk 3.8.7 md/trade notify surrogate; no login",
    "md_down_up": {"paused_once": reports["md_down_up"]["paused"] == 1, "recovered_once": reports["md_down_up"]["recovered"] == 1, "recovery_bars": [bar for bar in reports["md_down_up"]["bars"] if bar["is_recovery"]], "normal_after_recovery": [bar for bar in reports["md_down_up"]["bars"] if not bar["is_recovery"]][-1:]},
    "trade_code_ignored": reports["trade_disconnect"]["paused"] == 0 and any(not bar["is_recovery"] for bar in reports["trade_disconnect"]["bars"]),
    "same_batch_up_then_down_conservative": reports["same_batch_up_then_down"]["recovered"] == 0 and reports["same_batch_up_then_down"]["paused"] == 1,
    "zero_gap_recovery_then_new_bar_normal": reports["zero_gap"]["recovered"] == 1 and reports["zero_gap"]["bars"][-1]["is_recovery"] is False,
    "known_calendar_last_bar_consumed": reports["known_calendar_last_bar"]["paused"] == 0 and [bar["timestamp"] for bar in reports["known_calendar_last_bar"]["bars"]][-1:] == ["2026-10-09T09:01:00+08:00"],
    "unconfirmed_tail_waits": reports["unconfirmed_tail"]["paused"] == 0 and [bar["timestamp"] for bar in reports["unconfirmed_tail"]["bars"]] == ["2026-10-09T09:00:00+08:00"],
    "unknown_new_bar_pauses": reports["unknown_new_bar"]["paused"] == 1,
    "weekend_explicit_calendar_boundary": weekend_next == dt.datetime(2026, 10, 12, 9, 0, tzinfo=tz),
    "unknown_calendar_restricted": bridge.expected_live_timestamps(60, None, None, symbol) is None,
    "raw": reports,
}
result["all_passed"] = all(value for key, value in result.items() if key != "raw" and isinstance(value, bool))
print(json.dumps(result, ensure_ascii=False, indent=2))
raise SystemExit(0 if result["all_passed"] else 1)
