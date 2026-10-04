import time

STRATEGY = {
    "name": "G2 timeout fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        time.sleep(5)
        return None
