import hashlib
import json
import zipfile
from pathlib import Path

archive = Path("artifacts/g6-offline-package-20261003-g4-final8.zip")
root = archive.stem + "/"
with zipfile.ZipFile(archive) as package:
    manifest = json.loads(package.read(root + "manifest.json"))
    entries = manifest["files"]
    names = {name.rstrip("/") for name in package.namelist() if name.startswith(root) and not name.endswith("/")}
    declared = {root + entry["path"].replace("\\", "/") for entry in entries}
    failures = []
    for entry in entries:
        path = root + entry["path"].replace("\\", "/")
        if path not in names:
            failures.append((entry["path"], "missing"))
            continue
        with package.open(path) as source:
            digest = hashlib.sha256()
            size = 0
            while True:
                chunk = source.read(1024 * 1024)
                if not chunk:
                    break
                size += len(chunk)
                digest.update(chunk)
        if size != entry["bytes"] or digest.hexdigest().lower() != entry["sha256"].lower():
            failures.append((entry["path"], "hash-or-size"))
    undeclared = sorted(names - declared - {root + "manifest.json"})
    print(json.dumps({
        "archive": str(archive),
        "declared_files": len(entries),
        "zip_files_under_root": len(names),
        "manifest_in_declared": "manifest.json" in {entry["path"].replace("\\", "/") for entry in entries},
        "failures": failures,
        "undeclared_files": undeclared,
        "g4_data_present": any(path.startswith(root + "g4-data/") for path in names),
        "all_passed": not failures and not undeclared and "manifest.json" not in {entry["path"].replace("\\", "/") for entry in entries},
    }, ensure_ascii=False, indent=2))
    raise SystemExit(0 if not failures and not undeclared else 1)
