"""G3 P06 trade fixture: hangs while constructing the strategy."""
import time

STRATEGY = {
    "name": "G3 trade initialization hang fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 0,
    "parameters": {},
}


def create_strategy(parameters):
    time.sleep(30)
    return Strategy()


class Strategy:
    def on_bar(self, context):
        return None
