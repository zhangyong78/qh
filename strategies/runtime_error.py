STRATEGY = {
    "name": "G2 runtime error fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        raise RuntimeError("fixture runtime error")
