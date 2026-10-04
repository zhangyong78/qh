"""Small deterministic G3 offline trade fixtures.

The engine owns execution timing and accounting. This strategy only emits
completed-bar intents so the fixtures can be replayed without future data.
"""

STRATEGY = {
    "name": "G3 sequence trade fixture",
    "version": "1.0.0",
    "kind": "trade",
    "warmup_bars": 0,
    "parameters": {
        "scenario": {
            "type": "str",
            "default": "f01",
            "choices": ["f01", "short", "reverse", "end", "zero", "end_open"],
        }
    },
}


def create_strategy(parameters):
    return Strategy(parameters["scenario"])


class Strategy:
    def __init__(self, scenario):
        self.scenario = scenario

    def on_bar(self, context):
        action = None
        if self.scenario == "f01":
            action = {0: ("open", "long", "开多"), 1: ("close", "long", "平多")}.get(context.bar_index)
        elif self.scenario == "short":
            action = {0: ("open", "short", "开空"), 1: ("close", "short", "平空")}.get(context.bar_index)
        elif self.scenario == "reverse":
            action = {0: ("open", "long", "开多"), 1: ("reverse", "short", "反向开空"), 2: ("close", "short", "平空")}.get(context.bar_index)
        elif self.scenario == "end":
            action = {2: ("open", "long", "末根开多")}.get(context.bar_index)
        elif self.scenario == "end_open":
            action = {0: ("open", "long", "开多")}.get(context.bar_index)
        if action is None:
            return None
        verb, direction, description = action
        return {
            "signal": {
                "category": verb,
                "direction": direction,
                "description": description,
                "reference_price": context.bar["close"],
            },
            "intent": {
                "action": verb,
                "direction": direction,
                "quantity": 1,
                "description": description,
                "reference_price": context.bar["close"],
            },
        }
