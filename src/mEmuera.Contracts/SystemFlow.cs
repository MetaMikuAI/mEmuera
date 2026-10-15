namespace mEmuera.Contracts;

public enum SystemMode { Title, First, Shop, Train, AfterTrain, Load, Ablup, TurnEnd }
public abstract record FlowAction;
public sealed record FlowCall(string FunctionId, bool AllowDoTrain = false) : FlowAction;
public sealed record FlowInput(InputKind Kind) : FlowAction;
public sealed record FlowSwitch(SystemMode Mode) : FlowAction;
public sealed record FlowFill(string Variable, System.Collections.Immutable.ImmutableArray<EraValue> Initial = default) : FlowAction;
public sealed record FlowButton(string Text, EraValue Value) : FlowAction;
public sealed record FlowTemporary(string Text) : FlowAction;
public interface IFlowState
{
    EraValue Get(string name, int index = 0);
    void Set(string name, EraValue value, int index = 0);
    long InputRequests { get; }
    bool LastLineTemporary => false;
}
public interface ISystemFlowPolicy
{
    IEnumerable<FlowAction> Start(SystemMode mode, IFlowState state);
    IEnumerable<FlowAction> Events(string name, IFlowState state, bool required = false, bool allowDoTrain = false) => [];
    IEnumerable<FlowAction> DoTrain(long command, IFlowState state) => throw new ScriptException("FLOW_UNSUPPORTED", "当前流程不支持 DOTRAIN");
    bool CanSaveLoad(SystemMode mode) => true;
}
