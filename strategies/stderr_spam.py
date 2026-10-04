import sys

STRATEGY = {
    "name": "G2 stderr volume fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        sys.stderr.write("stderr-fixture-" + ("x" * 4096) + "\n" * 2)
        sys.stderr.flush()
        return None
