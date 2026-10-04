import time
time.sleep(30)

STRATEGY = {
    "name": "G2 load hang fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        return None
