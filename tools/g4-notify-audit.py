import datetime as dt
import importlib.util
import json
import types

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_notify", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)

symbol = "TEST.FAKE"
tz = dt.timezone(dt.timedelta(hours=8))


def row(minute, value=100):
    timestamp = dt.datetime(2026, 10, 9, 9, minute, tzinfo=tz)
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


events = []
sessions = []


class NotifyApi:
    def __init__(self, **kwargs):
        self.calls = 0
        self._data = {"notify": {}}
        self.frame = Frame([row(0), row(1)])
        sessions.append(self)
        self.number = len(sessions) - 1

    def get_kline_serial(self, *args, **kwargs):
        return self.frame

    def wait_update(self, **kwargs):
        self.calls += 1
        if self.number > 0:
            raise PermissionError("surrogate ends stream")
        if self.calls == 1:
            self._data["notify"].update({
                "down-1": {"type": "MESSAGE", "code": 2019112911, "conn_id": "market", "url": "offline://fixture"},
                "down-2": {"type": "MESSAGE", "code": 2019112911, "conn_id": "market", "url": "offline://fixture"},
                "unrelated": {"type": "MESSAGE", "code": 900001, "conn_id": "trade", "url": "offline://fixture"},
            })
            return True
        if self.calls == 2:
            self._data["notify"].update({
                "up-1": {"type": "MESSAGE", "code": 2019112902, "conn_id": "market", "url": "offline://fixture"},
                "up-2": {"type": "MESSAGE", "code": 2019112902, "conn_id": "market", "url": "offline://fixture"},
            })
            self.frame.rows = [row(1), row(2), row(3)]
            return True
        if self.calls == 3:
            self.frame.rows = [row(2), row(3), row(4)]
            return True
        raise PermissionError("surrogate ends stream")

    def is_changing(self, *args):
        return True

    def close(self):
        return None


module = types.SimpleNamespace(__version__="3.8.7-surrogate")
bridge.reply = events.append
bridge.time.sleep = lambda _: None
bridge.import_sdk = lambda: (module, NotifyApi, lambda *args: None)
bridge.importlib.util.find_spec = lambda _: object()
bridge.live({"account": "surrogate", "password": "not-written", "symbols": [symbol], "duration_seconds": 60})

bars = [item["bar"] for item in events if item.get("event") == "bar"]
names = [item.get("event") for item in events]
recovered_index = names.index("recovered")
notify_report = {
    "paused_once_for_duplicate_disconnect": names.count("paused") == 1,
    "recovery_once_for_duplicate_recovery": names.count("recovered") == 1,
    "recovery_bars": [item["timestamp"] for item in bars if item.get("is_recovery")],
    "normal_bars_after_recovery": [item["bar"]["timestamp"] for index, item in enumerate(events) if index > recovered_index and item.get("event") == "bar" and not item["bar"].get("is_recovery")],
    "permission_terminal": any(item.get("error_kind") == "Permission" for item in events),
    "events": events,
}


gap_events = []
gap_sessions = []


class GapApi(NotifyApi):
    def __init__(self, **kwargs):
        super().__init__(**kwargs)
        self.number = len(gap_sessions)
        gap_sessions.append(self)
        self.frame = Frame([row(0), row(1)]) if self.number == 0 else Frame([row(1, float("nan")), row(3, float("nan"))])

    def wait_update(self, **kwargs):
        self.calls += 1
        if self.number == 0 and self.calls == 1:
            self._data["notify"]["down"] = {"code": 2019112911, "conn_id": "market", "url": "offline://fixture"}
            return True
        if self.number == 0 and self.calls == 2:
            self._data["notify"]["up"] = {"code": 2019112902, "conn_id": "market", "url": "offline://fixture"}
            self.frame.rows = [row(1), row(3), row(4)]
            return True
        raise PermissionError("gap surrogate ends stream")


bridge.reply = gap_events.append
bridge.import_sdk = lambda: (module, GapApi, lambda *args: None)
bridge.live({"account": "surrogate", "password": "not-written", "symbols": [symbol], "duration_seconds": 60})
gap_names = [item.get("event") for item in gap_events]
gap_report = {
    "limited_gap_pause_without_recovered": "recovered" not in gap_names,
    "permission_terminal": any(item.get("error_kind") == "Permission" for item in gap_events),
    "events": gap_events,
}

print(json.dumps({"scope": "TqSdk 3.8.7 rtn_data notify surrogate; no account login", "notify": notify_report, "gap": gap_report}, ensure_ascii=False, indent=2))
