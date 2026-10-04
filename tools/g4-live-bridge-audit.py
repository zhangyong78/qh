import datetime as dt
import importlib.util
import json
import types

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_current", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

events = []
bridge.reply = events.append
bridge.time.sleep = lambda _: None
symbol = "TEST.FAKE"
beijing = dt.timezone(dt.timedelta(hours=8))


def row(minute, value=100):
    timestamp = dt.datetime(2026, 10, 9, 9, minute, tzinfo=beijing)
    return {"datetime": int(timestamp.timestamp() * 1_000_000_000), "open": value, "high": value + 2,
            "low": value - 1, "close": value + 1, "volume": 10, "close_oi": 6,
            "trading_day": "2026-10-09"}


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


sessions = []


class LiveApi:
    def __init__(self, **kwargs):
        self.number = len(sessions)
        self.calls = 0
        self.frame = Frame([row(1), row(2), row(3)]) if self.number == 0 else Frame([row(0, float("nan")), row(0, float("nan")), row(0, float("nan"))])
        sessions.append(self)

    def get_kline_serial(self, *args, **kwargs):
        return self.frame

    def wait_update(self, **kwargs):
        self.calls += 1
        if self.number == 0:
            if self.calls == 1:
                self.frame.rows = [row(3), row(4), row(5)]
                return True
            raise RuntimeError("fixture network disconnect")
        if self.calls == 1:
            self.frame.rows = [row(1), row(2), row(3)]
            return True
        if self.calls == 2:
            self.frame.rows = [row(5), row(6), row(7)]
            return True
        raise PermissionError("fixture stream ended")

    def is_changing(self, *args):
        return True

    def close(self):
        return None


module = types.SimpleNamespace(__version__="3.8.7-surrogate")
bridge.import_sdk = lambda: (module, LiveApi, lambda *args: None)
bridge.importlib.util.find_spec = lambda _: object()
bridge.live({"account": "surrogate", "password": "not-written", "symbols": [symbol],
             "duration_seconds": 60, "last_complete_bar_end": "2026-10-09T09:01:00+08:00"})

names = [item.get("event") for item in events]
bars = [item["bar"] for item in events if item.get("event") == "bar"]
recovery = [item for item in bars if item.get("is_recovery")]
normal = [item for item in bars if not item.get("is_recovery")]
report = {
    "scope": "current bridge live SDK-format surrogate; no account login or notification",
    "events": events,
    "recovery_ready_before_recovered": "recovery_ready" in names and names.index("recovery_ready") < names.index("recovered"),
    "recovery_bars": [item["timestamp"] for item in recovery],
    "recovery_trading_days": sorted({item["trading_day"] for item in recovery}),
    "normal_new_bars": [item["timestamp"] for item in normal],
    "normal_new_bars_after_recovery": [item["timestamp"] for item in normal] == ["2026-10-09T09:03:00+08:00", "2026-10-09T09:04:00+08:00"],
    "permission_terminal": any(item.get("error_kind") == "Permission" for item in events),
}
print(json.dumps(report, ensure_ascii=False, indent=2))
