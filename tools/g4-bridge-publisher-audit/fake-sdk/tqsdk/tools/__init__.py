import csv
import datetime


class DataDownloader:
    def __init__(self, api, symbol_list, dur_sec, start_dt, end_dt, csv_file_name):
        with open(csv_file_name, "w", newline="", encoding="utf-8") as target:
            writer = csv.writer(target)
            writer.writerow(["datetime", "datetime_nano", f"{symbol_list}.open", f"{symbol_list}.high", f"{symbol_list}.low", f"{symbol_list}.close", f"{symbol_list}.volume", f"{symbol_list}.open_oi", f"{symbol_list}.close_oi", f"{symbol_list}.trading_day"])
            current = start_dt
            limit = min(end_dt, start_dt + datetime.timedelta(minutes=4))
            while current <= limit:
                writer.writerow([current.isoformat(), int(current.timestamp() * 1_000_000_000), 100, 102, 99, 101, 10, 5, 6, "2026-09-29"])
                current += datetime.timedelta(seconds=dur_sec)

    def is_finished(self):
        return True
