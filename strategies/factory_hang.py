import time

STRATEGY = {
    "name": "G2 factory hang fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    time.sleep(30)
    return Strategy()

class Strategy:
    def on_bar(self, context):
        return None
