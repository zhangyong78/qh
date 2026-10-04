"""Isolated TqSdk bridge.

The parent sends one JSON request on stdin and receives one JSON response on
stdout. Credentials never appear in argv or diagnostics. No synthetic market
data is produced by this module.
"""
import csv
import contextlib
import datetime as dt
import importlib.metadata
import importlib.util
import io
import json
import math
import os
import socket
import sys
import tempfile
import time
import uuid
from collections.abc import Mapping


BEIJING = dt.timezone(dt.timedelta(hours=8))
project_sdk_path = os.environ.get("QH_TQSDK_PATH")
if project_sdk_path and project_sdk_path not in sys.path:
    sys.path.insert(0, project_sdk_path)


def reply(payload):
    sys.stdout.write(json.dumps(payload, ensure_ascii=False, separators=(",", ":")) + "\n")
    sys.stdout.flush()


def fail(kind, message):
    reply({"ok": False, "error_kind": kind, "message": message})


def classify_exception(error):
    type_name = type(error).__name__.lower()
    text = f"{type(error).__name__}: {error}".lower()
    if isinstance(error, PermissionError):
        return "Permission"
    if isinstance(error, (TimeoutError, socket.timeout)) or any(token in text for token in ("timeout", "timed out", "超时")):
        return "Timeout"
    if isinstance(error, (ImportError, ModuleNotFoundError)) or any(token in text for token in ("module not found", "no module named", "cannot import", "import error")):
        return "Unsupported"
    if any(token in text for token in ("auth", "authentication", "unauthorized", "password", "credential", "login", "invalid account", "account not found", "登录", "认证", "鉴权", "密码", "账号无效")):
        return "Authentication"
    if any(token in text for token in ("permission", "forbidden", "denied", "权限", "professional", "tq_dl", "购买后", "不支持下载历史数据")):
        return "Permission"
    if isinstance(error, (ConnectionError, socket.gaierror)) or any(token in text for token in (
        "connection", "connect", "dns", "name resolution", "host", "network", "unreachable", "refused", "reset by peer", "websocket", "ssl"
    )):
        return "Network"
    if any(token in text for token in ("initializ", "incompatible", "版本不兼容", "sdk 初始化", "tqsdk 初始化")) or any(token in type_name for token in ("attributeerror", "runtimeerror")):
        return "Unsupported"
    return "Unknown"


def installed_version(tqsdk):
    version = getattr(tqsdk, "__version__", None)
    if version:
        return str(version)
    try:
        return importlib.metadata.version("tqsdk")
    except importlib.metadata.PackageNotFoundError:
        return "unknown"


def import_sdk():
    # TqSdk emits a disclaimer during import; keep it out of the JSON stdout
    # protocol and out of the credential/error surface.
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        import tqsdk
        from tqsdk import TqApi, TqAuth
    return tqsdk, TqApi, TqAuth


def parse_datetime(value):
    if isinstance(value, (int, float)):
        return dt.datetime.fromtimestamp(float(value) / 1_000_000_000, tz=dt.timezone.utc).astimezone(BEIJING)
    text = str(value).strip()
    if not text:
        return None
    parsed = dt.datetime.fromisoformat(text.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        parsed = parsed.replace(tzinfo=BEIJING)
    return parsed.astimezone(BEIJING)


def number(value):
    try:
        result = float(value)
        return None if math.isnan(result) or math.isinf(result) else result
    except (TypeError, ValueError):
        return None


def period_label(duration_seconds):
    if duration_seconds % 86400 == 0:
        return f"{duration_seconds // 86400}d"
    if duration_seconds % 3600 == 0:
        return f"{duration_seconds // 3600}h"
    if duration_seconds % 60 == 0:
        return f"{duration_seconds // 60}m"
    return f"{duration_seconds}s"


def trading_day_from_timestamp(timestamp):
    # This mirrors the verified TqSdk trading-day rule: a timestamp at or
    # after 18:00 belongs to the next trading day, with weekends rolled to
    # Monday. It is not the same as timestamp.date() for night sessions.
    day = timestamp.date()
    if timestamp.hour >= 18:
        day += dt.timedelta(days=1)
    while day.weekday() >= 5:
        day += dt.timedelta(days=1)
    return day.isoformat()


TRADING_SESSIONS = ((dt.time(9, 0), dt.time(10, 15)), (dt.time(10, 30), dt.time(11, 30)),
                    (dt.time(13, 30), dt.time(15, 0)), (dt.time(21, 0), dt.time(23, 0)),
                    (dt.time(23, 0), dt.time(2, 30)))


def is_tradable_timestamp(timestamp):
    local = timestamp.astimezone(BEIJING)
    value = local.time().replace(tzinfo=None)
    return any(start <= value < end if start < end else value >= start or value < end
               for start, end in TRADING_SESSIONS)


def next_expected_timestamp(timestamp, duration_seconds):
    candidate = timestamp + dt.timedelta(seconds=duration_seconds)
    for _ in range(3 * 24 * 60 * 60 // max(duration_seconds, 1) + 2):
        if is_tradable_timestamp(candidate):
            return candidate
        candidate += dt.timedelta(seconds=duration_seconds)
    return None


def parse_session_windows(values):
    parsed = []
    for value in values or []:
        if not isinstance(value, str) or "-" not in value:
            return None
        start_text, end_text = (part.strip() for part in value.split("-", 1))
        try:
            session_start = dt.time.fromisoformat(start_text)
            session_end = dt.time.fromisoformat(end_text)
        except ValueError:
            return None
        parsed.append((session_start, session_end))
    return parsed or None


def expected_minute_timestamps(request_start, request_end, duration_seconds, session_windows, provider_trading_days, session_ranges=None, symbol=None):
    if session_ranges:
        expected = set()
        allowed_days = {str(value).strip()[:10] for value in (provider_trading_days or [])}
        for item in session_ranges:
            if not isinstance(item, dict) or str(item.get("trading_day", ""))[:10] not in allowed_days:
                return None
            scope = item.get("symbol_scope")
            if scope is not None and symbol is not None and scope != symbol:
                return None
            try:
                session_begin = parse_datetime(item["start"])
                session_finish = parse_datetime(item["end"])
            except (KeyError, TypeError, ValueError):
                return None
            if session_begin is None or session_finish is None or session_finish <= session_begin:
                return None
            candidate = session_begin
            while candidate < session_finish:
                if request_start <= candidate < request_end:
                    expected.add(candidate)
                candidate += dt.timedelta(seconds=duration_seconds)
        return expected or None
    parsed = parse_session_windows(session_windows)
    if parsed is None:
        return None
    expected = set()
    allowed_days = {str(value).strip()[:10] for value in (provider_trading_days or [])}
    if not allowed_days:
        return None
    day = request_start.date() - dt.timedelta(days=1)
    last_day = request_end.date() + dt.timedelta(days=1)
    while day <= last_day:
        if day.isoformat() not in allowed_days:
            day += dt.timedelta(days=1)
            continue
        for session_start, session_end in parsed:
            session_begin = dt.datetime.combine(day, session_start, tzinfo=BEIJING)
            finish_day = day + dt.timedelta(days=1) if session_end <= session_start else day
            session_finish = dt.datetime.combine(finish_day, session_end, tzinfo=BEIJING)
            candidate = session_begin
            while candidate < session_finish:
                if request_start <= candidate < request_end:
                    expected.add(candidate)
                candidate += dt.timedelta(seconds=duration_seconds)
        day += dt.timedelta(days=1)
    return expected


def expected_live_timestamps(duration_seconds, provider_trading_days, session_ranges, symbol):
    if not provider_trading_days or not session_ranges:
        return None
    allowed_days = {str(value).strip()[:10] for value in provider_trading_days}
    expected = set()
    for item in session_ranges:
        if not isinstance(item, dict) or str(item.get("trading_day", ""))[:10] not in allowed_days:
            return None
        scope = item.get("symbol_scope")
        if scope is not None and scope != symbol:
            return None
        try:
            session_begin = parse_datetime(item["start"])
            session_finish = parse_datetime(item["end"])
        except (KeyError, TypeError, ValueError):
            return None
        if session_begin is None or session_finish is None or session_finish <= session_begin:
            return None
        candidate = session_begin
        while candidate < session_finish:
            expected.add(candidate)
            candidate += dt.timedelta(seconds=duration_seconds)
    return sorted(expected) or None


def parse_completion(value):
    if value is None:
        return None
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return bool(value) if math.isfinite(float(value)) else False
    text = str(value).strip().lower()
    if text in ("true", "1", "yes", "y", "complete", "completed"):
        return True
    if text in ("false", "0", "no", "n", "incomplete", "unfinished"):
        return False
    return False


def completion_from_provider(value, has_successor, timestamp=None, duration_seconds=None, observed_at=None):
    explicit = parse_completion(value)
    if explicit is not None:
        return explicit
    if has_successor:
        return True
    if timestamp is None or not duration_seconds:
        return False
    observed_at = observed_at or dt.datetime.now(BEIJING)
    if observed_at.tzinfo is None:
        observed_at = observed_at.replace(tzinfo=BEIJING)
    return timestamp + dt.timedelta(seconds=duration_seconds) <= observed_at


def successor_flags(timestamps, duration_seconds):
    """Return successor evidence in one reverse pass, without quadratic scans."""
    flags = [False] * len(timestamps)
    latest = None
    for index in range(len(timestamps) - 1, -1, -1):
        timestamp = timestamps[index]
        if timestamp is not None and latest is not None and latest >= timestamp + dt.timedelta(seconds=duration_seconds):
            flags[index] = True
        if timestamp is not None and (latest is None or timestamp > latest):
            latest = timestamp
    return flags


def bar_from_values(symbol, duration_seconds, timestamp, values, sequence, is_complete=True):
    timestamp = parse_datetime(timestamp)
    if timestamp is None:
        return None
    open_price = number(values.get("open"))
    high = number(values.get("high"))
    low = number(values.get("low"))
    close = number(values.get("close"))
    volume = number(values.get("volume"))
    open_interest = number(values.get("close_oi", values.get("open_oi", 0))) or 0
    if None in (open_price, high, low, close, volume):
        return None
    supplier_trading_day = values.get("trading_day")
    trading_day = str(supplier_trading_day).strip()[:10] if supplier_trading_day else trading_day_from_timestamp(timestamp)
    exchange = symbol.split(".", 1)[0]
    return {"timestamp": timestamp.isoformat(), "symbol": symbol, "exchange": exchange,
            "period": period_label(duration_seconds), "trading_day": trading_day,
            "open": open_price, "high": high, "low": low, "close": close,
            "volume": volume, "open_interest": open_interest, "is_complete": is_complete,
            "sequence": sequence}


def coverage_payload(bars, request_start, request_end, used_professional, note, duration_seconds, provider_trading_days=None, coverage_evidence=None, provider_session_windows=None, provider_calendar_source=None, provider_session_ranges=None, symbol=None):
    if not bars:
        return None
    first = parse_datetime(bars[0]["timestamp"])
    last = parse_datetime(bars[-1]["timestamp"])
    expected = set()
    calendar_complete = True
    if duration_seconds < 86400:
        if not provider_trading_days or not provider_calendar_source or (not provider_session_windows and not provider_session_ranges):
            return {"first": first.isoformat(), "last": last.isoformat(), "rows": len(bars),
                    "complete": False, "used_professional_downloader": used_professional,
                    "coverage_note": note + "; minute trading-day/session calendar metadata is unavailable"}
        expected = expected_minute_timestamps(request_start, request_end, duration_seconds, provider_session_windows, provider_trading_days, provider_session_ranges, symbol)
        if expected is None:
            return {"first": first.isoformat(), "last": last.isoformat(), "rows": len(bars),
                    "complete": False, "used_professional_downloader": used_professional,
                    "coverage_note": note + "; minute session calendar metadata is invalid"}
    else:
        if provider_trading_days is None:
            return {"first": first.isoformat(), "last": last.isoformat(), "rows": len(bars),
                    "complete": False, "used_professional_downloader": used_professional,
                    "coverage_note": note + "; daily trading calendar evidence is unavailable"}
        expected_days = {str(value).strip()[:10] for value in provider_trading_days
                         if request_start.date().isoformat() <= str(value).strip()[:10] < request_end.date().isoformat()}
        actual_days = {str(item.get("trading_day", "")).strip()[:10] for item in bars}
        calendar_complete = expected_days.issubset(actual_days) and bool(expected_days)
    actual = {parse_datetime(item["timestamp"]) for item in bars}
    complete = (bool(bars) and all(item.get("is_complete", False) for item in bars)
                and first >= request_start
                and (duration_seconds >= 86400 or last + dt.timedelta(seconds=duration_seconds) >= max(expected) + dt.timedelta(seconds=duration_seconds))
                and (calendar_complete if duration_seconds >= 86400 else bool(expected) and expected.issubset(actual)))
    return {"first": first.isoformat(), "last": last.isoformat(), "rows": len(bars),
            "complete": complete, "used_professional_downloader": used_professional,
            "coverage_note": note if complete else note + "; requested range is not fully covered"}


def probe(request):
    if not request.get("account") or not request.get("password"):
        return {"ok": False, "error_kind": "Authentication", "message": "account/password required"}
    if importlib.util.find_spec("tqsdk") is None:
        return {"ok": False, "error_kind": "Unsupported", "message": "tqsdk is not installed in the selected project runtime"}
    try:
        tqsdk, TqApi, TqAuth = import_sdk()
        api = TqApi(auth=TqAuth(request["account"], request["password"]), disable_print=True)
        try:
            api.wait_update(deadline=time.time() + 8)
            return {"ok": True, "target_version": installed_version(tqsdk), "connected": True,
                    "can_get_kline_serial": True, "can_data_downloader": None,
                    "kline_serial_max_rows": 8000, "max_concrete_subscriptions": 20,
                    "initial_subscription_limit": 5,
                    "evidence": "TqApi/TqAuth wait_update connection probe; DataDownloader permission unverified"}
        finally:
            api.close()
    except Exception as error:
        return {"ok": False, "error_kind": classify_exception(error),
                "message": "TqSdk connection failed; detailed exception is kept out of logs"}


def runtime_probe():
    if importlib.util.find_spec("tqsdk") is None:
        return {"ok": False, "error_kind": "Unsupported", "message": "tqsdk is not installed in the selected project runtime"}
    try:
        tqsdk, _, _ = import_sdk()
        downloader_module = importlib.util.find_spec("tqsdk.tools") is not None
        return {"ok": True, "target_version": installed_version(tqsdk),
                "can_data_downloader_module": downloader_module,
                "evidence": "project-isolated TqSdk import; no account login performed"}
    except Exception:
        return {"ok": False, "error_kind": "Unsupported", "message": "project-isolated TqSdk import failed"}


def standard_history(request, api):
    symbol = request["symbol"]
    duration = int(request["duration_seconds"])
    start = parse_datetime(request["start"])
    end = parse_datetime(request["end"])
    klines = api.get_kline_serial(symbol, duration, data_length=8000)
    api.wait_update(deadline=time.time() + 30)
    rows = list(klines.iterrows())
    timestamps = [parse_datetime(row.get("datetime")) for _, row in rows]
    completion_flags = successor_flags(timestamps, duration)
    bars = []
    for index, (_, row) in enumerate(rows):
        timestamp = parse_datetime(row.get("datetime"))
        if timestamp is None or timestamp < start or timestamp >= end:
            continue
        values = {key: row.get(key) for key in ("open", "high", "low", "close", "volume", "open_oi", "close_oi", "trading_day")}
        explicit_complete = row.get("is_complete")
        is_complete = completion_from_provider(explicit_complete, completion_flags[index], timestamp, duration)
        bar = bar_from_values(symbol, duration, timestamp, values, index, is_complete)
        if bar is not None:
            bars.append(bar)
    bars.sort(key=lambda item: item["timestamp"])
    coverage = coverage_payload(bars, start, end, False, "TqApi.get_kline_serial last-8000 path", duration, request.get("provider_trading_days"), request.get("coverage_evidence"), request.get("provider_session_windows"), request.get("provider_calendar_source"), request.get("provider_session_ranges"), symbol)
    if not bars:
        return {"ok": False, "error_kind": "Empty", "message": "标准 K 线结果为空"}
    return {"ok": True, "bars": bars, "complete": coverage["complete"],
            "used_professional_downloader": False, "coverage_note": coverage["coverage_note"],
            "provider_request_id": str(uuid.uuid4())}


def professional_history(request, api):
    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
        from tqsdk.tools import DataDownloader
    symbol = request["symbol"]
    duration = int(request["duration_seconds"])
    start = parse_datetime(request["start"])
    end = parse_datetime(request["end"])
    fd, csv_path = tempfile.mkstemp(prefix="qh-tqsdk-", suffix=".csv")
    os.close(fd)
    try:
        downloader = DataDownloader(api, symbol_list=symbol, dur_sec=duration, start_dt=start, end_dt=end, csv_file_name=csv_path)
        while not downloader.is_finished():
            api.wait_update(deadline=time.time() + 30)
        with open(csv_path, newline="", encoding="utf-8-sig") as source:
            rows = list(csv.DictReader(source))
        timestamps = [parse_datetime(row.get("datetime") or row.get("timestamp")) for row in rows]
        completion_flags = successor_flags(timestamps, duration)
        bars = []
        for index, row in enumerate(rows):
            timestamp = parse_datetime(row.get("datetime") or row.get("timestamp"))
            if timestamp is None or timestamp < start or timestamp >= end:
                continue
            values = {name: row.get(f"{symbol}.{name}", row.get(name)) for name in ("open", "high", "low", "close", "volume", "open_oi", "close_oi", "trading_day")}
            explicit_complete = row.get(f"{symbol}.is_complete", row.get("is_complete"))
            is_complete = completion_from_provider(explicit_complete, completion_flags[index], timestamp, duration)
            bar = bar_from_values(symbol, duration, timestamp, values, index, is_complete)
            if bar is not None:
                bars.append(bar)
        bars.sort(key=lambda item: item["timestamp"])
        coverage = coverage_payload(bars, start, end, True, "TqSdk professional DataDownloader path", duration, request.get("provider_trading_days"), request.get("coverage_evidence"), request.get("provider_session_windows"), request.get("provider_calendar_source"), request.get("provider_session_ranges"), symbol)
        if not bars:
            return {"ok": False, "error_kind": "Empty", "message": "DataDownloader 结果为空"}
        return {"ok": True, "bars": bars, "complete": coverage["complete"],
                "used_professional_downloader": True, "coverage_note": coverage["coverage_note"],
                "provider_request_id": str(uuid.uuid4())}
    finally:
        try:
            os.remove(csv_path)
        except OSError:
            pass


def history(request):
    if not request.get("account") or not request.get("password"):
        return {"ok": False, "error_kind": "Authentication", "message": "account/password required"}
    if not request.get("symbol") or int(request.get("duration_seconds", 0)) <= 0:
        return {"ok": False, "error_kind": "InvalidRequest", "message": "symbol/duration_seconds invalid"}
    if importlib.util.find_spec("tqsdk") is None:
        return {"ok": False, "error_kind": "Unsupported", "message": "tqsdk is not installed in the selected project runtime"}
    api = None
    try:
        _, TqApi, TqAuth = import_sdk()
        api = TqApi(auth=TqAuth(request["account"], request["password"]), disable_print=True)
        return professional_history(request, api) if request.get("professional") else standard_history(request, api)
    except Exception as error:
        return {"ok": False, "error_kind": classify_exception(error),
                "message": "TqSdk history request failed; detailed exception is kept out of logs"}
    finally:
        if api is not None:
            try:
                api.close()
            except Exception:
                pass


DISCONNECT_NOTIFY_CODE = 2019112911
RECOVERY_NOTIFY_CODE = 2019112902


def connection_notifications(api, seen_ids):
    """Read the SDK 3.8.7 rtn_data notify entity without exposing its content."""
    data = getattr(api, "_data", None)
    if data is None:
        return []
    try:
        notify = data.get("notify", {})
    except (AttributeError, TypeError):
        return []
    try:
        items = notify.items()
    except AttributeError:
        return []
    events = []
    for notify_id, payload in list(items):
        if str(notify_id).startswith("_") or notify_id in seen_ids:
            continue
        seen_ids.add(notify_id)
        if not isinstance(payload, Mapping):
            continue
        code = payload.get("code")
        if code in (DISCONNECT_NOTIFY_CODE, RECOVERY_NOTIFY_CODE) and payload.get("conn_id") == "md":
            events.append(code)
    return events


def live(request):
    if not request.get("account") or not request.get("password"):
        fail("Authentication", "account/password required")
        return
    if not request.get("symbols") or int(request.get("duration_seconds", 0)) <= 0:
        fail("InvalidRequest", "symbols/duration_seconds invalid")
        return
    if importlib.util.find_spec("tqsdk") is None:
        fail("Unsupported", "tqsdk is not installed in the selected project runtime")
        return
    _, TqApi, TqAuth = import_sdk()
    duration = int(request["duration_seconds"])
    symbols = [str(symbol) for symbol in request["symbols"]]
    sequence = 0
    emitted = set()
    recovering = bool(request.get("last_complete_bar_end"))
    last_complete_bar_end = parse_datetime(request.get("last_complete_bar_end")) if request.get("last_complete_bar_end") else None
    last_confirmed_by_symbol = {symbol: last_complete_bar_end for symbol in symbols}
    seen_notify_ids = set()
    recovery_notify_seen = bool(recovering)
    paused_announced = False
    live_expected_timestamps = {symbol: expected_live_timestamps(duration, request.get("provider_trading_days"), request.get("provider_session_ranges"), symbol) for symbol in symbols}

    def frame_ready(frame):
        if len(frame.index) < 2:
            return False
        for _, row in frame.iloc[-2:].iterrows():
            timestamp = parse_datetime(row.get("datetime"))
            if timestamp is None:
                return False
            values = {name: row.get(name) for name in ("open", "high", "low", "close", "volume")}
            if bar_from_values("READY", duration, timestamp, values, 0, True) is None:
                return False
        return True

    def wait_series_ready(api, series):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if all(frame_ready(frame) for frame in series.values()):
                return True
            if not api.wait_update(deadline=time.time() + 1):
                continue
        return False

    def expected_after(boundary, symbol):
        if boundary is None:
            return None
        return next((candidate for candidate in live_expected_timestamps.get(symbol) or [] if candidate >= boundary), None)

    def rows_from_series(series):
        rows_by_symbol = {}
        for symbol, frame in series.items():
            if len(frame.index) < 2:
                rows_by_symbol[symbol] = []
                continue
            rows = []
            for index, (_, row) in enumerate(frame.iloc[:-1].iterrows()):
                timestamp = parse_datetime(row.get("datetime"))
                if timestamp is None:
                    continue
                values = {name: row.get(name) for name in ("open", "high", "low", "close", "volume", "open_oi", "close_oi", "trading_day")}
                explicit = row.get("is_complete")
                parsed_complete = parse_completion(explicit)
                complete = True if parsed_complete is None else parsed_complete
                if not complete:
                    continue
                bar = bar_from_values(symbol, duration, timestamp, values, index, True)
                if bar is not None:
                    rows.append(bar)
            rows.sort(key=lambda item: item["timestamp"])
            rows_by_symbol[symbol] = rows
        return rows_by_symbol

    def rows_are_contiguous(rows_by_symbol):
        for symbol, rows in rows_by_symbol.items():
            boundary = last_confirmed_by_symbol.get(symbol)
            if boundary is None:
                continue
            rows = [row for row in rows if parse_datetime(row["timestamp"]) + dt.timedelta(seconds=duration) > boundary]
            if not rows:
                continue
            expected = expected_after(boundary, symbol)
            if expected is None:
                return False
            for index, row in enumerate(rows):
                timestamp = parse_datetime(row["timestamp"])
                if timestamp != expected:
                    return False
                expected = expected_after(timestamp + dt.timedelta(seconds=duration), symbol)
                if expected is None:
                    return index == len(rows) - 1
        return True

    def consume_series(series, recovery_mode):
        nonlocal sequence
        rows_by_symbol = rows_from_series(series)
        if any(last_confirmed_by_symbol.get(symbol) is not None and rows and not rows_are_contiguous({symbol: rows}) for symbol, rows in rows_by_symbol.items()):
            raise TimeoutError("TqSdk live update contains an unfillable completed-bar gap; keeping session paused")
        output = []
        for symbol, rows in rows_by_symbol.items():
            boundary = last_confirmed_by_symbol.get(symbol)
            for bar in rows:
                timestamp = parse_datetime(bar["timestamp"])
                if boundary is not None and timestamp + dt.timedelta(seconds=duration) <= boundary:
                    continue
                key = (symbol, bar["timestamp"])
                if key in emitted:
                    continue
                sequence += 1
                bar["sequence"] = sequence
                bar["is_recovery"] = recovery_mode
                emitted.add(key)
                boundary = timestamp + dt.timedelta(seconds=duration)
                last_confirmed_by_symbol[symbol] = boundary
                output.append(bar)
        output.sort(key=lambda item: (item["symbol"], item["timestamp"]))
        for bar in output:
            reply({"ok": True, "event": "bar", "bar": bar})
        return output

    def recovery_boundary_ready(series):
        if not all(frame_ready(frame) for frame in series.values()):
            return False
        rows_by_symbol = rows_from_series(series)
        return rows_are_contiguous(rows_by_symbol)

    while True:
        api = None
        try:
            api = TqApi(auth=TqAuth(request["account"], request["password"]), disable_print=True)
            series = {symbol: api.get_kline_serial(symbol, duration, data_length=5) for symbol in symbols}
            if not wait_series_ready(api, series):
                raise TimeoutError("TqSdk live series readiness timeout")
            tqsdk, _, _ = import_sdk()
            reply({"ok": True, "event": "connected" if not recovering else "recovery_ready", "target_version": installed_version(tqsdk), "recovery_silent": False})
            if recovering:
                output = consume_series(series, True)
                if output or recovery_boundary_ready(series):
                    reply({"ok": True, "event": "recovered", "target_version": installed_version(tqsdk), "recovery_silent": False})
                    recovering = False
                    recovery_notify_seen = False
                    paused_announced = False
            else:
                consume_series(series, False)
            while True:
                if not api.wait_update():
                    continue
                notification_codes = connection_notifications(api, seen_notify_ids)
                for notification_code in notification_codes:
                    if notification_code == DISCONNECT_NOTIFY_CODE:
                        recovering = True
                        recovery_notify_seen = False
                        if not paused_announced:
                            reply({"ok": True, "event": "paused", "message": "TqSdk live session paused; SDK connection notification received"})
                            paused_announced = True
                    elif notification_code == RECOVERY_NOTIFY_CODE:
                        recovering = True
                        recovery_notify_seen = True
                data_changed = any(api.is_changing(frame.iloc[-1], "datetime") for frame in series.values() if len(frame.index) >= 2)
                if recovering and not recovery_notify_seen:
                    continue
                if data_changed or RECOVERY_NOTIFY_CODE in notification_codes:
                    output = consume_series(series, recovering)
                    if recovering and (output or recovery_boundary_ready(series)):
                        reply({"ok": True, "event": "recovered", "target_version": installed_version(tqsdk), "recovery_silent": False})
                        recovering = False
                        recovery_notify_seen = False
                        paused_announced = False
        except Exception as error:
            kind = classify_exception(error)
            if kind in ("Authentication", "Permission", "Unsupported"):
                fail(kind, "TqSdk live session failed; detailed exception is kept out of logs")
                return
            if not paused_announced:
                reply({"ok": True, "event": "paused", "message": "TqSdk live session paused; retrying connection"})
                paused_announced = True
            recovering = True
            recovery_notify_seen = True
            time.sleep(1)
        finally:
            if api is not None:
                try:
                    api.close()
                except Exception:
                    pass


def main(request):
    if request.get("op") == "runtime":
        return runtime_probe()
    if request.get("op") == "probe":
        return probe(request)
    if request.get("op") == "history":
        return history(request)
    if request.get("op") == "live":
        live(request)
        return None
    return {"ok": False, "error_kind": "Protocol", "message": "unknown operation"}


for line in sys.stdin:
    try:
        request = json.loads(line)
        result = main(request)
        if result is not None:
            reply(result)
    except Exception:
        fail("Protocol", "bridge request invalid")
