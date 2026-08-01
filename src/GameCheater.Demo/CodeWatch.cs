using GameCheater.Core.Memory;
using GameCheater.Core.Scanning;

namespace GameCheater.Demo;

/// <summary>
/// Capture a code-patch cheat by its ON/OFF effect. A single before/after diff is hopeless —
/// the game and the external trainer churn memory constantly, drowning the real patch in
/// noise. So we snapshot OFF, have the user turn the cheat ON, snapshot again, then turn it
/// OFF once more and keep only the bytes that changed for ON *and reverted* for OFF. That
/// lock-step revert is what distinguishes the cheat's patch from background noise.
///
/// We also scan only the game's own module code (not every executable page in the process),
/// which excludes the trainer's injected pages where most of the noise lives.
/// </summary>
public static class CodeWatch
{
    public static void Run(ProcessMemory mem, CaptureSession session)
    {
        PrintHelp();
        while (true)
        {
            Console.Write($"[{session.Count} captured] > ");
            Console.Out.Flush();
            var line = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (line is null or "q") break;

            switch (line)
            {
                case "c": CaptureByToggle(mem, session); break;
                case "help": PrintHelp(); break;
                case "": break;
                default: Console.WriteLine("commands:  c = capture a cheat   help   q = quit"); break;
            }
        }
    }

    private static void CaptureByToggle(ProcessMemory mem, CaptureSession session)
    {
        Console.WriteLine("\nCapture a code cheat by toggling it (this cancels background noise).");

        Prompt("  1) Make sure the cheat is OFF in your trainer, then press Enter...");
        Console.WriteLine("     scanning OFF state...");
        Console.Out.Flush();
        var off = CaptureGameCode(mem);

        Prompt("  2) Turn the cheat ON in your trainer, then press Enter...");
        Console.WriteLine("     scanning ON state...");
        Console.Out.Flush();
        var changesOn = off.DiffAgainstCurrent(mem, joinGap: 1);

        Prompt("  3) Turn the cheat OFF again, then press Enter...");
        Console.WriteLine("     confirming which changes reverted...");
        Console.Out.Flush();

        // Keep only sites that reverted to their OFF bytes — that lock-step is the real patch.
        var real = new List<MemoryChange>();
        var buf = new byte[64];
        foreach (var ch in changesOn)
        {
            int len = ch.Old.Length;
            if (len > buf.Length) buf = new byte[len];
            if (mem.TryReadBytes(ch.Address, buf, len) && buf.AsSpan(0, len).SequenceEqual(ch.Old))
                real.Add(ch);
        }

        if (real.Count == 0)
        {
            Console.WriteLine($"No reverting code changes ({changesOn.Count} noisy candidates dropped).");
            Console.WriteLine("This is likely a VALUE cheat (money/fuel/tires/time), or your trainer runs it from");
            Console.WriteLine("injected memory we don't scan. Try  .\\watch-values SnowRunner int  instead.");
            return;
        }

        Console.WriteLine($"{real.Count} code site(s) are the cheat's patch:");
        var suggestions = new List<CodePatchSuggestion>();
        foreach (var ch in real)
        {
            var suggestion = Oracle.BuildCodeSuggestion(off, ch);
            suggestions.Add(suggestion);
            Console.WriteLine($"   @ 0x{ch.Address:X}   off:{Signature.ToPattern(ch.Old)}  on:{Signature.ToPattern(ch.New)}");
        }

        var info = CaptureSession.Prompt();
        if (info is not { } p) { Console.WriteLine("skipped."); return; }

        for (int i = 0; i < suggestions.Count; i++)
        {
            var s = suggestions[i];
            string name = suggestions.Count == 1 ? p.Name : $"{p.Name} #{i + 1}";
            session.AddPatch(name, p.Category, p.Description, s.Signature, s.PatchOffset, s.Patched);
        }
        Console.WriteLine($"captured \"{p.Name}\" ({real.Count} site(s)). Do another with 'c', or 'q' to finish.");
    }

    private static void Prompt(string message)
    {
        Console.Write(message + " ");
        Console.Out.Flush();
        Console.ReadLine();
    }

    // Only the main module's executable regions — the game's own code, where patches land.
    private static MemorySnapshot CaptureGameCode(ProcessMemory mem)
    {
        ulong start = mem.MainModuleBase;
        ulong end = start + (ulong)mem.MainModuleSize;
        return MemorySnapshot.Capture(mem, r => r.IsExecutable && r.Base >= start && r.Base < end);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Capture code cheats (God Mode / No Damage) by toggling them:
              Type 'c', then follow the OFF -> ON -> OFF prompts for ONE cheat.
              Only bytes that change when ON and revert when OFF are kept (noise is filtered out).
            Commands:  c = capture a cheat    help    q = quit (saves JSON)
            """);
    }
}
