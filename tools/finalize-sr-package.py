"""Refresh the copied offline package manifest and verify every payload byte."""
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

root = Path(sys.argv[1]).resolve()
manifest = root / 'manifest.json'
if not root.is_dir() or not manifest.is_file() or root.name != 'sr2701-backtest-20261003':
    raise SystemExit('Unexpected package target')
data = json.loads(manifest.read_text(encoding='utf-8'))
data['package'] = 'qh-sr2701-15m-trial-win-x64'
data['created_at'] = datetime.now(timezone.utc).isoformat()
files = []
for path in sorted(root.rglob('*')):
    if not path.is_file() or path == manifest:
        continue
    relative = path.relative_to(root).as_posix()
    digest = hashlib.sha256(path.read_bytes()).hexdigest().upper()
    files.append({'path': relative, 'bytes': path.stat().st_size, 'sha256': digest})
data['files'] = files
manifest.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
failures = [(item['path']) for item in files if len((root / item['path']).read_bytes()) != item['bytes'] or hashlib.sha256((root / item['path']).read_bytes()).hexdigest().upper() != item['sha256']]
if failures:
    raise SystemExit(f'Package verification failed: {failures[:3]}')
print(json.dumps({'package': str(root), 'files': len(files), 'missing_or_hash_failures': len(failures)}, ensure_ascii=False))
