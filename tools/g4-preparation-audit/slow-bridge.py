import json
import sys
import time

for line in sys.stdin:
    request = json.loads(line)
    if request.get("op") == "runtime":
        time.sleep(12)
        print(json.dumps({"ok": True, "target_version": "surrogate", "can_data_downloader_module": True, "evidence": "slow dependency preparation; no account login"}), flush=True)
    elif request.get("op") == "probe":
        print(json.dumps({"ok": False, "error_kind": "Authentication", "message": "surrogate network handshake denied"}), flush=True)
    else:
        print(json.dumps({"ok": False, "error_kind": "Protocol", "message": "unsupported"}), flush=True)
