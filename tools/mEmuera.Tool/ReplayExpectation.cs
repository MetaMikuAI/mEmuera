using mEmuera.Contracts;

internal sealed record ReplayValue(string Variable, string Value, int[]? Indices = null, int? Character = null);
internal sealed record ReplayExpectation(string State, string Mode, int? CharacterCount = null, ReplayValue[]? Values = null, string[]? Events = null,
    Dictionary<string, int>? EventCounts = null, string[]? EventOrder = null, string[]? ButtonValues = null)
{
    public IEnumerable<StateSelection> Selection => (Values ?? []).Select(v => new StateSelection(v.Variable, v.Character));
    public void Check(SessionSnapshot state)
    {
        void Need(bool condition, string detail)
        { if (!condition) throw new ScriptException("REPLAY_ASSERT", "终态断言失败: " + detail); }
        Need(state.State.ToString() == State, "State"); Need(state.Mode.ToString() == Mode, "Mode");
        if (CharacterCount is { } count) Need(state.Characters.Length == count, "CharacterCount");
        foreach (var expected in Values ?? [])
        {
            var shape = state.Shapes.GetValueOrDefault(expected.Variable);
            Need(!shape.IsDefaultOrEmpty, "变量不存在: " + expected.Variable);
            int[] indices = expected.Indices ?? new int[shape.Length];
            Need(indices.Length == shape.Length, "维数: " + expected.Variable);
            int offset = 0;
            for (int i = 0; i < shape.Length; i++)
            { Need(indices[i] >= 0 && indices[i] < shape[i], "下标: " + expected.Variable); offset = checked(offset * shape[i] + indices[i]); }
            var values = expected.Character is { } character
                ? state.Characters.ElementAtOrDefault(character)?.Variables.FirstOrDefault(v => v.Name == expected.Variable)?.Values ?? []
                : state.Variables.GetValueOrDefault(expected.Variable);
            Need(!values.IsDefault && offset < values.Length, "存储: " + expected.Variable);
            Need(values[offset].ToString() == expected.Value, expected.Variable);
        }
        foreach (string required in Events ?? []) Need(state.Events.Any(e => e.Detail == required), "事件: " + required);
        foreach (var (detail, expectedCount) in EventCounts ?? []) Need(state.Events.Count(e => e.Detail == detail) == expectedCount, "事件次数: " + detail);
        int next = 0;
        foreach (var item in state.Events)
            if (next < (EventOrder?.Length ?? 0) && item.Detail == EventOrder![next]) next++;
        Need(next == (EventOrder?.Length ?? 0), "事件顺序");
        foreach (var value in ButtonValues ?? []) Need(state.Input?.Buttons.Values.Any(v => v.ToString() == value) == true, "按钮: " + value);
    }
}
