using System.Collections.Immutable;

namespace mEmuera.Contracts;

public sealed record TableColumnSnapshot(string Name, int Type, bool Nullable)
{
    public EraValue? DefaultValue { get; init; }
}
public sealed record TableSnapshot(string Name, bool CaseSensitive, ImmutableArray<TableColumnSnapshot> Columns,
    ImmutableArray<ImmutableArray<EraValue?>> Rows)
{
    public ImmutableArray<string>? PrimaryKey { get; init; }
    public ImmutableArray<TableUniqueConstraintSnapshot>? UniqueConstraints { get; init; }
}
public sealed record TableUniqueConstraintSnapshot(string Name, ImmutableArray<string> Columns, bool PrimaryKey);
public sealed record ExtensionSaveNames
{
    public ImmutableHashSet<string> Maps { get; init; } = [];
    public ImmutableHashSet<string> Tables { get; init; } = [];
    public ImmutableHashSet<string> XmlDocuments { get; init; } = [];
}
