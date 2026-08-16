using System.Globalization;
using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// Binary-search a big candidate set down to the ONE authoritative value, by freezing half at a
/// time and watching the in-game readout. When many addresses all track the same thing (engine
/// integrity has dozens of mirrors), scanning can't tell them apart — but only the *authoritative*
/// one, when frozen, actually stops the value dropping. So: freeze half the set (held at its
/// current value); if the readout stops, the driver is in that half; if it keeps dropping, it's in
/// the other half. Halve and repeat — ~6 rounds for 60 candidates instead of 60.
///
/// Stateful across invocations via the candidate file, so it can be driven one round per call as
/// the player reports what they saw. Round 1 uses verdict "start"; then "stopped" / "dropping".
/// </summary>
public static class BisectFreeze
{
    public static void Run(ProcessMemory mem, string file, string verdict, int seconds)
    {
        if (!File.Exists(file)) { Console.WriteLine($"No candidate file at {file}. Export candidates from the app first."); return; }
        var lines = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToList();
        if (lines.Count < 2) { Console.WriteLine("Candidate file is empty."); return; }

        string type = lines[0].Trim().ToLowerInvariant();
        int size = TypeSize(type);
        if (size == 0) { Console.WriteLine($"Unknown type '{type}' in candidate file."); return; }

        // The set that was FROZEN last round is the first half of the file as it stood then.
        var all = lines.Skip(1).Select(ParseHex).Where(a => a != 0).ToList();
        int frozenCount = (all.Count + 1) / 2;

        List<ulong> working = verdict switch
        {
            "stopped" => all.Take(frozenCount).ToList(),   // driver was in the frozen half
            "dropping" => all.Skip(frozenCount).ToList(),  // driver was in the other half
            _ => all,                                        // "start": whole set
        };

        if (working.Count == 0) { Console.WriteLine("No candidates left — something went wrong; re-export and start over."); return; }

        if (working.Count == 1)
        {
            SaveWorking(file, type, working);
            Console.WriteLine($"\n*** DRIVER FOUND: 0x{working[0]:X} ***");
            Console.WriteLine($"That single address is the authoritative value. Freeze/pointer-scan it:");
            Console.WriteLine($"  --pointer-scan {mem.Process.Id} {working[0]:X}");
            return;
        }

        SaveWorking(file, type, working);

        int half = (working.Count + 1) / 2;
        var toFreeze = working.Take(half).ToList();
        Console.WriteLine($"{working.Count} candidate(s) left. Freezing the first {toFreeze.Count} at their current values for {seconds}s.");
        Console.WriteLine("RAM the component now and WATCH the readout.\n");

        // Snapshot each frozen address's current bytes, then hold them.
        var held = new List<(ulong Addr, byte[] Bytes)>();
        var buf = new byte[size];
        foreach (var a in toFreeze)
            if (mem.TryReadBytes(a, buf, size))
                held.Add((a, buf.ToArray()));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int writes = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (mem.Process.HasExited) { Console.WriteLine("target exited."); return; }
            foreach (var (addr, bytes) in held)
            {
                try { mem.WriteBytes(addr, bytes); writes++; } catch { }
            }
            Thread.Sleep(15);
        }

        Console.WriteLine($"Held {held.Count} address(es) ({writes} writes). What did the readout do?");
        Console.WriteLine("  • STOPPED dropping  → driver is in this frozen half:   --bisect <pid> <file> stopped");
        Console.WriteLine("  • KEPT dropping     → driver is in the other half:     --bisect <pid> <file> dropping");
    }

    private static void SaveWorking(string file, string type, List<ulong> working)
    {
        var outLines = new List<string> { type };
        outLines.AddRange(working.Select(a => "0x" + a.ToString("X")));
        File.WriteAllLines(file, outLines);
    }

    private static ulong ParseHex(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return ulong.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    private static int TypeSize(string type) => type switch
    {
        "byte" => 1,
        "short" => 2,
        "int" or "float" => 4,
        "long" or "double" => 8,
        _ => 0,
    };
}
