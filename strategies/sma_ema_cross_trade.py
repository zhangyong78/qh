"""G3 agreed single-contract SMA/EMA cross trading example.

The strategy emits an open or reverse intent only after a completed-bar cross.
Execution, position state and accounting remain owned by the C# engine.
"""

STRATEGY = {
    "name": "SMA/EMA 均线交叉交易示例",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 3,
    "warmup_bars_parameters": ["fast_period", "slow_period"],
    "parameters": {
        "fast_period": {"type": "int", "default": 20, "min": 1, "max": 100},
        "slow_period": {"type": "int", "default": 60, "min": 2, "max": 200},
        "average": {"type": "str", "default": "sma", "choices": ["sma", "ema"]},
    },
}


def create_strategy(parameters):
    return Strategy(parameters)


class Strategy:
    def __init__(self, parameters):
        self.fast_period = int(parameters["fast_period"])
        self.slow_period = int(parameters["slow_period"])
        self.average = str(parameters["average"])
        if self.fast_period >= self.slow_period:
            raise ValueError("fast_period 必须小于 slow_period")
        self.previous_fast = None
        self.previous_slow = None

    def on_bar(self, context):
        average = context.sma if self.average == "sma" else context.ema
        fast = average(self.fast_period)
        slow = average(self.slow_period)
        action = None
        if fast is not None and slow is not None and self.previous_fast is not None and self.previous_slow is not None:
            position = (context.simulation.get("position") or {}).get("direction")
            if self.previous_fast <= self.previous_slow and fast > slow and position != "long":
                action = ("open" if position is None else "reverse", "long", "均线上穿开多/反向开多")
            elif self.previous_fast >= self.previous_slow and fast < slow and position != "short":
                action = ("open" if position is None else "reverse", "short", "均线下穿开空/反向开空")
        if fast is not None and slow is not None:
            self.previous_fast = fast
            self.previous_slow = slow
        if action is None:
            return None
        verb, direction, description = action
        return {
            "signal": {"category": verb, "direction": direction, "description": description, "reference_price": context.bar["close"]},
            "intent": {"action": verb, "direction": direction, "quantity": 1, "description": description, "reference_price": context.bar["close"]},
        }
