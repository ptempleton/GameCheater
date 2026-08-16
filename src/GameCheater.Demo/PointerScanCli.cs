using System.Globalization;
using System.Text.Json;
using GameCheater.Core.Memory;
using GameCheater.Core.Scanning;

namespace GameCheater.Demo;

/// <summary>
/// Drives <see cref="PointerScanner"/> and the two-session workflow that makes a chain durable.
///
/// A raw heap address dies on relaunch; a static pointer chain survives it — but only chains
/// that resolve correctly in MORE THAN ONE session are trustworthy, since a single scan turns up
/// many coincidental paths. So:
///   1. <c>--pointer-scan  &lt;pid&gt; &lt;fuelAddr&gt;</c>  — scan, save every candidate to a file.
///   2. restart the game, re-scan the value to its new address.
///   3. <c>--pointer-verify &lt;pid&gt; &lt;newAddr&gt;</c> — keep only the saved chains that still
///      resolve to the value. Repeat once more; whatever survives twice is your durable cheat.
/// </summary>
public static class PointerScanCli
{
    private const string PathsFile = "pointer-paths.json";

    private sealed record PathDto(string Module, string ModuleOffset, List<string> Offsets);

    public static void Scan(ProcessMemory mem, ulong target, int maxDepth, int maxOffset)
    {
        Console.WriteLine($"Pointer scan for 0x{target:X}  (maxDepth={maxDepth}, maxOffset=0x{maxOffset:X})");
        if (mem.TryGetModuleContaining(target, out _, out _))
            Console.WriteLine("NOTE: that address is already inside a module — it may not need a pointer chain.");
        Console.WriteLine("This reads a lot of memory; give it a moment.\n");

        var options = new PointerScanOptions { MaxDepth = maxDepth, MaxOffset = maxOffset };
        var paths = PointerScanner.Scan(mem, target, options, Console.WriteLine);

        if (paths.Count == 0)
        {
            Console.WriteLine("\nNo static pointer paths found. Try a larger --maxOffset or --maxDepth.");
            return;
        }

        // Keep only paths that actually resolve right now (cheap sanity filter before we trust them).
        var good = paths.Where(p => PointerScanner.Verify(mem, p, target)).ToList();
        Console.WriteLine($"\n{good.Count} path(s) resolve to the target this session (showing up to 25):\n");
        foreach (var p in good.Take(25))
            Console.WriteLine($"  {p}");

        Save(good);
        Console.WriteLine($"\nSaved {good.Count} candidate(s) to {PathsFile}.");
        Console.WriteLine("Now RESTART the game, re-scan the value to its new address, and run:");
        Console.WriteLine($"  --pointer-verify <pid> <newAddress>   (keeps only the chains that still hit it)");
    }

    public static void Verify(ProcessMemory mem, ulong target)
    {
        var saved = Load();
        if (saved.Count == 0)
        {
            Console.WriteLine($"No saved paths in {PathsFile}. Run --pointer-scan first.");
            return;
        }

        Console.WriteLine($"Checking {saved.Count} saved path(s) against 0x{target:X} in this fresh session…\n");
        var survivors = saved.Where(p => PointerScanner.Verify(mem, p, target)).ToList();

        if (survivors.Count == 0)
        {
            Console.WriteLine("None of the saved chains resolve to the value now.");
            Console.WriteLine("The durable path wasn't among the candidates — widen the scan (bigger maxOffset/maxDepth) and retry.");
            return;
        }

        Console.WriteLine($"{survivors.Count} chain(s) survived into this session — these are the durable ones:\n");
        foreach (var p in survivors)
            Console.WriteLine($"  {p}");

        Save(survivors);
        Console.WriteLine($"\nNarrowed {PathsFile} to the {survivors.Count} survivor(s).");
        Console.WriteLine("Run this once more after another restart to be safe; then author the shortest survivor as:");
        var best = survivors[0];
        Console.WriteLine($"  \"resolve\": {JsonSerializer.Serialize(ToDto(best), Options)}");
    }

    public static void ResolveSaved(ProcessMemory mem)
    {
        var saved = Load();
        if (saved.Count == 0)
        {
            Console.WriteLine($"No saved paths in {PathsFile}. Run --pointer-scan first.");
            return;
        }

        Console.WriteLine($"Resolving {saved.Count} saved path(s) without writing memory...\n");
        foreach (var path in saved)
        {
            ulong? address = path.ToResolver()(mem);
            if (address is not ulong resolved)
            {
                Console.WriteLine($"  {path} => unresolved");
                continue;
            }

            try
            {
                int value = mem.Read<int>(resolved);
                Console.WriteLine($"  {path} => 0x{resolved:X} (i32 {value})");
            }
            catch (IOException)
            {
                Console.WriteLine($"  {path} => 0x{resolved:X} (unreadable)");
            }
        }
    }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private static void Save(IReadOnlyList<PointerPath> paths) =>
        File.WriteAllText(PathsFile, JsonSerializer.Serialize(paths.Select(ToDto).ToList(), Options));

    private static List<PointerPath> Load()
    {
        if (!File.Exists(PathsFile))
            return new List<PointerPath>();
        var dtos = JsonSerializer.Deserialize<List<PathDto>>(File.ReadAllText(PathsFile)) ?? new();
        return dtos.Select(FromDto).ToList();
    }

    private static PathDto ToDto(PointerPath p) => new(
        p.Module,
        "0x" + p.ModuleOffset.ToString("X"),
        p.Offsets.Select(o => (o < 0 ? "-0x" + (-o).ToString("X") : "0x" + o.ToString("X"))).ToList());

    private static PointerPath FromDto(PathDto d) => new()
    {
        Module = d.Module,
        ModuleOffset = ParseHex(d.ModuleOffset),
        Offsets = d.Offsets.Select(ParseOffset).ToArray(),
    };

    private static ulong ParseHex(string s)
    {
        s = s.Trim();
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        ulong v = ulong.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return v;
    }

    private static int ParseOffset(string s)
    {
        s = s.Trim();
        bool neg = s.StartsWith('-');
        if (neg) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        int v = int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }
}
