using mEmuera.Contracts;
using System.Collections.Immutable;

namespace mEmuera.EraCompat.SystemFlow;

public sealed class EraSystemFlow(ProgramIndex index) : ISystemFlowPolicy
{
    public bool CanSaveLoad(SystemMode mode) => mode is not (SystemMode.Train or SystemMode.Load);
    public IEnumerable<FlowAction> Events(string name, IFlowState state, bool required = false, bool allowDoTrain = false)
    {
        var events = index.EventGroups.GetValueOrDefault(name, []);
        if (events.IsEmpty && required) throw new ScriptException("FLOW_CALLBACK", "缺少系统事件: " + name);
        foreach (string group in new[] { "#ONLY", "#PRI", "", "#LATER" })
        {
            foreach (var function in events.Where(f => group.Length == 0 ? !f.Attributes.Contains("#PRI") && !f.Attributes.Contains("#LATER") : f.Attributes.Contains(group)))
            {
                yield return new FlowCall(function.Id, allowDoTrain);
                if (function.Attributes.Contains("#ONLY")) yield break;
                if (function.Attributes.Contains("#SINGLE") && state.Get("RESULT").AsInt() == 1) break;
            }
        }
    }
    private FlowCall Call(string name, bool allowDoTrain = false) => new((index.Find(name) ?? throw new ScriptException("FLOW_CALLBACK", "缺少系统回调: " + name)).Id, allowDoTrain);
    public IEnumerable<FlowAction> DoTrain(long command, IFlowState state)
    {
        if (command < 0 || command >= index.VariablesByName["TRAINNAME"].Length)
            throw new ScriptException("DOTRAIN_RANGE", "DOTRAIN 指令编号越界");
        return Train(state, command);
    }
    public IEnumerable<FlowAction> Start(SystemMode mode, IFlowState state)
    {
        switch (mode)
        {
            case SystemMode.Title:
                yield return Call("SYSTEM_TITLE"); break;
            case SystemMode.First:
                foreach (var action in Events("EVENTFIRST", state, true)) yield return action;
                break;
            case SystemMode.Load:
                if (index.Find("SYSTEM_LOADEND") is { } callback) yield return new FlowCall(callback.Id);
                foreach (var action in Events("EVENTLOAD", state)) yield return action;
                yield return new FlowSwitch(SystemMode.Shop); break;
            case SystemMode.AfterTrain:
                foreach (var action in Events("EVENTEND", state, true)) yield return action;
                break;
            case SystemMode.Train:
                foreach (var action in Train(state)) yield return action;
                break;
            case SystemMode.TurnEnd:
                foreach (var action in Events("EVENTTURNEND", state, true)) yield return action;
                break;
            case SystemMode.Ablup:
                while (true)
                {
                    yield return Call("SHOW_JUEL");
                    yield return Call("SHOW_ABLUP_SELECT");
                    while (true)
                    {
                        yield return new FlowInput(InputKind.Integer);
                        long selected = state.Get("RESULT").AsInt();
                        if (selected is >= 0 and < 100)
                        {
                            if (index.Find("ABLUP" + selected) is not { } ability)
                            { yield return new FlowTemporary("无效的选择"); continue; }
                            yield return new FlowCall(ability.Id);
                        }
                        else yield return Call("USERABLUP");
                        if (!state.LastLineTemporary) break;
                    }
                }
            case SystemMode.Shop:
                foreach (var action in Events("EVENTSHOP", state)) yield return action;
                while (true)
                {
                    yield return Call("SHOW_SHOP");
                    yield return new FlowInput(InputKind.Integer);
                    long selected = state.Get("RESULT").AsInt();
                    if (selected is >= 0 and < 100)
                    {
                        if (state.Get("ITEMSALES", (int)selected).AsInt() == 0) continue;
                        long price = state.Get("ITEMPRICE", (int)selected).AsInt();
                        if (state.Get("MONEY").AsInt() < price) continue;
                        state.Set("MONEY", new(state.Get("MONEY").AsInt() - price));
                        state.Set("ITEM", new(state.Get("ITEM", (int)selected).AsInt() + 1), (int)selected);
                        state.Set("BOUGHT", new(selected));
                        foreach (var action in Events("EVENTBUY", state)) yield return action;
                    }
                    else yield return Call("USERSHOP");
                }
            default: throw new ScriptException("FLOW_UNSUPPORTED", "系统流程尚未实现: " + mode);
        }
    }
    private IEnumerable<FlowAction> Train(IFlowState state, long? direct = null)
    {
        if (direct is null)
        {
            state.Set("ASSIPLAY", new(0L)); state.Set("PREVCOM", new(-1L)); state.Set("NEXTCOM", new(-1L));
            foreach (string name in "TFLAG TSTR GOTJUEL TEQUIP EX PALAM SOURCE TCVAR".Split(' ')) yield return new FlowFill(name);
            yield return new FlowFill("STAIN", index.Options.DefaultStain.Select(n => new EraValue(n)).ToImmutableArray());
            foreach (var action in Events("EVENTTRAIN", state, allowDoTrain: true)) yield return action;
        }
        var trainNames = index.VariablesByName.GetValueOrDefault("TRAINNAME")
            ?? throw new ScriptException("FLOW_VARIABLE", "缺少 TRAINNAME");
        while (true)
        {
            long selected = direct ?? state.Get("NEXTCOM").AsInt();
            if (direct is not null)
            {
                foreach (string name in "UP DOWN LOSEBASE DOWNBASE CUP CDOWN".Split(' ')) yield return new FlowFill(name);
                direct = null;
            }
            else if (selected >= 0) state.Set("NEXTCOM", new(0L));
            else
            {
                yield return Call("SHOW_STATUS", true);
                var available = new Dictionary<long, int>();
                int menuIndex = -1;
                for (int code = 0; code < trainNames.Initial.Length; code++)
                {
                    string name = trainNames.Initial[code].AsString();
                    if (name.Length == 0) continue;
                    menuIndex++;
                    if (index.Find("COM_ABLE" + code) is { } able) yield return new FlowCall(able.Id);
                    else state.Set("RESULT", new(index.Options.DefaultComAble));
                    if (state.Get("RESULT").AsInt() == 0) continue;
                    available.Add(menuIndex, code);
                    yield return new FlowButton($"{name}[{menuIndex,3}]", new((long)menuIndex));
                }
                yield return Call("SHOW_USERCOM", true);
                foreach (string name in "UP DOWN LOSEBASE DOWNBASE CUP CDOWN".Split(' ')) yield return new FlowFill(name);
                yield return new FlowInput(InputKind.Integer);
                if (!available.TryGetValue(state.Get("RESULT").AsInt(), out int codeSelected))
                { yield return Call("USERCOM", true); continue; }
                selected = codeSelected;
            }
            state.Set("SELECTCOM", new(selected));
            yield return new FlowFill("NOWEX");
            foreach (var action in Events("EVENTCOM", state)) yield return action;
            yield return Call("COM" + state.Get("SELECTCOM").AsInt());
            if (state.Get("RESULT").AsInt() == 0) continue;
            yield return Call("SOURCE_CHECK");
            yield return new FlowFill("SOURCE");
            long inputRequests = state.InputRequests;
            foreach (var action in Events("EVENTCOMEND", state, allowDoTrain: true)) yield return action;
            if (state.InputRequests == inputRequests) yield return new FlowInput(InputKind.Wait);
        }
    }
}
