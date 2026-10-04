"""G3 P06 trade fixture: exceeds the per-bar strategy deadline."""
import time

STRATEGY = {
    "name": "G3 trade on_bar timeout fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 0,
    "parameters": {},
}


def create_strategy(parameters):
    return Strategy()


class Strategy:
    def on_bar(self, context):
        time.sleep(5)
        return None
