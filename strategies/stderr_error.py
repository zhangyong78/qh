import sys

STRATEGY = {
    "name": "G2 stderr runtime error fixture",
    "version": "1.0.0",
    "kind": "signal",
    "warmup_bars": 0,
    "parameters": {},
}

def create_strategy(parameters):
    return Strategy()

class Strategy:
    def on_bar(self, context):
        sys.stderr.write("STDERR_MARKER_G2_RUNTIME\n")
        sys.stderr.flush()
        raise RuntimeError("fixture traceback error")
