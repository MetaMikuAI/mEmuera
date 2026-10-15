using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.Compiler;

internal static class BytecodeValidator
{
    public static ImmutableArray<Diagnostic> Validate(ImmutableArray<Instruction> code)
    {
        var depths = new Dictionary<int, int>(); var queue = new Queue<(int Pc, int Depth)>(); queue.Enqueue((0, 0));
        while (queue.TryDequeue(out var item))
        {
            if (item.Pc < 0 || item.Pc >= code.Length) return [new("BYTECODE_JUMP", "跳转越出函数")];
            var instruction = code[item.Pc];
            if (depths.TryGetValue(item.Pc, out int existing))
            { if (existing != item.Depth) return [new("BYTECODE_STACK", "分支汇合时表达式栈不一致", instruction.Span)]; continue; }
            depths.Add(item.Pc, item.Depth);
            int required = instruction.Op switch
            {
                Op.Store or Op.Binary or Op.Button or Op.Save => 2,
                Op.Input when instruction.Text == "TINPUT" => 2,
                Op.Call or Op.Return or Op.Builtin => instruction.Number,
                Op.DynamicCall or Op.StoreArray => instruction.Number + 1,
                Op.Reference or Op.ReadVariable or Op.ReadCharacter or Op.ReadLocal => Math.Max(1, instruction.Number),
                Op.Load or Op.Duplicate or Op.Pop or Op.Unary or Op.JumpFalse or Op.JumpTrue or Op.Emit or Op.ClearLine or Op.LoadSave or Op.AdvanceReference or Op.StringCall or Op.Throw => 1,
                Op.Color when instruction.Text != "RESET" => 1, _ => 0
            };
            int produced = instruction.Op switch
            {
                Op.Constant or Op.Reference or Op.ReadVariable or Op.ReadCharacter or Op.ReadLocal or Op.ReadLocalConstant or Op.Load or Op.Unary or Op.Binary or Op.LineCount or Op.AdvanceReference or Op.StringCall => 1,
                Op.Duplicate => 2, Op.Call or Op.DynamicCall or Op.Builtin when instruction.Value.Integer == 1 => 1, _ => 0
            };
            if (item.Depth < required) return [new("BYTECODE_STACK", "指令缺少操作数", instruction.Span)];
            int next = item.Depth - required + produced;
            if (instruction.Op is Op.Return or Op.Quit or Op.BeginTitle || instruction.Op == Op.Call && instruction.Value.Integer == 2) continue;
            if (instruction.Op is Op.Jump or Op.JumpFalse or Op.JumpTrue) queue.Enqueue((instruction.Number, next));
            if (instruction.Op != Op.Jump) queue.Enqueue((item.Pc + 1, next));
        }
        return [];
    }
}
