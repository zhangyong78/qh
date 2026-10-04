"""G2 first trusted local signal strategy: SMA fast/slow cross."""

STRATEGY = {
    "name": "SMA/EMA 均线交叉信号",
    "version": "1.0.0",
    "kind": "signal",
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
        signal = None
        if fast is not None and slow is not None and self.previous_fast is not None and self.previous_slow is not None:
            if self.previous_fast <= self.previous_slow and fast > slow:
                signal = {"category": "cross_up", "direction": "long", "description": "快线上穿慢线", "reference_price": context.bar["close"]}
            elif self.previous_fast >= self.previous_slow and fast < slow:
                signal = {"category": "cross_down", "direction": "short", "description": "快线下穿慢线", "reference_price": context.bar["close"]}
        if fast is not None and slow is not None:
            self.previous_fast = fast
            self.previous_slow = slow
        return signal
