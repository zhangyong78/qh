"""Port of QQOKX's EMA dynamic limit-long strategy for local futures replay.

Entry uses an EMA limit order. Exit stop updates are evaluated on completed
OHLC bars and close at the next valid bar open in QH's local replay model.
"""

import math

STRATEGY = {
    "name": "EMA 动态委托做多（QQOKX）",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 55,
    "warmup_bars_parameters": ["ema_period", "trend_ema_period", "atr_period", "entry_ema_period"],
    "parameters": {
        "min_tick": {"type": "float", "default": 1.0, "min": 0.000001, "max": 1000000},
        "dynamic_fee_offset_points": {"type": "float", "default": 1.0, "min": 0.0, "max": 1000000},
        "ema_period": {"type": "int", "default": 21, "min": 2, "max": 500},
        "trend_ema_period": {"type": "int", "default": 55, "min": 2, "max": 1000},
        "entry_ema_period": {"type": "int", "default": 55, "min": 2, "max": 1000},
        "atr_period": {"type": "int", "default": 10, "min": 2, "max": 500},
        "atr_stop_multiplier": {"type": "float", "default": 2.0, "min": 0.1, "max": 20.0},
        "max_entries_per_trend": {"type": "int", "default": 1, "min": 1, "max": 10},
    },
}


def create_strategy(parameters):
    return Strategy(parameters)


class Strategy:
    def __init__(self, parameters):
        self.min_tick = float(parameters["min_tick"])
        self.dynamic_fee_offset_points = float(parameters["dynamic_fee_offset_points"])
        self.ema_period = parameters["ema_period"]
        self.trend_ema_period = parameters["trend_ema_period"]
        self.entry_ema_period = parameters["entry_ema_period"]
        self.atr_period = parameters["atr_period"]
        self.atr_stop_multiplier = float(parameters["atr_stop_multiplier"])
        self.max_entries_per_trend = parameters["max_entries_per_trend"]
        self.entries_in_trend = 0
        self.pending_risk_distance = None
        self.position_entry_price = None
        self.initial_risk_distance = None
        self.active_stop = None
        self.max_favorable_r = 0.0
        self.was_in_position = False

    def _snap(self, price, mode):
        units = price / self.min_tick
        if mode == "up":
            return math.ceil(units - 1e-12) * self.min_tick
        if mode == "down":
            return math.floor(units + 1e-12) * self.min_tick
        return math.floor(units + 0.5) * self.min_tick

    @staticmethod
    def _close_intent(reason):
        return {
            "signal": {
                "category": "close",
                "direction": "long",
                "description": reason,
            },
            "intent": {
                "action": "close",
                "direction": "long",
                "quantity": 1,
                "description": reason,
            },
        }

    def on_bar(self, context):
        fast = context.ema(self.ema_period)
        trend = context.ema(self.trend_ema_period)
        entry_reference = context.ema(self.entry_ema_period)
        atr_value = context.atr(self.atr_period)
        if None in (fast, trend, entry_reference, atr_value):
            return None

        position = context.simulation.get("position")
        trend_is_up = fast > trend and context.bar["close"] > trend
        if fast <= trend:
            self.entries_in_trend = 0

        if position is not None:
            entry = float(position["entry_price"])
            new_position = not self.was_in_position or entry != self.position_entry_price
            if new_position:
                self.position_entry_price = entry
                risk_distance = self.pending_risk_distance or float(atr_value) * self.atr_stop_multiplier
                self.active_stop = self._snap(entry - risk_distance, "down")
                self.initial_risk_distance = max(entry - self.active_stop, self.min_tick)
                self.max_favorable_r = 0.0
                self.entries_in_trend += 1
            self.was_in_position = True

            # The replay fills a pending limit from this completed bar's OHLC.
            # Do not use that same bar's high/low to manage a position that may
            # have entered later within it; doing so would introduce look-ahead.
            if new_position:
                return None

            low = float(context.bar["low"])
            if low <= self.active_stop:
                return self._close_intent(
                    f"收盘后确认本根触及动态保护价 {self.active_stop:.4f}；下一根有效K线开盘模拟平仓"
                )

            high = float(context.bar["high"])
            trigger_r = math.floor(
                (high - entry - self.dynamic_fee_offset_points) / self.initial_risk_distance + 1e-12
            )
            if trigger_r >= 11:
                locked_r = trigger_r - 1
            elif trigger_r >= 4:
                locked_r = trigger_r - 3
            elif trigger_r >= 1:
                locked_r = 0
            else:
                locked_r = None
            if locked_r is not None:
                candidate = self._snap(
                    entry + locked_r * self.initial_risk_distance + self.dynamic_fee_offset_points,
                    "up",
                )
                self.active_stop = max(self.active_stop, candidate)
            return None

        if self.was_in_position:
            self.was_in_position = False
            self.position_entry_price = None
            self.initial_risk_distance = None
            self.active_stop = None
            self.max_favorable_r = 0.0

        if not trend_is_up or self.entries_in_trend >= self.max_entries_per_trend:
            return None

        reference = self._snap(float(entry_reference), "nearest")
        if reference <= 0:
            return None
        self.pending_risk_distance = float(atr_value) * self.atr_stop_multiplier
        description = (
            f"EMA{self.ema_period}>EMA{self.trend_ema_period} 且收盘站上趋势EMA；"
            f"按EMA{self.entry_ema_period}={reference:.4f} 挂限价多单，未成交则下一根按新EMA重挂"
        )
        return {
            "signal": {
                "category": "entry_limit",
                "direction": "long",
                "description": description,
                "reference_price": reference,
            },
            "intent": {
                "action": "open_limit",
                "direction": "long",
                "quantity": 1,
                "description": description,
                "reference_price": reference,
            },
        }
