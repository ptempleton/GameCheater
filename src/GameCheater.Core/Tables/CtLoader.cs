using GameCheater.Core.Cheats;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Tables;

/// <summary>What came out of loading a .CT: runnable cheats, and what couldn't be converted.</summary>
public sealed class CtLoadResult
{
    /// <summary>Value/pointer entries turned into live, toggleable cheats in our runtime.</summary>
    public List<Cheat> Cheats { get; } = new();

    /// <summary>Descriptions of value entries we couldn't convert (complex address or unsupported type).</summary>
    public List<string> Unconverted { get; } = new();

    /// <summary>Count of Lua/AA script entries — these are the CE backend's job, not ours.</summary>
    public int Scripts { get; set; }
}

/// <summary>
/// Converts the classified <see cref="CtTable"/> into runnable cheats. Plain value and
/// pointer entries with an address we can resolve become <see cref="FreezeCheat{T}"/>
/// instances (freeze-at-current-value); script entries are counted for the CE backend;
/// anything we can't handle is reported rather than silently dropped.
///
/// A converted cheat's Category comes from its parent group's description, matching how
/// Cheat Engine organizes a table.
/// </summary>
public static class CtLoader
{
    public static CtLoadResult Build(CtTable table)
    {
        var result = new CtLoadResult();
        foreach (var entry in table.Entries)
            Walk(entry, "Loaded", result);
        return result;
    }

    private static void Walk(CtEntry e, string category, CtLoadResult result)
    {
        switch (e.Kind)
        {
            case CtEntryKind.Group:
                // Children inherit this group's name as their category.
                foreach (var child in e.Children)
                    Walk(child, e.Description, result);
                return;

            case CtEntryKind.Value:
                var cheat = TryMakeCheat(e, category);
                if (cheat is not null) result.Cheats.Add(cheat);
                else result.Unconverted.Add(e.Description);
                break;

            case CtEntryKind.Script:
                result.Scripts++;
                break;

            default:
                result.Unconverted.Add(e.Description);
                break;
        }

        // Value/script/unsupported entries can still nest children (CE allows it).
        foreach (var child in e.Children)
            Walk(child, category, result);
    }

    private static Cheat? TryMakeCheat(CtEntry e, string category)
    {
        var baseResolver = CtAddress.BuildResolver(e.Address);
        if (baseResolver is null)
            return null; // complex address expression — leave for the CE backend

        var resolver = e.IsPointer
            ? Resolve.Pointer(baseResolver, e.Offsets.ToArray())
            : baseResolver;

        return e.ValueType switch
        {
            CtValueType.Byte => Make<byte>(),
            CtValueType.TwoBytes => Make<short>(),
            CtValueType.FourBytes => Make<int>(),
            CtValueType.EightBytes => Make<long>(),
            CtValueType.Float => Make<float>(),
            CtValueType.Double => Make<double>(),
            _ => null, // String / ByteArray / Binary aren't simple freezes
        };

        Cheat Make<TValue>() where TValue : unmanaged => new FreezeCheat<TValue>(
            resolver, default, freeze: true, resolveEachTick: e.IsPointer, freezeAtCurrentValue: true)
        {
            Name = string.IsNullOrWhiteSpace(e.Description) ? $"Entry {e.Id}" : e.Description,
            Category = category,
            Description = e.IsPointer ? "Loaded from .CT (pointer)" : "Loaded from .CT",
        };
    }
}
