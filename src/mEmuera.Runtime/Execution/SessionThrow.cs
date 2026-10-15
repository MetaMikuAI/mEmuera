using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession
{
    private Diagnostic? pendingThrow;
    private IEnumerator<FlowAction>? beforeThrow;
    private int beforeThrowDepth;
    private string beforeThrowCallback = "";
    private void ClearPendingThrow()
    {
        pendingThrow = null; beforeThrow?.Dispose(); beforeThrow = null; beforeThrowCallback = "";
    }
    private void StartThrow(string text)
    {
        if (pendingThrow is not null || frames.Any(f => f.Descriptor.IsEvent && f.Descriptor.Name is "BEFORE_THROW" or "BEFORE_ERROR"))
        {
            document.Single(text); return;
        }
        pendingThrow = new("SCRIPT_THROW", text, currentSpan); beforeThrowDepth = frames.Count;
        beforeThrowCallback = game.Index.EventGroups.GetValueOrDefault("BEFORE_THROW", []).IsEmpty ? "BEFORE_ERROR" : "BEFORE_THROW";
        beforeThrow = game.Index.Options.DisableBeforeErrorThrow ? null : game.SystemFlow?.Events(beforeThrowCallback, this).GetEnumerator();
        AdvanceBeforeThrow();
    }
    private void AdvanceBeforeThrow()
    {
        if (beforeThrow is not null && beforeThrow.MoveNext())
        {
            if (beforeThrow.Current is not FlowCall call) throw new ScriptException("FLOW_CALLBACK", "错误回调包含无效流程操作");
            Push(game.Index.ByFunctionId[call.FunctionId], [], 0); return;
        }
        var diagnostic = pendingThrow!; ClearPendingThrow(); Fault(diagnostic);
    }
}
