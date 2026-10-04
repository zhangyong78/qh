STRATEGY = {
    "name": "G2 preheat accounting fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 3,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        return {"category": "probe", "direction": "long", "description": "preheat/formal accounting", "reference_price": context.bar["close"]}
