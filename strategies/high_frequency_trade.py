"""G3 performance fixture: one formal open intent on every completed bar."""

STRATEGY = {
    "name": "G3 high-frequency intent fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 0,
    "parameters": {},
}


def create_strategy(parameters):
    return Strategy()


class Strategy:
    def on_bar(self, context):
        return {
            "intent": {
                "action": "open",
                "direction": "long",
                "quantity": 1,
                "description": "高频意图扫描夹具",
            }
        }
