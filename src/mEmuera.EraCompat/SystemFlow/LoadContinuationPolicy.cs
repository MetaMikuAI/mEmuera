using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.EraCompat.SystemFlow;

public static class LoadContinuationPolicy
{
    public static SystemFlowDefinition Create(ProgramIndex index)
    {
        var names = index.Functions.Where(f => f.Name == "EVENTLOAD").Select(f => f.Id).ToList();
        if (index.Find("SYSTEM_LOADEND") is { } end) names.Insert(0, end.Id);
        var continuation = new FlowContinuation(names.ToImmutableArray(), new("FLOW_UNSUPPORTED", "读档回调自然结束后的 SHOP 流程尚未实现"));
        // 旧脚本 LOADDATA 与界面读取均重建读档流程，不恢复指令地址。
        return new("SYSTEM_TITLE", continuation, continuation);
    }
}
