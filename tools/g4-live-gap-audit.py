import datetime as dt
import importlib.util
import json
import types

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_gap", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
events = []
bridge.reply = events.append
bridge.time.sleep = lambda _: None
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


class GapApi:
    def __init__(self, **kwargs):
        self.number = len(sessions)
        if self.number >= 2:
            raise PermissionError("fixture credential ended")
        self.calls = 0
        self.frame = Frame([row(0), row(1)]) if self.number == 0 else Frame([row(0, float("nan")), row(0, float("nan")), row(0, float("nan"))])
        sessions.append(self)

    def get_kline_serial(self, *args, **kwargs):
        return self.frame

    def wait_update(self, **kwargs):
        self.calls += 1
        if self.number == 0:
            if self.calls == 1:
                return True
            raise RuntimeError("fixture network disconnect")
        self.frame.rows = [row(10), row(11), row(12)]
        return True

    def is_changing(self, *args):
        return True

    def close(self):
        return None


bridge.import_sdk = lambda: (types.SimpleNamespace(__version__="3.8.7-surrogate"), GapApi, lambda *args: None)
bridge.importlib.util.find_spec = lambda _: object()
bridge.live({"account": "surrogate", "password": "not-written", "symbols": ["TEST.FAKE"],
             "duration_seconds": 60, "last_complete_bar_end": "2026-10-09T09:01:00+08:00"})
names = [item.get("event") for item in events]
print(json.dumps({
    "scope": "current bridge limited recovery SDK-format surrogate; no account login or notification",
    "events": events,
    "paused_after_unavailable_gap": "paused" in names,
    "no_recovered_event": "recovered" not in names,
    "no_recovery_bar_consumed": not any(item.get("bar", {}).get("is_recovery") for item in events),
    "terminal_failure_after_rebuild": any(item.get("error_kind") == "Permission" for item in events),
}, ensure_ascii=False, indent=2))
