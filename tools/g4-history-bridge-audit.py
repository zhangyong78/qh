import csv
import datetime as dt
import importlib.util
import json
import sys
import types

spec = importlib.util.spec_from_file_location("qh_tqsdk_bridge_history", "worker/qh_tqsdk_bridge.py")
bridge = importlib.util.module_from_spec(spec)
spec.loader.exec_module(bridge)
start = dt.datetime(2026, 9, 29, 9, 0, tzinfo=bridge.BEIJING)
request = {"symbol": "TEST.FAKE", "duration_seconds": 60, "start": start.isoformat(), "end": (start + dt.timedelta(minutes=2)).isoformat(),
           "provider_trading_days": ["2026-09-29"],
           "provider_session_windows": ["09:00-10:15", "10:30-11:30", "13:30-15:00", "21:00-23:00", "23:00-02:30"],
           "provider_calendar_source": "fixture"}


class Downloader:
    include_successor = True
    explicit = None

    def __init__(self, api, symbol_list, dur_sec, start_dt, end_dt, csv_file_name):
        with open(csv_file_name, "w", newline="", encoding="utf-8") as target:
            writer = csv.writer(target)
            columns = ["datetime", "datetime_nano", f"{symbol_list}.open", f"{symbol_list}.high", f"{symbol_list}.low", f"{symbol_list}.close", f"{symbol_list}.volume", f"{symbol_list}.close_oi", f"{symbol_list}.trading_day"]
            if self.explicit is not None:
                columns.append(f"{symbol_list}.is_complete")
            writer.writerow(columns)
            count = 3 if self.include_successor else 2
            for index in range(count):
                timestamp = start_dt + dt.timedelta(minutes=index)
                values = [timestamp.isoformat(), int(timestamp.timestamp() * 1_000_000_000), 100, 102, 99, 101, 10, 6, "2026-10-09"]
                if self.explicit is not None:
                    values.append(self.explicit)
                writer.writerow(values)

    def is_finished(self):
        return True


module = types.ModuleType("tqsdk.tools")
module.DataDownloader = Downloader
sys.modules["tqsdk.tools"] = module
successor = bridge.professional_history(request, object())
Downloader.include_successor = False
no_successor = bridge.professional_history(request, object())
Downloader.include_successor = True
Downloader.explicit = "false"
explicit_false = bridge.professional_history(request, object())


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


class StandardApi:
    def get_kline_serial(self, *args, **kwargs):
        rows = []
        for index in range(3):
            timestamp = start + dt.timedelta(minutes=index)
            rows.append({"datetime": int(timestamp.timestamp() * 1_000_000_000), "open": 100, "high": 102, "low": 99, "close": 101, "volume": 10, "close_oi": 6, "trading_day": "2026-10-09"})
        return Frame(rows)

    def wait_update(self, **kwargs):
        return True


standard = bridge.standard_history(request, StandardApi())
report = {
    "scope": "current history bridge SDK-format surrogate; no account login or market data",
    "successor_tail_complete": successor["bars"][-1]["is_complete"],
    "successor_coverage_complete": successor["complete"],
    "elapsed_tail_complete_without_successor": no_successor["bars"][-1]["is_complete"],
    "future_tail_incomplete_without_successor": not bridge.completion_from_provider(
        None, False, dt.datetime.now(bridge.BEIJING) + dt.timedelta(minutes=1), 60),
    "explicit_false_not_true": not explicit_false["bars"][0]["is_complete"],
    "standard_supplier_trading_day": standard["bars"][0]["trading_day"],
    "permission_classification": bridge.classify_exception(Exception("您的账户不支持下载历史数据功能，需要购买后才能使用。")),
}
print(json.dumps(report, ensure_ascii=False, indent=2))
