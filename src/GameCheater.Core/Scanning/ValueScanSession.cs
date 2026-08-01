using System.Globalization;
using GameCheater.Core.Cheats;
using GameCheater.Core.Definitions;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Scanning;

/// <summary>One surviving scan candidate: its address and current value (as text for the UI).</summary>
public readonly record struct ScanCandidate(ulong Address, string Value)
{
    public string Display => $"0x{Address:X}   =   {Value}";
}

/// <summary>
/// A non-generic facade over <see cref="ValueScanner{T}"/> so UI code can drive a scan without
/// knowing the element type (chosen at runtime from a dropdown). Parses/formats values as
/// strings, and turns a found candidate into either a live "freeze" test or a durable
/// authored definition.
/// </summary>
public interface IValueScanSession
{
    string TypeName { get; }
    long CandidateCount { get; }
    bool InSnapshotMode { get; }
    bool FirstScanDone { get; }

    void FirstScanExact(string value);
    void FirstScanUnknown();
    void NarrowIncreased();
    void NarrowDecreased();
    void NarrowChanged();
    void NarrowUnchanged();
    void NarrowExact(string value);
    void Reset();

    IReadOnlyList<ScanCandidate> Top(int max);

    /// <summary>A live freeze cheat at this address (absolute — for testing this session).</summary>
    Cheat CreateFreeze(ulong address, string? valueText, string name, string category, string? description);

    /// <summary>A durable definition: module+offset if the address is in a module, else a heap DRAFT.</summary>
    CheatDefinition CreateDefinition(ProcessMemory memory, ulong address, string? valueText,
        string name, string category, string? description);
}

/// <summary>Factory: build a scan session for a runtime-chosen value type.</summary>
public static class ValueScan
{
    public static readonly string[] Types = { "int", "float", "long", "short", "byte", "double" };

    public static IValueScanSession Create(ProcessMemory memory, string type) => type.ToLowerInvariant() switch
    {
        "int" => new ValueScanSession<int>(memory, "int", int.Parse),
        "float" => new ValueScanSession<float>(memory, "float", s => float.Parse(s, CultureInfo.InvariantCulture)),
        "long" => new ValueScanSession<long>(memory, "long", long.Parse),
        "short" => new ValueScanSession<short>(memory, "short", short.Parse),
        "byte" => new ValueScanSession<byte>(memory, "byte", byte.Parse),
        "double" => new ValueScanSession<double>(memory, "double", s => double.Parse(s, CultureInfo.InvariantCulture)),
        _ => throw new ArgumentException($"Unsupported scan type '{type}'.", nameof(type)),
    };
}

internal sealed class ValueScanSession<T> : IValueScanSession where T : unmanaged, IComparable<T>
{
    private readonly ValueScanner<T> _scanner;
    private readonly Func<string, T> _parse;

    public string TypeName { get; }
    public bool FirstScanDone { get; private set; }
    public long CandidateCount => _scanner.CandidateCount;
    public bool InSnapshotMode => _scanner.InSnapshotMode;

    public ValueScanSession(ProcessMemory memory, string typeName, Func<string, T> parse)
    {
        _scanner = new ValueScanner<T>(memory);
        TypeName = typeName;
        _parse = parse;
    }

    public void FirstScanExact(string value) { _scanner.FirstScan(_parse(value)); FirstScanDone = true; }
    public void FirstScanUnknown() { _scanner.FirstScanUnknown(); FirstScanDone = true; }
    public void NarrowIncreased() => _scanner.NextScanIncreased();
    public void NarrowDecreased() => _scanner.NextScanDecreased();
    public void NarrowChanged() => _scanner.NextScanChanged();
    public void NarrowUnchanged() => _scanner.NextScanUnchanged();
    public void NarrowExact(string value) => _scanner.NextScanExact(_parse(value));
    public void Reset() { _scanner.Reset(); FirstScanDone = false; }

    public IReadOnlyList<ScanCandidate> Top(int max)
    {
        var list = new List<ScanCandidate>(Math.Min(max, _scanner.Count));
        for (int i = 0; i < Math.Min(max, _scanner.Count); i++)
        {
            ulong addr = _scanner.Results[i];
            list.Add(new ScanCandidate(addr, _scanner.ReadCurrent(addr).ToString() ?? ""));
        }
        return list;
    }

    public Cheat CreateFreeze(ulong address, string? valueText, string name, string category, string? description)
    {
        bool atCurrent = string.IsNullOrWhiteSpace(valueText);
        T value = atCurrent ? default : (T)Convert.ChangeType(valueText!, typeof(T), CultureInfo.InvariantCulture);
        return new FreezeCheat<T>(Resolve.Absolute(address), value, freeze: true, freezeAtCurrentValue: atCurrent)
        {
            Name = name,
            Category = category,
            Description = description,
        };
    }

    public CheatDefinition CreateDefinition(ProcessMemory memory, ulong address, string? valueText,
        string name, string category, string? description)
    {
        ResolveSpec spec;
        string? note = description;
        if (memory.TryGetModuleContaining(address, out var module, out var offset))
        {
            spec = new ResolveSpec { Kind = "static", Module = module, ModuleOffset = "0x" + offset.ToString("X") };
        }
        else
        {
            spec = new ResolveSpec { Kind = "pointer", ModuleOffset = "0x0", Offsets = new List<string>() };
            note = $"DRAFT — heap address 0x{address:X}; needs a pointer scan to be durable. {description}".Trim();
        }

        return new CheatDefinition
        {
            Name = name,
            Category = category,
            Description = note,
            Type = "freeze",
            ValueType = TypeName,
            Value = string.IsNullOrWhiteSpace(valueText) ? null : valueText,
            Resolve = spec,
        };
    }
}
