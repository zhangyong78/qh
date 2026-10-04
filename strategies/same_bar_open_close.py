"""Reviewer fixture: two intents from the same completed bar."""

STRATEGY = {
    "name": "G3 same-bar intent fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 0,
    "parameters": {},
}


def create_strategy(parameters):
    return Strategy()


class Strategy:
    def on_bar(self, context):
        if context.bar_index != 0:
            return None
        return [
            {"signal": {"category": "open", "direction": "long", "description": "同根开多", "reference_price": context.bar["close"]}, "intent": {"action": "open", "direction": "long", "quantity": 1, "description": "同根开多"}},
            {"signal": {"category": "close", "direction": "long", "description": "同根平多", "reference_price": context.bar["close"]}, "intent": {"action": "close", "direction": "long", "quantity": 1, "description": "同根平多"}},
        ]
