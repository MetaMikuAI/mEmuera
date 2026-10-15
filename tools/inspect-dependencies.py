"""输出原包依赖的技术元数据，不输出脚本正文或字符串参数值。"""
import argparse
import json
import re
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--game", required=True)
parser.add_argument("--scan", required=True)
parser.add_argument("--output", required=True)
parser.add_argument("--metadata")
args = parser.parse_args()
root = Path(args.game)
scan = json.loads(Path(args.scan).read_text(encoding="utf-8"))
calls = []
external = []
bindings = {}
if args.metadata:
    for library in json.loads(Path(args.metadata).read_text(encoding='utf-8'))['libraries']:
        for binding in library['bindings']:
            bindings[binding['method']] = {'path': library['path'], 'type': binding['type'], 'evidence': binding['evidence']}
def split_arguments(text):
    parts, start, depth, quoted, escaped = [], 0, 0, False, False
    for i, char in enumerate(text):
        if escaped:
            escaped = False
            continue
        if quoted and char == '\\':
            escaped = True
            continue
        if char == '"':
            quoted = not quoted
        elif not quoted:
            depth += (char == '(') - (char == ')')
            if char == ',' and depth == 0:
                parts.append(text[start:i].strip())
                start = i + 1
    return parts + [text[start:].strip()]
for file in scan["manifest"]["Files"]:
    if file["Category"] not in ("ERB", "ERH"):
        continue
    path = file["Id"]["Path"]
    for number, line in enumerate((root / path).read_text(encoding="utf-8-sig").splitlines(), 1):
        head = re.match(r'\s*(\w+)', line)
        if head and (head[1].upper().startswith('SQL') or head[1].upper() in {'LOADTEXT', 'SAVETEXT', 'FIND_FILES', 'FILEEXISTS', 'DELETEFILE', 'COPYFILE', 'MOVEFILE', 'GLOAD', 'GSAVE', 'PLAYBGM', 'PLAYSOUND', 'XML_LOAD'}):
            external.append({'path': path, 'line': number, 'command': head[1].upper(), 'target': 'unknown', 'note': '参数可能包含配置或私密路径，不输出字符串；动态目标不静态猜测'})
        match = re.match(r'\s*CALLSHARP\s+(\w+)\s*\((.*)\)', line, re.I)
        if match:
            method = match[1]
            parameters = split_arguments(match[2])
            writable = [bool(re.fullmatch(r'[\w]+(?::[^,]+)*', parameter)) for parameter in parameters]
            calls.append({"path": path, "line": number, "method": method, "argumentCount": len(parameters), "writableArgumentCandidates": writable,
                          "reachability": "unknown", "optional": "unknown", "libraryBinding": bindings.get(method, "unknown"),
                          "referenceWriteBack": "旧 CALLSHARP 对 VariableTerm 参数逐项写回；候选按语法识别，宏展开与插件行为未执行"})
libraries = [{"path": f["Id"]["Path"], "sha256": f["Id"]["Hash"]} for f in scan["manifest"]["Files"] if f["Category"] == "DLL" and "plugin" in f["Id"]["Path"].lower()]
requirements = [r for r in scan["requirements"] if r["Name"].startswith("SQL") or r["Name"] in ("CALLSHARP", "GETMETH", "GETMETHS", "GGETCOLOR", "SPRITEWIDTH", "SPRITEHEIGHT")]
report = {"calls": calls, "libraries": libraries, "requirements": requirements, "externalFileOperations": external,
          "limitations": "DLL 仅读取元数据；方法名 getter 是静态绑定证据，不证明动态注册及可达性。外部文件目标保留 unknown；SQL 指令尚未映射"}
Path(args.output).write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({"callSites": len(calls), "pluginLibraries": len(libraries)}, ensure_ascii=False))
