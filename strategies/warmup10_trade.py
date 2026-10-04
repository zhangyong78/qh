STRATEGY = {
    "name": "G3 warmup gate fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 10,
    "parameters": {},
}


def create_strategy(parameters):
    return Strategy()


class Strategy:
    def on_bar(self, context):
        return None
