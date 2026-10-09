"""G2 trusted local Python signal replay worker.

The parent sends one completed bar per JSON command. No future bar sequence is
ever supplied to the strategy context.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import sys
import time
import traceback
from pathlib import Path


def send(payload: dict) -> None:
    try:
        sys.stdout.write(json.dumps(payload, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    except (BrokenPipeError, OSError):
        raise SystemExit(0)


def exception_payload(path: str, exc: Exception) -> dict:
    return {"strategy_path": str(Path(path).resolve()), "traceback": traceback.format_exc(), "message": str(exc)}


def load_module(path: str):
    module_path = Path(path).resolve()
    spec = importlib.util.spec_from_file_location("qh_trusted_strategy", module_path)
    if spec is None or spec.loader is None:
        raise ValueError(f"无法加载策略文件：{module_path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def validate_metadata(module) -> dict:
    metadata = getattr(module, "STRATEGY", None)
    if not isinstance(metadata, dict):
        raise ValueError("策略缺少 STRATEGY 元数据")
    for key in ("name", "version", "kind", "warmup_bars", "parameters"):
        if key not in metadata:
            raise ValueError(f"策略元数据缺少 {key}")
    if metadata["kind"] not in ("signal", "trade"):
        raise ValueError("策略 kind 必须是 signal 或 trade")
    if not isinstance(metadata["parameters"], dict):
        raise ValueError("parameters 必须是对象")
    if not isinstance(metadata["warmup_bars"], int) or metadata["warmup_bars"] < 0:
        raise ValueError("warmup_bars 必须是非负整数")
    if "warmup_bars_parameters" in metadata:
        if not isinstance(metadata["warmup_bars_parameters"], list) or not all(isinstance(item, str) for item in metadata["warmup_bars_parameters"]):
            raise ValueError("warmup_bars_parameters 必须是字符串数组")
    return metadata


def parameters_with_defaults(metadata: dict, supplied: dict | None) -> dict:
    supplied = supplied or {}
    result = {}
    for name, spec in metadata["parameters"].items():
        if not isinstance(spec, dict) or "type" not in spec or "default" not in spec:
            raise ValueError(f"参数定义无效：{name}")
        value = supplied[name] if name in supplied else spec["default"]
        kind = spec["type"]
        if kind == "int" and (isinstance(value, bool) or not isinstance(value, int)):
            raise ValueError(f"参数 {name} 必须是整数")
        if kind == "float" and (isinstance(value, bool) or not isinstance(value, (int, float))):
            raise ValueError(f"参数 {name} 必须是数字")
        if kind == "str" and not isinstance(value, str):
            raise ValueError(f"参数 {name} 必须是字符串")
        if kind == "bool" and not isinstance(value, bool):
            raise ValueError(f"参数 {name} 必须是布尔值")
        if "min" in spec and value < spec["min"]:
            raise ValueError(f"参数 {name} 小于最小值")
        if "max" in spec and value > spec["max"]:
            raise ValueError(f"参数 {name} 大于最大值")
        if "choices" in spec and value not in spec["choices"]:
            raise ValueError(f"参数 {name} 不在允许值内")
        result[name] = value
    unknown = sorted(set(supplied) - set(metadata["parameters"]))
    if unknown:
        raise ValueError("未知参数：" + ", ".join(unknown))
    return result


class IndicatorState:
    def __init__(self):
        self.closes: list[float] = []
        self.trs: list[float] = []
        self.ema_states: dict[int, dict] = {}
        self.atr_states: dict[int, dict] = {}

    def update(self, bar: dict) -> None:
        close = float(bar["close"])
        high = float(bar["high"])
        low = float(bar["low"])
        if self.closes:
            previous = self.closes[-1]
            tr = max(high - low, abs(high - previous), abs(low - previous))
        else:
            tr = high - low
        self.closes.append(close)
        self.trs.append(tr)

    def sma(self, period: int):
        if period < 1 or len(self.closes) < period:
            return None
        return sum(self.closes[-period:]) / period

    def ema(self, period: int):
        if period < 1 or len(self.closes) < period:
            return None
        state = self.ema_states.setdefault(period, {"last": 0, "value": None})
        if state["last"] == 0:
            state["value"] = sum(self.closes[:period]) / period
            state["last"] = period
        alpha = 2.0 / (period + 1)
        while state["last"] < len(self.closes):
            close = self.closes[state["last"]]
            state["value"] = state["value"] + alpha * (close - state["value"])
            state["last"] += 1
        return state["value"]

    def atr(self, period: int):
        if period < 1 or len(self.trs) < period:
            return None
        state = self.atr_states.setdefault(period, {"last": 0, "value": None})
        if state["last"] == 0:
            state["value"] = sum(self.trs[:period]) / period
            state["last"] = period
        while state["last"] < len(self.trs):
            state["value"] = (state["value"] * (period - 1) + self.trs[state["last"]]) / period
            state["last"] += 1
        return state["value"]


class Context:
    def __init__(self, bar: dict, bar_end: str, is_formal: bool, parameters: dict, indicator_state: IndicatorState, bar_index: int, simulation: dict):
        self.bar = bar
        self.bar_end = bar_end
        self.is_formal = is_formal
        self.parameters = parameters
        self.bar_index = bar_index
        self.simulation = simulation
        self.__indicator_state = indicator_state

    def sma(self, period: int):
        return self.__indicator_state.sma(int(period))

    def ema(self, period: int):
        return self.__indicator_state.ema(int(period))

    def atr(self, period: int):
        return self.__indicator_state.atr(int(period))


def describe(path: str) -> int:
    try:
        metadata = validate_metadata(load_module(path))
        send({"type": "descriptor", "metadata": metadata})
        return 0
    except Exception as exc:
        send({"type": "error", "kind": "load", **exception_payload(path, exc)})
        return 2


def run(path: str, timeout_seconds: float) -> int:
    try:
        module = load_module(path)
        metadata = validate_metadata(module)
        send({"type": "ready", "metadata": metadata})
    except Exception as exc:
        send({"type": "error", "kind": "load", **exception_payload(path, exc)})
        return 2

    state = IndicatorState()
    strategy = None
    parameters = None
    seen_bar_ends: set[str] = set()
    processed = 0
    formal_signals = 0
    warmup_signals = 0

    for line in sys.stdin:
        try:
            command = json.loads(line)
        except json.JSONDecodeError as exc:
            send({"type": "error", "kind": "protocol", "message": str(exc)})
            return 3
        name = command.get("command")
        if name == "start":
            try:
                parameters = parameters_with_defaults(metadata, command.get("parameters"))
                factory = getattr(module, "create_strategy", None)
                if factory is None:
                    raise ValueError("策略缺少 create_strategy(parameters)")
                strategy = factory(parameters)
                send({"type": "started", "parameters": parameters, "formal_boundary": "[startInclusive,endExclusive)"})
            except Exception as exc:
                send({"type": "error", "kind": "parameters", **exception_payload(path, exc)})
                return 4
        elif name == "bar":
            if strategy is None or parameters is None:
                send({"type": "error", "kind": "protocol", "message": "start 之前不能发送 bar"})
                return 3
            bar = command.get("bar") or {}
            bar_end = str(command.get("bar_end"))
            if not bar.get("is_complete", False):
                send({"type": "error", "kind": "incomplete_bar", "bar_end": bar_end, "message": "策略输入包含未完成 K 线"})
                return 5
            if bar_end in seen_bar_ends:
                send({"type": "bar_done", "bar_end": bar_end, "formal": bool(command.get("formal")), "duplicate": True, "processed": processed})
                continue
            seen_bar_ends.add(bar_end)
            state.update(bar)
            context = Context(bar=bar, bar_end=bar_end, is_formal=bool(command.get("formal")), parameters=parameters, indicator_state=state, bar_index=processed, simulation=command.get("simulation") or {})
            strategy_started = time.perf_counter()
            try:
                output = strategy.on_bar(context)
            except Exception as exc:
                send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, exc)})
                return 7
            outputs = [] if output is None else output if isinstance(output, list) else [output]
            for output_item in outputs:
                if not isinstance(output_item, dict):
                    error = ValueError("策略信号必须是对象")
                    send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, error)})
                    return 7
                signal = output_item.get("signal") if metadata["kind"] == "trade" else output_item
                intent = output_item.get("intent") if metadata["kind"] == "trade" else None
                if signal is not None:
                    if not isinstance(signal, dict) or not signal.get("category") or not signal.get("direction") or not signal.get("description"):
                        error = ValueError("信号缺少 category/direction/description")
                        send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, error)})
                        return 7
                    send({"type": "signal", "bar_end": bar_end, "formal": context.is_formal, "signal": signal})
                    if context.is_formal:
                        formal_signals += 1
                    else:
                        warmup_signals += 1
                if intent is not None:
                    if not isinstance(intent, dict) or intent.get("action") not in ("open", "open_limit", "close", "reverse", "protect"):
                        error = ValueError("交易意图 action 必须是 open/open_limit/close/reverse/protect")
                        send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, error)})
                        return 7
                    if intent.get("direction") not in ("long", "short"):
                        error = ValueError("交易意图 direction 必须是 long/short")
                        send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, error)})
                        return 7
                    quantity = intent.get("quantity", 1)
                    if isinstance(quantity, bool) or not isinstance(quantity, int) or quantity < 1:
                        error = ValueError("交易意图 quantity 必须是正整数")
                        send({"type": "error", "kind": "runtime", "bar_end": bar_end, **exception_payload(path, error)})
                        return 7
                    send({"type": "intent", "bar_end": bar_end, "formal": context.is_formal, "intent": intent, "signal": signal})
            strategy_elapsed_ms = (time.perf_counter() - strategy_started) * 1000.0
            processed += 1
            send({"type": "bar_done", "bar_end": bar_end, "formal": context.is_formal, "duplicate": False, "processed": processed, "strategy_elapsed_ms": strategy_elapsed_ms})
        elif name == "finish":
            send({"type": "completed", "processed": processed, "formal_signals": formal_signals, "warmup_signals": warmup_signals})
            return 0
        elif name == "cancel":
            send({"type": "cancelled", "processed": processed})
            return 0
        elif name == "stop":
            send({"type": "stopped", "processed": processed})
            return 0
        else:
            send({"type": "error", "kind": "protocol", "message": f"未知命令：{name}"})
            return 3
    send({"type": "stopped", "reason": "stdin_closed", "processed": processed})
    return 0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--describe")
    parser.add_argument("--strategy")
    parser.add_argument("--timeout", type=float, default=2.0)
    args = parser.parse_args()
    if args.describe:
        return describe(args.describe)
    if not args.strategy:
        send({"type": "error", "kind": "load", "message": "缺少 --strategy"})
        return 2
    return run(args.strategy, args.timeout)


if __name__ == "__main__":
    raise SystemExit(main())
