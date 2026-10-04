"""G1 managed worker protocol. It intentionally does not load or execute user strategies."""
from __future__ import annotations

import json
import sys
import threading
import time


def send(message: dict) -> None:
    try:
        sys.stdout.write(json.dumps(message, ensure_ascii=False) + "\n")
        sys.stdout.flush()
    except (BrokenPipeError, OSError):
        raise SystemExit(0)


def main() -> int:
    send({"type": "ready", "protocol": 1, "capabilities": ["heartbeat", "cancel", "stop"]})
    command_queue: list[str] = []
    lock = threading.Lock()
    stdin_closed = threading.Event()

    def read_commands() -> None:
        for line in sys.stdin:
            try:
                payload = json.loads(line)
                with lock:
                    command_queue.append(str(payload.get("command", "")))
            except json.JSONDecodeError:
                send({"type": "error", "message": "invalid command json"})
        stdin_closed.set()

    thread = threading.Thread(target=read_commands, daemon=True)
    thread.start()
    running = False
    last_heartbeat = 0.0
    while True:
        if stdin_closed.is_set():
            send({"type": "stopped", "reason": "stdin_closed"})
            return 0
        with lock:
            command = command_queue.pop(0) if command_queue else None
        if command == "run":
            running = True
            send({"type": "started"})
        elif command == "cancel":
            if running:
                running = False
                send({"type": "cancelled"})
            else:
                send({"type": "cancelled"})
        elif command == "stop":
            send({"type": "stopped"})
            return 0
        if running and time.monotonic() - last_heartbeat >= 0.25:
            send({"type": "heartbeat"})
            last_heartbeat = time.monotonic()
        time.sleep(0.05)


if __name__ == "__main__":
    raise SystemExit(main())
