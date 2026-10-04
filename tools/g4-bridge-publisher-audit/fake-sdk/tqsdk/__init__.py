__version__ = "3.8.7"


class TqAuth:
    def __init__(self, account, password):
        self.account = account
        self.password = password


class _Frame:
    def __init__(self, rows):
        self.rows = rows

    def iterrows(self):
        return enumerate(self.rows)


class TqApi:
    def __init__(self, auth=None, disable_print=True):
        self.auth = auth

    def get_kline_serial(self, symbol, duration, data_length=8000):
        start = __import__("datetime").datetime(2026, 9, 29, 9, 0, tzinfo=__import__("datetime").timezone(__import__("datetime").timedelta(hours=8)))
        rows = []
        for index in range(5):
            timestamp = start + __import__("datetime").timedelta(minutes=index)
            rows.append({"datetime": int(timestamp.timestamp() * 1_000_000_000), "open": 100 + index, "high": 102 + index, "low": 99 + index, "close": 101 + index, "volume": 10, "open_oi": 5, "close_oi": 6, "trading_day": "2026-09-29"})
        return _Frame(rows)

    def wait_update(self, **kwargs):
        return True

    def close(self):
        return None
