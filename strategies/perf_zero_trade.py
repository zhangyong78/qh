"""Fixed no-intent trade strategy used only for G3 performance measurement."""

STRATEGY = {"name": "G3 performance zero trade", "version": "1.0.0", "kind": "trade", "warmup_bars": 0, "parameters": {}}


def create_strategy(parameters):
    return Strategy()


class Strategy:
    def on_bar(self, context):
        return None
