using System.Collections.Immutable;

namespace mEmuera.Contracts;

public sealed record CharacterTemplate(long Number, ImmutableDictionary<string, ImmutableArray<EraValue>> Values, SourceId? Source = null);
public sealed record CharacterSnapshot(long Id, long Template, ImmutableArray<SavedVariable> Variables);
public enum CharacterOperation { Add, Delete, Copy, AddCopy, Swap, Clear }
