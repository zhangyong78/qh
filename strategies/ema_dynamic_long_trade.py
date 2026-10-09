"""Port of QQOKX's EMA dynamic limit-long strategy for local futures replay.

Entry uses an EMA limit order. Exit stop updates are evaluated on completed
OHLC bars; protective stops are active intrabar from the next valid bar.
EMA close-confirmed exits execute at the next valid bar open.
"""

import math

STRATEGY = {
    "name": "EMA 动态委托做多（QQOKX）",
    "version": "1.5.1",
    "kind": "trade",
    "warmup_bars": 55,
    "warmup_bars_parameters": ["ema_period", "trend_ema_period", "atr_period", "entry_ema_period"],
    "parameters": {
        "min_tick": {"type": "float", "default": 1.0, "min": 0.000001, "max": 1000000},
        "dynamic_fee_offset_points": {"type": "float", "default": 1.0, "min": 0.0, "max": 1000000},
        "ema_period": {"type": "int", "default": 21, "min": 2, "max": 500},
        "trend_ema_period": {"type": "int", "default": 55, "min": 2, "max": 1000},
        "entry_ema_period": {"type": "int", "default": 55, "min": 2, "max": 1000},
        "atr_period": {"type": "int", "default": 14, "min": 2, "max": 500},
        "atr_stop_multiplier": {"type": "float", "default": 2.0, "min": 0.1, "max": 20.0},
        "profit_lock_start_r": {"type": "int", "default": 4, "min": 3, "max": 20},
        "protection_variant": {"type": "str", "default": "two_r_n_minus_one", "choices": ["current", "two_r_old_ladder", "two_r_n_minus_one"]},
        "max_entries_per_trend": {"type": "int", "default": 1, "min": 0, "max": 10},
        "ema_exit_period": {"type": "int", "default": 55, "choices": [0, 21, 55]},
        "ema_exit_activation_r": {"type": "int", "default": 3, "min": 1, "max": 10},
        "risk_budget": {"type": "float", "default": 1000.0, "min": 1.0, "max": 1000000000.0},
        "contract_multiplier": {"type": "float", "default": 10.0, "min": 0.000001, "max": 1000000.0},
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
        self.profit_lock_start_r = parameters["profit_lock_start_r"]
        self.protection_variant = parameters["protection_variant"]
        self.max_entries_per_trend = parameters["max_entries_per_trend"]
        self.ema_exit_period = parameters["ema_exit_period"]
        self.ema_exit_activation_r = parameters["ema_exit_activation_r"]
        self.risk_budget = float(parameters["risk_budget"])
        self.contract_multiplier = float(parameters["contract_multiplier"])
        self.entries_in_trend = 0
        self.pending_risk_distance = None
        self.position_quantity = 0
        self.position_entry_price = None
        self.initial_risk_distance = None
        self.active_stop = None
        self.active_stop_reason = None
        self.max_favorable_r = 0.0
        self.ema_exit_armed = False
        self.was_in_position = False

    def _snap(self, price, mode):
        units = price / self.min_tick
        if mode == "up":
            return math.ceil(units - 1e-12) * self.min_tick
        if mode == "down":
            return math.floor(units + 1e-12) * self.min_tick
        return math.floor(units + 0.5) * self.min_tick

    def _fee_offsets(self, context, entry, exit_price):
        costs = context.simulation.get("costs") or {}
        multiplier = float(costs.get("multiplier", self.contract_multiplier))
        if costs.get("fee_mode") == "rate":
            fee_points = (abs(entry) + abs(exit_price)) * float(costs.get("fee_rate", 0.0))
        elif costs:
            close_fee = max(float(costs.get("today_close_fee", 0.0)), float(costs.get("ordinary_close_fee", 0.0)))
            fee_points = (float(costs.get("open_fee", 0.0)) + close_fee) / multiplier
        else:
            fee_points = 0.0
        # Fees are reserved in the protective price in addition to the configured
        # adverse slippage buffer (currently one tick). Actual exchange fees are
        # still charged once on fills when realized net PnL is calculated.
        return fee_points + self.dynamic_fee_offset_points, fee_points

    @staticmethod
    def _close_intent(reason, quantity):
        return {
            "signal": {
                "category": "close",
                "direction": "long",
                "description": reason,
            },
            "intent": {
                "action": "close",
                "direction": "long",
                "quantity": quantity,
                "description": reason,
            },
        }

    def _protection_intent(self):
        reason = f"{self.active_stop_reason}；保护价 {self.active_stop:.4f}（下一根起盘中有效）"
        return {
            "signal": {"category": "protection_updated", "direction": "long", "description": reason,
                       "reference_price": self.active_stop},
            "intent": {"action": "protect", "direction": "long", "quantity": self.position_quantity,
                       "description": reason, "reference_price": self.active_stop},
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
                self.position_quantity = int(position["quantity"])
                risk_distance = self.pending_risk_distance or float(atr_value) * self.atr_stop_multiplier
                self.active_stop = self._snap(entry - risk_distance, "down")
                self.initial_risk_distance = max(entry - self.active_stop, self.min_tick)
                self.active_stop_reason = f"初始ATR{self.atr_period}×{self.atr_stop_multiplier:g}止损（约-1R）"
                self.max_favorable_r = 0.0
                self.ema_exit_armed = False
                self.entries_in_trend += 1
            self.was_in_position = True

            # The replay fills a pending limit from this completed bar's OHLC.
            # Do not use that same bar's high/low to manage a position that may
            # have entered later within it; doing so would introduce look-ahead.
            if new_position:
                return self._protection_intent()

            previous_stop = self.active_stop
            close = float(context.bar["close"])
            exit_ema = context.ema(self.ema_exit_period) if self.ema_exit_period > 0 else None
            if self.ema_exit_period > 0 and self.ema_exit_armed and exit_ema is not None and close < exit_ema:
                return self._close_intent(
                    f"达到{self.ema_exit_activation_r}R后收盘跌破EMA{self.ema_exit_period}；下一根有效K线开盘模拟平仓",
                    self.position_quantity,
                )

            high = float(context.bar["high"])
            trigger_r = max(0, math.floor((high - entry) / self.initial_risk_distance + 1e-12))
            while trigger_r > 0:
                trigger_price = entry + trigger_r * self.initial_risk_distance
                if high >= trigger_price:
                    break
                trigger_r -= 1
            if self.protection_variant == "two_r_n_minus_one":
                locked_r = trigger_r - 1 if trigger_r >= self.profit_lock_start_r else (0 if trigger_r >= 2 else None)
            elif self.protection_variant == "two_r_old_ladder":
                if trigger_r >= 11:
                    locked_r = trigger_r - 1
                elif trigger_r >= 4:
                    locked_r = trigger_r - 3
                elif trigger_r >= 2:
                    locked_r = 0
                else:
                    locked_r = None
            else:
                if trigger_r >= 11:
                    locked_r = trigger_r - 1
                elif trigger_r >= 4:
                    locked_r = trigger_r - 3
                elif trigger_r >= 1:
                    locked_r = 0
                else:
                    locked_r = None
            if locked_r is not None:
                unadjusted_stop = entry + locked_r * self.initial_risk_distance
                stop_offset, fee_points = self._fee_offsets(context, entry, unadjusted_stop)
                candidate = self._snap(
                    unadjusted_stop + stop_offset,
                    "up",
                )
                if candidate > self.active_stop:
                    self.active_stop = candidate
                    self.active_stop_reason = f"{trigger_r}R触发锁{locked_r}R（双向手续费{fee_points:.2f}点+滑点缓冲{self.dynamic_fee_offset_points:.2f}点=总偏移{stop_offset:.2f}点）"
            ema_activation_price = entry + self.ema_exit_activation_r * self.initial_risk_distance
            if self.ema_exit_period > 0 and high >= ema_activation_price:
                self.ema_exit_armed = True
            return self._protection_intent() if self.active_stop != previous_stop else None

        if self.was_in_position:
            self.was_in_position = False
            self.position_entry_price = None
            self.position_quantity = 0
            self.initial_risk_distance = None
            self.active_stop = None
            self.active_stop_reason = None
            self.max_favorable_r = 0.0
            self.ema_exit_armed = False

        if not trend_is_up or (self.max_entries_per_trend > 0 and self.entries_in_trend >= self.max_entries_per_trend):
            return None

        reference = self._snap(float(entry_reference), "nearest")
        if reference <= 0:
            return None
        self.pending_risk_distance = float(atr_value) * self.atr_stop_multiplier
        stop = self._snap(reference - self.pending_risk_distance, "down")
        stop_distance = max(reference - stop, self.min_tick)
        risk_per_lot = stop_distance * self.contract_multiplier
        quantity = math.floor(self.risk_budget / risk_per_lot)
        if quantity < 1:
            return {
                "signal": {
                    "category": "entry_skipped_risk_budget",
                    "direction": "long",
                    "description": (
                        f"风险预算不足一手：ATR{self.atr_period}={float(atr_value):.4f}，"
                        f"计划止损{stop_distance:.4f}点，每手计划止损风险{risk_per_lot:.2f}元，"
                        f"预算{self.risk_budget:.2f}元"
                    ),
                    "reference_price": reference,
                }
            }
        description = (
            f"EMA{self.ema_period}>EMA{self.trend_ema_period} 且收盘站上趋势EMA；"
            f"按EMA{self.entry_ema_period}={reference:.4f} 挂限价多单，风险预算{self.risk_budget:.0f}元，"
            f"计划{quantity}手，未成交则下一根按新EMA重挂"
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
                "quantity": quantity,
                "description": description,
                "reference_price": reference,
            },
        }
