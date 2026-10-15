"""从索引生成引擎名称、用法和词法次数清单。"""
import argparse
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument('--scan', type=Path, required=True)
parser.add_argument('--check', type=Path)
parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[2] / 'docs/rewrite/原包指令清单.md')
args = parser.parse_args()
scan = json.loads(args.scan.read_text(encoding='utf-8-sig'))
check = json.loads(args.check.read_text(encoding='utf-8-sig')) if args.check else None
root = Path(__file__).resolve().parents[1]
known = (root / 'src/mEmuera.Compiler/Indexing/KnownIdentifiers.cs').read_text(encoding='utf-8-sig')
commands = set(re.search(r'Commands = "([^"]+)"', known)[1].split())
functions = set(re.search(r'Functions = "([^"]+)"', known)[1].split())
script = set(scan['scriptFunctions'])
specials = set(re.findall(r'case "([A-Z_]+)":', (root / 'src/mEmuera.Compiler/FunctionCompiler.cs').read_text(encoding='utf-8-sig')))
rows = []
for item in scan['requirements']:
    name, category = item['Name'], item['Category']
    if category not in ('command', 'expression-function') or name == 'ASSIGN':
        continue
    if category == 'expression-function' and name in script and name not in functions:
        continue
    if name not in commands | functions | specials and not item['Supported']:
        continue
    rows.append((name, '指令' if category == 'command' else '表达式函数', item['Count']))

lines = ['# 原包指令与函数清单', '',
    f"加载配置：`{scan['version']}`；{scan['coverage']['Files']} 个脚本文件，{scan['functions']} 个函数定义。",
    f"- 清单共 {len(set(r[0] for r in rows))} 个引擎名称；按指令／表达式两种用法展开 {len(rows)} 项。",
    '- 范围：当前预处理配置下全部已加载脚本，包括未进入的函数。被 SKIPSTART、条件预处理排除的内容不计入；动态字符串生成的调用与格式表达式仍须单独核对。',
    '- 次数是词法出现次数，不是执行次数或兼容结论；脚本自定义函数不计入引擎名称。',
    '', '| 名称 | 用法 | 次数 |', '| --- | --- | ---: |']
for name, kind, count in sorted(rows):
    lines.append(f'| `{name}` | {kind} | {count} |')
lines.append('')
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text('\n'.join(lines), encoding='utf-8')
summary = f'names={len(set(r[0] for r in rows))}'
if check is not None:
    summary += f' diagnostics={len(check["diagnostics"])}'
print(summary)
