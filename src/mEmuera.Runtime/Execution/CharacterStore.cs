using System.Collections.Immutable;
using mEmuera.Contracts;

namespace mEmuera.Runtime.Execution;

public sealed class CharacterStore(IEnumerable<VariableDescriptor> variables, IReadOnlyDictionary<long, CharacterTemplate> templates)
{
    private sealed record Instance(long Id, long Template, Dictionary<string, EraValue[]> Values);
    private readonly Dictionary<string, VariableDescriptor> specifications = variables.Where(v => v.Scope == VariableScope.Character).ToDictionary(v => v.Name);
    private readonly List<Instance> order = [];
    private readonly Dictionary<long, Instance> instances = [];
    private long nextId;
    public int Count => order.Count;
    public long IdAt(long index) => At(index).Id;
    public int IndexOf(long id) => order.FindIndex(c => c.Id == id);
    private Instance At(long index) => index >= 0 && index < order.Count ? order[(int)index] : throw new ScriptException("CHARACTER_BOUNDS", "角色下标越界");
    public EraValue[] Values(long id, string name)
    {
        if (!instances.TryGetValue(id, out var instance)) throw new ScriptException("STALE_REFERENCE", "引用的角色已删除");
        return instance.Values.TryGetValue(name, out var values) ? values : throw new ScriptException("VARIABLE", "角色变量不存在: " + name);
    }
    public long Add(long templateNumber)
    {
        if (!templates.TryGetValue(templateNumber, out var template)) throw new ScriptException("CHARACTER_TEMPLATE", "角色模板不存在: " + templateNumber);
        var values = specifications.ToDictionary(p => p.Key, p => Enumerable.Repeat(EraValue.Zero(p.Value.Kind), p.Value.Length).ToArray());
        foreach (var (name, initial) in template.Values)
        {
            if (!specifications.TryGetValue(name, out var spec) || initial.Length != spec.Length || initial.Any(v => v.Kind != spec.Kind))
                throw new ScriptException("CHARACTER_TEMPLATE", "角色模板变量规格不匹配: " + name);
            initial.CopyTo(values[name]);
        }
        var instance = new Instance(++nextId, templateNumber, values); order.Add(instance); instances.Add(instance.Id, instance); return instance.Id;
    }
    public void Delete(long index) { var instance = At(index); order.Remove(instance); instances.Remove(instance.Id); }
    public void Copy(long from, long to)
    {
        var source = At(from); var target = At(to);
        foreach (var (name, values) in source.Values) values.CopyTo(target.Values[name], 0);
    }
    public void AddCopy(long from)
    {
        var source = At(from);
        var copy = new Instance(++nextId, source.Template, source.Values.ToDictionary(p => p.Key, p => p.Value.ToArray()));
        instances.Add(copy.Id, copy); order.Add(copy);
    }
    public void Swap(long first, long second)
    { var a = At(first); var b = At(second); order[(int)first] = b; order[(int)second] = a; }
    public void Clear() { order.Clear(); instances.Clear(); }
    public ImmutableArray<CharacterSnapshot> SnapshotIdentity() => order.Select(c => new CharacterSnapshot(c.Id, c.Template, [])).ToImmutableArray();
    public ImmutableArray<CharacterSnapshot> Snapshot() => order.Select(c => new CharacterSnapshot(c.Id, c.Template,
        c.Values.Where(p => specifications[p.Key].Saved).Select(p => new SavedVariable(p.Key, specifications[p.Key].Kind, p.Value.Length, p.Value.ToImmutableArray()) { Shape = specifications[p.Key].Shape }).ToImmutableArray())).ToImmutableArray();
    public CharacterStore Candidate(ImmutableArray<CharacterSnapshot> snapshots)
    {
        if (snapshots.IsDefault || snapshots.Length > 10000) throw new ScriptException("SAVE_CHARACTERS", "角色集合无效");
        var candidate = new CharacterStore(specifications.Values, templates);
        long cellsPerCharacter = specifications.Values.Sum(v => (long)v.Length);
        if (cellsPerCharacter * snapshots.Length > 16_000_000) throw new ScriptException("SAVE_LIMIT", "角色存档总量超限");
        foreach (var saved in snapshots)
        {
            if (saved is null || saved.Id <= 0 || candidate.instances.ContainsKey(saved.Id) || saved.Variables.IsDefault
                || saved.Variables.Length != specifications.Values.Count(v => v.Saved)) throw new ScriptException("SAVE_CHARACTERS", "角色编号或变量集合无效");
            var values = specifications.ToDictionary(p => p.Key, p => Enumerable.Repeat(EraValue.Zero(p.Value.Kind), p.Value.Length).ToArray());
            var names = new HashSet<string>();
            foreach (var variable in saved.Variables)
            {
                if (variable is null || !names.Add(variable.Name) || !specifications.TryGetValue(variable.Name, out var spec) || !spec.Saved
                    || spec.Kind != variable.Kind || spec.Length != variable.Length || variable.Shape.IsDefault || !variable.Shape.SequenceEqual(spec.Shape)
                    || variable.Values.IsDefault || variable.Values.Length != spec.Length || variable.Values.Any(v => v.Kind != spec.Kind || v.Kind == ValueKind.String && v.Text is null))
                    throw new ScriptException("SAVE_CHARACTERS", "角色变量规格无效");
                variable.Values.CopyTo(values[variable.Name]);
            }
            var instance = new Instance(saved.Id, saved.Template, values);
            candidate.order.Add(instance); candidate.instances.Add(instance.Id, instance); candidate.nextId = Math.Max(candidate.nextId, instance.Id);
        }
        return candidate;
    }
    public ImmutableArray<CharacterSnapshot> SelectSnapshots(IReadOnlyList<long> indices)
    {
        if (indices.Count == 0 || indices.Distinct().Count() != indices.Count) throw new ScriptException("CHARACTER_BOUNDS", "保存角色名单为空或重复");
        var snapshots = Snapshot();
        return indices.Select(index => snapshots[IndexOf(At(index).Id)]).ToImmutableArray();
    }
    public void Append(ImmutableArray<CharacterSnapshot> snapshots)
    {
        var candidate = Candidate(snapshots);
        if (Count + candidate.Count > 10000 || specifications.Values.Sum(v => (long)v.Length) * (Count + candidate.Count) > 16_000_000)
            throw new ScriptException("SAVE_LIMIT", "角色存档总量超限");
        foreach (var instance in candidate.order)
        {
            var added = instance with { Id = ++nextId };
            order.Add(added); instances.Add(added.Id, added);
        }
    }
}
