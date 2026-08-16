using System.Diagnostics;
using GameCheater.Core.Definitions;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Scanning;

/// <summary>One discovered static→…→target pointer path, ready to author as a durable cheat.</summary>
public sealed class PointerPath
{
    /// <summary>Module the static base lives in (usually the main .exe).</summary>
    public required string Module { get; init; }

    /// <summary>Offset of the static base within <see cref="Module"/>.</summary>
    public required ulong ModuleOffset { get; init; }

    /// <summary>Dereference offsets in resolution order (Cheat-Engine order), matching
    /// <see cref="PointerChain"/>: <c>[[[base]+o0]+o1]…+oLast</c>.</summary>
    public required int[] Offsets { get; init; }

    public int Depth => Offsets.Length;

    /// <summary>Turn this into the resolver the cheat runtime walks at enable time.</summary>
    public Func<ProcessMemory, ulong?> ToResolver() =>
        mem => mem.GetModuleBase(Module) is { } b
            ? new PointerChain(b + ModuleOffset, Offsets).Resolve(mem)
            : null;

    /// <summary>The serializable form for a games/&lt;game&gt;.json definition.</summary>
    public ResolveSpec ToResolveSpec() => new()
    {
        Kind = "pointer",
        Module = Module,
        ModuleOffset = "0x" + ModuleOffset.ToString("X"),
        Offsets = Offsets.Select(o => (o < 0 ? "-0x" + (-o).ToString("X") : "0x" + o.ToString("X"))).ToList(),
    };

    /// <summary>Cheat-Engine-style display, e.g. <c>SnowRunner.exe+0x1A2B3C -> +0x40 -> +0x18</c>.</summary>
    public override string ToString()
    {
        string offs = string.Join(" -> ", Offsets.Select(o => (o < 0 ? "-0x" + (-o).ToString("X") : "+0x" + o.ToString("X"))));
        return $"{Module}+0x{ModuleOffset:X}" + (Offsets.Length == 0 ? "" : $" -> {offs}");
    }
}

/// <summary>Tuning for a pointer scan. Defaults mirror a conservative Cheat Engine scan.</summary>
public sealed class PointerScanOptions
{
    /// <summary>Max dereference hops from the static base (chain length).</summary>
    public int MaxDepth { get; init; } = 5;

    /// <summary>Largest struct offset allowed at each hop (bytes). Bigger = more chains, more noise.</summary>
    public int MaxOffset { get; init; } = 0x800;

    /// <summary>Stop after this many complete paths.</summary>
    public int MaxResults { get; init; } = 60;

    /// <summary>Backstop on reverse-search work so a pathological game can't run forever.</summary>
    public int MaxNodes { get; init; } = 2_000_000;

    /// <summary>Cap on the pointer index (candidate pointer slots) to bound memory use.</summary>
    public int MaxPointerSlots { get; init; } = 60_000_000;
}

/// <summary>
/// A static pointer-chain scanner — the in-house equivalent of Cheat Engine's "pointer scan",
/// so a value cheat found at a heap address (SnowRunner fuel) can be pinned to a module-relative
/// path that survives relaunches instead of a raw address that dies with the process.
///
/// It works backwards, the way CE does. First it indexes every aligned slot in the target's
/// committed memory whose contents look like a pointer into that same memory. Then, starting
/// from the target address, it finds slots whose value sits within <c>MaxOffset</c> below it
/// (so <c>*slot + off == wanted</c>), and recurses on each such slot's address, until it reaches
/// a slot inside a loaded module — that slot is a static base, and the offsets collected along
/// the way (reversed) are the chain. The output paths resolve through <see cref="PointerChain"/>.
///
/// A single scan finds candidates; only a chain that still resolves to the value after a RESTART
/// is truly durable, so the intended workflow is: scan, restart the game, re-scan the (new)
/// address, and keep the paths common to both. <see cref="Verify"/> is the per-session check.
/// </summary>
public static class PointerScanner
{
    private readonly record struct Slot(ulong Value, ulong Address, bool Static);

    public static IReadOnlyList<PointerPath> Scan(ProcessMemory memory, ulong target,
        PointerScanOptions? options = null, Action<string>? log = null)
    {
        options ??= new PointerScanOptions();
        var sw = Stopwatch.StartNew();

        var moduleRanges = ModuleRanges(memory);
        log?.Invoke($"indexing pointers ({moduleRanges.Count} modules)…");
        var slots = BuildPointerIndex(memory, moduleRanges, options, log);
        log?.Invoke($"indexed {slots.Length:N0} pointer slot(s) in {sw.Elapsed.TotalSeconds:F1}s; reverse-searching…");

        // slots is sorted by Value, so "which slots hold a value in [lo, hi]" is a binary-searched range.
        var values = new ulong[slots.Length];
        for (int i = 0; i < slots.Length; i++)
            values[i] = slots[i].Value;

        var results = new List<PointerPath>();
        var queue = new Queue<(ulong Wanted, int[] Offsets)>();
        queue.Enqueue((target, Array.Empty<int>()));
        int nodes = 0;

        while (queue.Count > 0 && results.Count < options.MaxResults && nodes < options.MaxNodes)
        {
            var (wanted, offsets) = queue.Dequeue();
            nodes++;

            ulong lo = wanted > (ulong)options.MaxOffset ? wanted - (ulong)options.MaxOffset : 0;
            (int start, int end) = ValueRange(values, lo, wanted);

            for (int i = start; i < end && results.Count < options.MaxResults; i++)
            {
                var slot = slots[i];
                int off = (int)(wanted - slot.Value);
                var next = Append(offsets, off);

                if (slot.Static)
                {
                    if (memory.TryGetModuleContaining(slot.Address, out var module, out var moduleOffset))
                    {
                        results.Add(new PointerPath
                        {
                            Module = module,
                            ModuleOffset = moduleOffset,
                            // Discovery order is target→base; resolution order is base→target.
                            Offsets = Reversed(next),
                        });
                    }
                }
                else if (next.Length < options.MaxDepth)
                {
                    queue.Enqueue((slot.Address, next));
                }
            }
        }

        log?.Invoke($"reverse search done: {results.Count} path(s), {nodes:N0} node(s), {sw.Elapsed.TotalSeconds:F1}s total.");

        // Prefer main-exe bases, then shorter chains, then smaller offsets — the ones most likely stable.
        string mainModule = MainModuleName(memory);
        return results
            .OrderByDescending(p => string.Equals(p.Module, mainModule, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Depth)
            .ThenBy(p => p.Offsets.Sum(o => Math.Abs(o)))
            .ToList();
    }

    /// <summary>Resolve a path in the current session and check it lands on <paramref name="expected"/>.</summary>
    public static bool Verify(ProcessMemory memory, PointerPath path, ulong expected)
        => path.ToResolver()(memory) == expected;

    private static Slot[] BuildPointerIndex(ProcessMemory memory, List<(ulong Start, ulong End)> modules,
        PointerScanOptions options, Action<string>? log)
    {
        // A value is a plausible pointer if it lands inside committed readable memory and is
        // 4-byte aligned. Collect committed readable regions once; reuse as both the scan set
        // and the "does this value point somewhere real" membership test.
        var regions = memory.EnumerateRegions()
            .Where(r => r.IsReadable)
            .Select(r => (Start: r.Base, End: r.Base + r.Size))
            .OrderBy(r => r.Start)
            .ToList();

        var slots = new List<Slot>(1 << 20);
        var buffer = new byte[1 << 20]; // 1 MB
        long lastLog = 0;

        foreach (var (regionStart, regionEnd) in regions)
        {
            ulong addr = regionStart;
            while (addr < regionEnd)
            {
                int want = (int)Math.Min((ulong)buffer.Length, regionEnd - addr);
                want &= ~7; // whole 8-byte slots only
                if (want == 0)
                    break;
                if (!memory.TryReadBytes(addr, buffer, want))
                {
                    addr += (ulong)buffer.Length;
                    continue;
                }

                for (int off = 0; off + 8 <= want; off += 8)
                {
                    ulong value = BitConverter.ToUInt64(buffer, off);
                    if ((value & 3) != 0 || !PointsIntoCommitted(regions, value))
                        continue;

                    ulong slotAddr = addr + (ulong)off;
                    slots.Add(new Slot(value, slotAddr, InModule(modules, slotAddr)));
                    if (slots.Count >= options.MaxPointerSlots)
                    {
                        log?.Invoke($"pointer index hit the {options.MaxPointerSlots:N0} cap — narrow MaxOffset/regions if results look thin.");
                        goto done;
                    }
                }

                addr += (ulong)want;
                if (slots.Count - lastLog > 5_000_000)
                {
                    lastLog = slots.Count;
                    log?.Invoke($"  …{slots.Count:N0} pointer slots so far");
                }
            }
        }

    done:
        var array = slots.ToArray();
        Array.Sort(array, (a, b) => a.Value.CompareTo(b.Value));
        return array;
    }

    private static bool PointsIntoCommitted(List<(ulong Start, ulong End)> regions, ulong value)
    {
        int lo = 0, hi = regions.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (value < regions[mid].Start) hi = mid - 1;
            else if (value >= regions[mid].End) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static (int Start, int End) ValueRange(ulong[] values, ulong lo, ulong hi)
    {
        int start = LowerBound(values, lo);
        int end = UpperBound(values, hi);
        return (start, end);
    }

    private static int LowerBound(ulong[] values, ulong target)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (values[mid] < target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(ulong[] values, ulong target)
    {
        int lo = 0, hi = values.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (values[mid] <= target) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static int[] Append(int[] offsets, int value)
    {
        var next = new int[offsets.Length + 1];
        Array.Copy(offsets, next, offsets.Length);
        next[^1] = value;
        return next;
    }

    private static int[] Reversed(int[] offsets)
    {
        var r = new int[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
            r[i] = offsets[offsets.Length - 1 - i];
        return r;
    }

    private static List<(ulong Start, ulong End)> ModuleRanges(ProcessMemory memory)
    {
        var list = new List<(ulong, ulong)>();
        foreach (System.Diagnostics.ProcessModule m in memory.Process.Modules)
        {
            ulong b = (ulong)m.BaseAddress.ToInt64();
            list.Add((b, b + (ulong)m.ModuleMemorySize));
        }
        return list;
    }

    private static bool InModule(List<(ulong Start, ulong End)> modules, ulong address)
    {
        foreach (var (start, end) in modules)
            if (address >= start && address < end)
                return true;
        return false;
    }

    private static string MainModuleName(ProcessMemory memory)
        => memory.Process.MainModule?.ModuleName ?? "";
}
