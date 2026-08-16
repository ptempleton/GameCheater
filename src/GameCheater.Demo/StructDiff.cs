using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// "What changed inside a known struct?" — a targeted, read-only alternative to a whole-process
/// value scan. Once a pointer chain pins one field (SnowRunner fuel at vehicleStruct+0x5E8), the
/// other per-vehicle values — the damage/integrity of each component — are almost certainly
/// nearby fields in the SAME struct. So resolve the struct base, snapshot every 4-byte float in
/// a window, and after the player triggers an event (takes damage) report exactly which offsets
/// moved. Far less noise than an unknown-value scan, and no debugger, so no anti-tamper risk.
/// </summary>
public static class StructDiff
{
    public static void Run(ProcessMemory mem, ulong moduleOffset, int[] derefOffsets,
        int windowBytes, int seconds)
    {
        ulong? structBase = ResolveStruct(mem, moduleOffset, derefOffsets);
        if (structBase is null)
        {
            Console.WriteLine("Couldn't resolve the struct pointer — check the module offset / deref offsets.");
            return;
        }
        ulong @base = structBase.Value;
        Console.WriteLine($"Struct base resolved to 0x{@base:X}. Watching [+0x0 .. +0x{windowBytes:X}] as floats.");

        int count = windowBytes / 4;
        var window = new byte[windowBytes];
        if (!mem.TryReadBytes(@base, window, windowBytes))
        {
            Console.WriteLine("Couldn't read the struct window.");
            return;
        }
        var baseline = new float[count];
        for (int i = 0; i < count; i++)
            baseline[i] = BitConverter.ToSingle(window, i * 4);

        Console.WriteLine("Baseline captured. Now TRIGGER the change in-game (take ONE hit to a component),");
        Console.WriteLine($"then hold still for the rest of the ~{seconds}s.\n");

        // Per-offset: first/last value, how many times it changed sample-to-sample (churn), and
        // its last sample. A DAMAGE field steps once and holds (low churn); PHYSICS jitter
        // changes almost every sample (high churn). Churn is what separates them.
        var first = new float[count];
        var last = new float[count];
        var prev = new float[count];
        var churn = new int[count];
        Array.Copy(baseline, first, count);
        Array.Copy(baseline, last, count);
        Array.Copy(baseline, prev, count);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int samples = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (mem.Process.HasExited) { Console.WriteLine("target exited."); break; }
            if (mem.TryReadBytes(@base, window, windowBytes))
            {
                samples++;
                for (int i = 0; i < count; i++)
                {
                    float now = BitConverter.ToSingle(window, i * 4);
                    if (float.IsNaN(now)) continue;
                    last[i] = now;
                    if (now != prev[i]) { churn[i]++; prev[i] = now; }
                }
            }
            Thread.Sleep(50);
        }

        // A damage step: net change, but changed only a handful of times (not every frame).
        int churnCap = Math.Max(3, samples / 10);
        var steppers = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (last[i] != first[i] && churn[i] >= 1 && churn[i] <= churnCap)
                steppers.Add(i);
        }

        Console.WriteLine($"{samples} samples. Low-churn steppers (changed <= {churnCap}x — these are the");
        Console.WriteLine("event-driven fields; high-churn physics jitter is filtered out):\n");
        foreach (int i in steppers.OrderBy(i => churn[i]).ThenBy(i => i))
        {
            int off = i * 4;
            bool looksIntegrity = InRange(last[i], 0, 1.0001f) || InRange(last[i], 0, 100.5f);
            string flag = looksIntegrity ? "  <-- candidate" : "";
            Console.WriteLine($"  +0x{off:X4}  0x{@base + (ulong)off:X}  {first[i]:G6} -> {last[i]:G6}  ({churn[i]}x){flag}");
        }
        if (steppers.Count == 0)
            Console.WriteLine("  (none — widen the window, or the field isn't in this struct)");

        Console.WriteLine("\nFreeze a candidate at its PRISTINE value and watch the damage icon clear:");
        Console.WriteLine($"  --freeze {mem.Process.Id} <absoluteAddress> float <pristineValue> 20");
    }

    /// <summary>
    /// Like <see cref="Run"/>, but also follows the pointer fields in the struct into their
    /// sub-objects and diffs those too. Per-component vehicle damage isn't stored inline next to
    /// fuel — it lives in small heap sub-objects (one per component) that the vehicle struct
    /// points to. Those objects are almost all state, so a damage step stands out with far less
    /// physics noise than a whole-memory scan. Each pointer field in [base, base+structWindow]
    /// that dereferences to readable memory is watched over [sub, sub+subWindow].
    /// </summary>
    public static void FollowScan(ProcessMemory mem, ulong moduleOffset, int[] derefOffsets,
        int structWindow, int subWindow, int seconds)
    {
        ulong? structBase = ResolveStruct(mem, moduleOffset, derefOffsets);
        if (structBase is null) { Console.WriteLine("Couldn't resolve the struct pointer."); return; }
        ulong root = structBase.Value;

        // Watch the struct itself, plus every sub-object it points to.
        var regions = new List<(string Label, ulong Base, int Size)> { ("struct", root, structWindow) };
        var seen = new HashSet<ulong> { root };
        var header = new byte[structWindow];
        if (mem.TryReadBytes(root, header, structWindow))
        {
            for (int off = 0; off + 8 <= structWindow; off += 8)
            {
                ulong ptr = BitConverter.ToUInt64(header, off);
                // Plausible user-space heap pointer that actually reads → treat as a sub-object.
                if (ptr is > 0x10000 and < 0x7FFF_FFFF_FFFF && (ptr & 7) == 0 && seen.Add(ptr))
                {
                    var probe = new byte[subWindow];
                    if (mem.TryReadBytes(ptr, probe, subWindow))
                        regions.Add(($"[+0x{off:X}]", ptr, subWindow));
                }
                if (regions.Count > 96) break; // bound the work
            }
        }
        Console.WriteLine($"Struct 0x{root:X}: watching it + {regions.Count - 1} sub-object(s).");
        Console.WriteLine($"Damage ONE component ~5s in, then hold still (~{seconds}s).\n");

        // Per region: baseline + last + churn, all floats.
        var floatsBase = new List<float[]>();
        var floatsPrev = new List<float[]>();
        var floatsLast = new List<float[]>();
        var churn = new List<int[]>();
        foreach (var (_, b, size) in regions)
        {
            int n = size / 4;
            var buf = new byte[size];
            var arr = new float[n];
            if (mem.TryReadBytes(b, buf, size))
                for (int i = 0; i < n; i++) arr[i] = BitConverter.ToSingle(buf, i * 4);
            floatsBase.Add((float[])arr.Clone());
            floatsPrev.Add((float[])arr.Clone());
            floatsLast.Add((float[])arr.Clone());
            churn.Add(new int[n]);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int samples = 0;
        var scratch = new byte[Math.Max(structWindow, subWindow)];
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (mem.Process.HasExited) { Console.WriteLine("target exited."); break; }
            samples++;
            for (int r = 0; r < regions.Count; r++)
            {
                var (_, b, size) = regions[r];
                if (!mem.TryReadBytes(b, scratch, size)) continue;
                int n = size / 4;
                var prev = floatsPrev[r]; var last = floatsLast[r]; var ch = churn[r];
                for (int i = 0; i < n; i++)
                {
                    float now = BitConverter.ToSingle(scratch, i * 4);
                    if (float.IsNaN(now)) continue;
                    last[i] = now;
                    if (now != prev[i]) { ch[i]++; prev[i] = now; }
                }
            }
            Thread.Sleep(50);
        }

        int churnCap = Math.Max(2, samples / 12);
        Console.WriteLine($"{samples} samples. Low-churn steppers (<= {churnCap}x), by location:\n");
        int hits = 0;
        for (int r = 0; r < regions.Count; r++)
        {
            var (label, b, size) = regions[r];
            int n = size / 4;
            var first = floatsBase[r]; var last = floatsLast[r]; var ch = churn[r];
            for (int i = 0; i < n; i++)
            {
                if (last[i] == first[i] || ch[i] < 1 || ch[i] > churnCap) continue;
                // Show the int32 view too: a damage FLAG/enum (0<->1, small ints) is invisible as
                // a float but obvious as an int, and that's the kind of value that drives an icon.
                int fi = BitConverter.SingleToInt32Bits(first[i]);
                int li = BitConverter.SingleToInt32Bits(last[i]);
                bool integrity = (last[i] is >= 0 and <= 1.0001f) || (last[i] is >= 0 and <= 100.5f);
                bool flagLike = (li is >= 0 and <= 16) || (fi is >= 0 and <= 16);
                string tag = flagLike ? "  <-- FLAG?" : integrity ? "  <-- candidate" : "";
                Console.WriteLine($"  {label,-10} +0x{i * 4:X4}  0x{b + (ulong)(i * 4):X}  " +
                                  $"f:{first[i]:G6}->{last[i]:G6}  i:{fi}->{li}  ({ch[i]}x){tag}");
                hits++;
            }
        }
        if (hits == 0)
            Console.WriteLine("  (none — try a bigger structWindow, or the components are nested another level down)");
        Console.WriteLine($"\nFreeze a candidate at pristine and watch the icon:  --freeze {mem.Process.Id} <addr> float <val> 20");
    }

    /// <summary>
    /// Search the vehicle struct and its sub-objects for a known value (e.g. engine integrity
    /// 59, or its max 180). Once the game shows real numbers, this pinpoints the field's chain
    /// instantly — no whole-memory scan. Each hit prints its chain [0x28, subOff, fieldOff] plus
    /// neighboring cells, so a current/max pair (59 next to 180) is obvious.
    /// </summary>
    public static void Find(ProcessMemory mem, ulong moduleOffset, int[] derefOffsets,
        int structWindow, int subWindow, float target)
    {
        ulong? structBase = ResolveStruct(mem, moduleOffset, derefOffsets);
        if (structBase is null) { Console.WriteLine("Couldn't resolve the struct pointer."); return; }
        ulong root = structBase.Value;

        var regions = new List<(string Chain, ulong Base, int Size)>
        {
            ($"0x{moduleOffset:X},{Csv(derefOffsets)}", root, structWindow),
        };
        var seen = new HashSet<ulong> { root };
        var header = new byte[structWindow];
        if (mem.TryReadBytes(root, header, structWindow))
        {
            for (int off = 0; off + 8 <= structWindow; off += 8)
            {
                ulong ptr = BitConverter.ToUInt64(header, off);
                if (ptr is > 0x10000 and < 0x7FFF_FFFF_FFFF && (ptr & 7) == 0 && seen.Add(ptr))
                {
                    var probe = new byte[subWindow];
                    if (mem.TryReadBytes(ptr, probe, subWindow))
                        regions.Add(($"0x{moduleOffset:X},{Csv(derefOffsets)},0x{off:X},<field>", ptr, subWindow));
                }
                if (regions.Count > 128) break;
            }
        }

        int iTarget = (int)target;
        Console.WriteLine($"Searching struct + {regions.Count - 1} sub-object(s) for {target:G6} (int {iTarget})…\n");
        int hits = 0;
        foreach (var (chain, b, size) in regions)
        {
            var buf = new byte[size];
            if (!mem.TryReadBytes(b, buf, size)) continue;
            int n = size / 4;
            for (int i = 0; i < n; i++)
            {
                float f = BitConverter.ToSingle(buf, i * 4);
                int iv = BitConverter.ToInt32(buf, i * 4);
                if (f != target && iv != iTarget) continue;

                int off = i * 4;
                // Show a few neighbors to reveal a current/max pair.
                var nb = new List<string>();
                for (int j = Math.Max(0, i - 2); j <= Math.Min(n - 1, i + 3); j++)
                {
                    float nf = BitConverter.ToSingle(buf, j * 4);
                    string mark = j == i ? "*" : " ";
                    nb.Add($"{mark}+0x{j * 4:X}={(nf == MathF.Round(nf) && MathF.Abs(nf) < 1e6 ? ((int)nf).ToString() : nf.ToString("G4"))}");
                }
                string chainForField = chain.Replace("<field>", $"0x{off:X}");
                Console.WriteLine($"  0x{b + (ulong)off:X}  chain[{chainForField}]");
                Console.WriteLine($"      neighbors: {string.Join("  ", nb)}");
                hits++;
                if (hits >= 60) { Console.WriteLine("  …(stopping at 60)"); return; }
            }
        }
        if (hits == 0)
            Console.WriteLine("  (not found — try the other value of the pair, or a bigger window)");
    }

    private static string Csv(int[] offs) => string.Join(",", offs.Select(o => "0x" + o.ToString("X")));

    /// <summary>
    /// Walk to the struct pointer: start at module+offset, dereference, then dereference through
    /// EVERY given offset (unlike <see cref="PointerChain"/>, whose last offset is a plain add).
    /// For fuel that's moduleOffset 0x2AA17F0, derefs [0x28] → the vehicle struct pointer, with
    /// fuel itself at +0x5E8 inside it.
    /// </summary>
    private static ulong? ResolveStruct(ProcessMemory mem, ulong moduleOffset, int[] derefOffsets)
    {
        try
        {
            ulong addr = mem.MainModuleBase + moduleOffset;
            addr = mem.Read<ulong>(addr);
            if (addr == 0) return null;
            foreach (int off in derefOffsets)
            {
                addr = mem.Read<ulong>(addr + (ulong)(long)off);
                if (addr == 0) return null;
            }
            return addr;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool InRange(float v, float lo, float hi) => v >= lo && v <= hi;
}
