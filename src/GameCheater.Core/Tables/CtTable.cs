namespace GameCheater.Core.Tables;

/// <summary>Cheat Engine value types, as they appear in a .CT's &lt;VariableType&gt;.</summary>
public enum CtValueType
{
    Unknown,
    Byte,
    TwoBytes,
    FourBytes,
    EightBytes,
    Float,
    Double,
    String,
    ByteArray,
    Binary,
    /// <summary>The entry is an Auto Assembler script rather than a value.</summary>
    AutoAssembler,
}

/// <summary>
/// How GameCheater will handle a given entry — the routing decision at the heart of the
/// hybrid model.
/// </summary>
public enum CtEntryKind
{
    /// <summary>A header/category with children and no address of its own.</summary>
    Group,

    /// <summary>A plain value/pointer entry we can run in our OWN runtime (FreezeCheat).</summary>
    Value,

    /// <summary>Has an Auto Assembler or Lua script — routed to the Cheat Engine backend.</summary>
    Script,

    /// <summary>A value entry whose address expression we can't resolve ourselves — CE backend.</summary>
    Unsupported,
}

/// <summary>One node in a parsed .CT (a CheatEntry). Entries nest, forming the tree CE shows.</summary>
public sealed class CtEntry
{
    public int Id { get; init; }
    public string Description { get; init; } = "";
    public CtValueType ValueType { get; init; } = CtValueType.Unknown;

    /// <summary>Raw CE address expression, e.g. <c>"game.exe"+1A2B3C</c> or a bare hex, or a symbol.</summary>
    public string? Address { get; init; }

    /// <summary>Pointer offsets in resolution order (base → … → final). See CtParser for the ordering note.</summary>
    public IReadOnlyList<int> Offsets { get; init; } = Array.Empty<int>();

    /// <summary>The [ENABLE]/[DISABLE] Auto Assembler body, if this is a script entry.</summary>
    public string? AssemblerScript { get; init; }

    public bool HasLuaScript { get; init; }

    public List<CtEntry> Children { get; } = new();

    /// <summary>Set by the classifier — who runs this entry.</summary>
    public CtEntryKind Kind { get; set; } = CtEntryKind.Unsupported;

    public bool HasScript => AssemblerScript is { Length: > 0 } || HasLuaScript
        || ValueType == CtValueType.AutoAssembler;
    public bool IsPointer => Offsets.Count > 0;
}

/// <summary>A parsed Cheat Engine table plus the classification summary.</summary>
public sealed class CtTable
{
    public int Version { get; init; }
    public List<CtEntry> Entries { get; } = new();

    /// <summary>Depth-first walk over every entry (groups included).</summary>
    public IEnumerable<CtEntry> Flatten()
    {
        static IEnumerable<CtEntry> Walk(CtEntry e)
        {
            yield return e;
            foreach (var c in e.Children)
                foreach (var d in Walk(c))
                    yield return d;
        }
        foreach (var e in Entries)
            foreach (var d in Walk(e))
                yield return d;
    }
}

/// <summary>A human-readable summary of what a loaded table contains and how it will run.</summary>
public sealed class CtLoadReport
{
    public int Total { get; init; }
    public int Groups { get; init; }
    public int RunnableValues { get; init; }
    public int RoutedToCe { get; init; }
    public int Unsupported { get; init; }

    /// <summary>Descriptions of entries that need the Cheat Engine backend (Lua/AA scripts).</summary>
    public IReadOnlyList<string> ScriptEntries { get; init; } = Array.Empty<string>();

    public override string ToString() =>
        $"{Total} entries: {RunnableValues} runnable in our engine, " +
        $"{RoutedToCe} need CE backend (scripts), {Unsupported} unsupported, {Groups} groups.";
}
