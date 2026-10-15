"""从旧源码提取技术标识，只用于区分未支持与未知功能。"""
import re
from pathlib import Path

root = Path(__file__).resolve().parents[2]
statements = root / "Emuera/Emuera/Runtime/Script/Statements"
commands = sorted(set(re.findall(r"^\s*([A-Z][A-Z0-9_]+)\s*[,=]", (statements / "BuiltInFunctionCode.cs").read_text(encoding="utf-8-sig"), re.M)))
functions = sorted(set(re.findall(r'\["([A-Z][A-Z0-9_]*)"\]', (statements / "Function/Creator.cs").read_text(encoding="utf-8-sig"))))
target = root / "mEmuera/src/mEmuera.Compiler/Indexing/KnownIdentifiers.cs"
target.write_text('using System.Collections.Immutable;\n\nnamespace mEmuera.Compiler.Indexing;\n\n// 旧参考提交 3ecfd62；仅技术标识，不表示已实现。\npublic static class KnownIdentifiers\n{\n' + ''.join(f'    public static readonly ImmutableHashSet<string> {name} = "{" ".join(values)}".Split(\' \').ToImmutableHashSet();\n' for name, values in [("Commands", commands), ("Functions", functions)]) + '}\n', encoding="utf-8")
