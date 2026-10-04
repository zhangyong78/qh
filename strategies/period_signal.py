STRATEGY = {
    "name": "G2 period parameter fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 1,
    "warmup_bars_parameters": ["period"],
    "parameters": {
        "period": {"type": "int", "default": 2, "min": 1, "max": 20},
    },
}

def create_strategy(parameters):
    return Strategy(parameters)

class Strategy:
    def __init__(self, parameters):
        self.period = parameters["period"]

    def on_bar(self, context):
        value = context.sma(self.period)
        if value is None:
            return None
        return {"category": "period_ready", "direction": "long", "description": "period parameter ready", "reference_price": context.bar["close"]}
