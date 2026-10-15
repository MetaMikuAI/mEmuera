using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

public sealed partial class GameSession
{
    private bool ContinueScriptSaveFailure(Diagnostic error)
    {
        if (savedInput is not null || service is null) return false;
        if (service.Payload is LoadPayload { Scope: SaveScope.Global }) globals["RESULT"].Values[0] = new(0L);
        else if (service.Payload is SavePayload { Scope: SaveScope.Game })
        {
            diagnostics.Add(error with { Severity = Severity.Warning, Span = error.Span ?? currentSpan });
            document.Text("存档保存失败：" + error.Message, newline: true);
        }
        else return false;
        var kind = service.Kind; service = null; state = StopReason.Yielded;
        Log(TraceKind.ServiceCompleted, kind + ":failed"); return true;
    }
}
