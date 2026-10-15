"""只读核对原包文件摘要；不导出正文。"""
import argparse
import hashlib
import json
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--game', required=True)
parser.add_argument('--baseline', required=True)
parser.add_argument('--output', required=True)
args = parser.parse_args()
root = Path(args.game).resolve()
baseline = json.loads(Path(args.baseline).read_text(encoding='utf-8'))
expected = {item['Id']['Path'].replace('\\', '/'): item['Id']['Hash'] for item in baseline['manifest']['Files']}
actual = {}
for path in sorted(root.rglob('*')):
    if path.is_file():
        with path.open('rb') as source:
            actual[path.relative_to(root).as_posix()] = hashlib.file_digest(source, 'sha256').hexdigest().upper()
changed = [name for name in expected.keys() & actual.keys() if expected[name] != actual[name]]
result = {'Files': len(actual), 'Changed': sorted(changed), 'Added': sorted(actual.keys() - expected.keys()),
          'Removed': sorted(expected.keys() - actual.keys()), 'Hashes': actual}
result['PackageId'] = hashlib.sha256(json.dumps(actual, sort_keys=True).encode()).hexdigest()
output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps({key: value for key, value in result.items() if key != 'Hashes'}))
raise SystemExit(1 if result['Changed'] or result['Added'] or result['Removed'] else 0)
