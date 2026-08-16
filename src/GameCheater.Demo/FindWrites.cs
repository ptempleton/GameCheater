using System.Globalization;
using GameCheater.Core.Debugging;
using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// The interactive front end for <see cref="WriteWatch"/> — our answer to Cheat Engine's
/// "find out what writes to this address", with no Cheat Engine required.
///
/// The intended loop, and the reason this exists: value-scan until you have an address that
/// tracks the thing you want (fuel, ammo, durability), point this at it, play for a few
/// seconds, and see exactly which instructions store to it. Then NOP the consuming one
/// *live* and look at the game — if the drain stops, save it as a durable AOB patch. That
/// last step is what value freezing cannot do for a continuously recomputed value.
/// </summary>
public static class FindWrites
{
    public static void Run(ProcessMemory mem, ulong address, int size, CaptureSession session)
    {
        Console.WriteLine($"Target: 0x{address:X}  ({size} bytes)");
        if (mem.TryGetModuleContaining(address, out var module, out var moduleOffset))
            Console.WriteLine($"        inside {module}+0x{moduleOffset:X}");
        else
            Console.WriteLine("        heap/dynamic address (not in a module)");

        PrintCurrentValue(mem, address, size);

        Console.WriteLine("\nAttaching as a debugger and arming a hardware write breakpoint...");
        WriteWatch watch;
        try
        {
            watch = WriteWatch.Start(mem, address, size);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed: {ex.Message}");
            return;
        }

        // Keyed by instruction address, NOT by list position: the writer list is sorted by hit
        // count and re-orders as the game runs, so a positional key would restore the wrong site.
        var patched = new Dictionary<ulong, PatchedSite>();
        try
        {
            if (watch.WatchedBytes < size)
            {
                Console.WriteLine($"NOTE: the address is only {watch.WatchedBytes}-byte aligned, so the CPU is " +
                                  $"watching {watch.WatchedBytes} of the {size} bytes you asked for.");
            }

            Console.WriteLine("Attached. The game freezes for a moment on every write — expect stutter.\n");
            Collect(watch, 10);
            if (watch.AntiDebugEngaged)
                Console.WriteLine("(anti-debug detected and neutralised: the game's PEB debugger flag is being cleared.)");
            Report(watch, patched);
            CommandLoop(watch, mem, session, patched);
        }
        finally
        {
            // Restore before detaching, always: leaving NOPs in a game we then walk away
            // from is exactly the kind of thing that gets blamed on "the trainer broke my save".
            RestoreAll(mem, patched);
            Console.WriteLine("Clearing breakpoints and detaching...");
            watch.Dispose();
        }
    }

    /// <summary>One live-NOPed writer, holding the bytes needed to put it back.</summary>
    private sealed record PatchedSite(ulong Address, byte[] Original, string Where);

    private static void Collect(WriteWatch watch, int seconds)
    {
        Console.WriteLine($"Collecting for {seconds}s — go make the value change in-game (drive, shoot, spend).");
        int before = -1;
        for (int i = 0; i < seconds * 4; i++)
        {
            Thread.Sleep(250);
            if (watch.TargetExited)
            {
                Console.WriteLine("\nThe game exited.");
                return;
            }
            if (watch.TotalHits != before)
            {
                before = watch.TotalHits;
                Console.Write($"\r  {before} write(s) seen, {watch.Snapshot().Count} distinct writer(s)…   ");
            }
        }
        Console.WriteLine();
    }

    private static void Report(WriteWatch watch, Dictionary<ulong, PatchedSite> patched)
    {
        var writers = watch.Snapshot();
        Console.WriteLine();

        if (writers.Count == 0)
        {
            Console.WriteLine("No writes seen.");
            Console.WriteLine($"  diagnostics: armed on {watch.ThreadsArmed} thread install(s), " +
                              $"{watch.ArmFailures} failure(s), {watch.ReArms} re-arm(s), " +
                              $"watching {watch.WatchedBytes} byte(s), target exited: {watch.TargetExited}.");
            if (watch.ThreadsArmed == 0)
                Console.WriteLine("  • The breakpoint never installed on ANY thread — the attach didn't enumerate " +
                                  "the game's threads (or it exited first). Writes could not have been caught.");
            if (watch.ReArms > 0)
                Console.WriteLine("  • The breakpoint kept getting wiped — the game clears debug registers as " +
                                  "anti-tamper. Re-arming is on, but writes between wipes can still be missed.");
            Console.WriteLine("  • The value may be recomputed for display and never stored here — re-scan for the");
            Console.WriteLine("    address the game actually keeps, or watch a nearby address in the same struct.");
            Console.WriteLine("  • Or nothing changed it yet: 'w 20' watches for another 20 seconds.");
            return;
        }

        Console.WriteLine($"{writers.Count} instruction(s) wrote to this address " +
                          $"({watch.TotalHits} write(s) total):\n");
        foreach (var w in writers)
        {
            string state = patched.ContainsKey(w.Instruction.Address) ? "  [NOPed]" : "";
            Console.WriteLine($"  [{w.Index}] {w.Where}   {w.HitCount}x   thread {w.ThreadId}{state}");
            Console.WriteLine($"      {w.Instruction.ToPattern()}   ({w.Instruction.Length} bytes)");
            Console.WriteLine($"      value after: {w.DescribeValue()}");
        }

        if (watch.UnresolvedHits > 0)
            Console.WriteLine($"\n  ({watch.UnresolvedHits} write(s) could not be traced to an instruction)");
        if (watch.ArmFailures > 0)
            Console.WriteLine($"  (warning: breakpoint failed to install on {watch.ArmFailures} thread(s))");

        Console.WriteLine("\nThe one that DRAINS the value is usually the most frequent. NOP it with 'n <number>',");
        Console.WriteLine("then look at the game — if the drain stopped, 's <number>' saves it as a durable cheat.");
    }

    private static void CommandLoop(WriteWatch watch, ProcessMemory mem, CaptureSession session,
        Dictionary<ulong, PatchedSite> patched)
    {
        PrintHelp();
        while (true)
        {
            Console.Write($"[{session.Count} captured] > ");
            Console.Out.Flush();
            var line = Console.ReadLine()?.Trim();
            if (line is null or "q")
                break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var writers = watch.Snapshot();

            switch (parts)
            {
                case []:
                    Report(watch, patched);
                    break;

                case ["help"]:
                    PrintHelp();
                    break;

                case ["w", var secs] when int.TryParse(secs, out int s):
                    Collect(watch, Math.Clamp(s, 1, 300));
                    Report(watch, patched);
                    break;

                case ["p", var idx] when TryPick(writers, idx, out var forPreview):
                    Preview(mem, forPreview);
                    break;

                case ["n", var idx] when TryPick(writers, idx, out var forNop):
                    Nop(mem, forNop, patched);
                    break;

                case ["r", var idx] when TryPick(writers, idx, out var forRestore):
                    Restore(mem, forRestore.Instruction.Address, patched);
                    break;

                case ["s", var idx] when TryPick(writers, idx, out var forSave):
                    Save(mem, forSave, session);
                    break;

                default:
                    Console.WriteLine("Unrecognised. 'help' lists the commands.");
                    break;
            }
        }
    }

    private static void Preview(ProcessMemory mem, WriterHit hit)
    {
        var patch = WriterPatch.Build(mem, hit.Instruction);
        if (patch is null)
        {
            Console.WriteLine("Couldn't read the code around that instruction.");
            return;
        }

        Console.WriteLine($"  writer:    {hit.Where}   {hit.Instruction.ToPattern()}");
        Console.WriteLine($"  module:    {patch.Module ?? "(none — dynamically generated code)"}");
        Console.WriteLine($"  aob:       {patch.Signature}");
        Console.WriteLine($"  offset:    {patch.PatchOffset}   (patch {patch.PatchBytes.Length} byte(s) with 0x90)");
        Console.WriteLine($"  durable:   {(patch.IsUnique ? "yes — unique match in the module" : $"NO — {patch.Warning}")}");
        Console.WriteLine($"  c#:        {patch.ToCSharp()}");
    }

    private static void Nop(ProcessMemory mem, WriterHit hit, Dictionary<ulong, PatchedSite> patched)
    {
        ulong at = hit.Instruction.Address;
        if (patched.ContainsKey(at))
        {
            Console.WriteLine($"  {hit.Where} is already NOPed — 'r' on it puts it back.");
            return;
        }

        int length = hit.Instruction.Length;
        try
        {
            // Save first, then write — the invariant the whole patching layer is built on.
            var original = mem.ReadBytes(at, length);
            var nops = new byte[length];
            Array.Fill(nops, (byte)0x90);
            mem.WithWritable(at, length, () => mem.WriteBytes(at, nops));
            patched[at] = new PatchedSite(at, original, hit.Where);

            Console.WriteLine($"  NOPed {length} byte(s) at {hit.Where}. Look at the game now.");
            Console.WriteLine("  'r' on the same writer restores it; quitting restores everything.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Couldn't patch: {ex.Message}");
        }
    }

    private static void Restore(ProcessMemory mem, ulong address, Dictionary<ulong, PatchedSite> patched)
    {
        if (!patched.TryGetValue(address, out var site))
        {
            Console.WriteLine($"  0x{address:X} isn't patched.");
            return;
        }

        try
        {
            mem.WithWritable(site.Address, site.Original.Length,
                () => mem.WriteBytes(site.Address, site.Original));
            patched.Remove(address);
            Console.WriteLine($"  Restored {site.Where}.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  Restore FAILED at {site.Where}: {ex.Message} — the game may now be unstable.");
        }
    }

    private static void RestoreAll(ProcessMemory mem, Dictionary<ulong, PatchedSite> patched)
    {
        foreach (ulong address in patched.Keys.ToList())
            Restore(mem, address, patched);
    }

    private static void Save(ProcessMemory mem, WriterHit hit, CaptureSession session)
    {
        var patch = WriterPatch.Build(mem, hit.Instruction);
        if (patch is null)
        {
            Console.WriteLine("Couldn't read the code around that instruction.");
            return;
        }

        if (patch.Warning is { } warning)
        {
            Console.WriteLine($"  WARNING: {warning}.");
            Console.WriteLine("  Saving it anyway so you have the bytes, but check it before shipping.");
        }

        var info = CaptureSession.Prompt();
        if (info is not { } p)
        {
            Console.WriteLine("skipped.");
            return;
        }

        string description = string.IsNullOrEmpty(p.Description)
            ? $"NOPs the instruction that writes this value (found by find-what-writes at {hit.Where})."
            : p.Description;

        session.AddPatch(p.Name, p.Category, description, patch.Signature, patch.PatchOffset, patch.PatchBytes);
        Console.WriteLine($"captured \"{p.Name}\".");
    }

    /// <summary>Resolve a command argument to a writer by its stable <see cref="WriterHit.Index"/>.</summary>
    private static bool TryPick(IReadOnlyList<WriterHit> writers, string index, out WriterHit hit)
    {
        hit = null!;
        if (!int.TryParse(index, out int n))
        {
            Console.WriteLine("Expected a writer number, e.g. 'n 1'.");
            return false;
        }

        var match = writers.FirstOrDefault(w => w.Index == n);
        if (match is null)
        {
            Console.WriteLine($"No writer [{n}]. Press Enter to re-list.");
            return false;
        }

        hit = match;
        return true;
    }

    private static void PrintCurrentValue(ProcessMemory mem, ulong address, int size)
    {
        var buffer = new byte[Math.Clamp(size, 1, 8)];
        if (!mem.TryReadBytes(address, buffer, buffer.Length))
        {
            Console.WriteLine("        (couldn't read the current value)");
            return;
        }
        Console.WriteLine($"        now: {Signature.ToPattern(buffer)}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""

            Commands:
              (Enter)   re-list the writers found so far
              w <secs>  keep watching for another <secs> seconds
              p <n>     preview the durable AOB patch for writer <n>
              n <n>     NOP writer <n> in the live game (reversible)
              r <n>     restore writer <n>
              s <n>     save writer <n> as a cheat in this session
              q         quit (restores every patch, clears breakpoints, detaches)
            """);
    }

    /// <summary>
    /// Sample an address over time with plain reads — no debugger, so no anti-debug risk. Prints
    /// only when the bytes change, which answers "is this the live value or a dead copy?" before
    /// we spend a debugger attach hunting a writer that might not exist.
    /// </summary>
    public static void Poll(ProcessMemory mem, ulong address, int size, int seconds)
    {
        size = Math.Clamp(size, 1, 8);
        Console.WriteLine($"Polling 0x{address:X} ({size} bytes) for {seconds}s — reads only, no attach.");
        Console.WriteLine("Change the value in-game (drive, spend). Prints on every change.\n");

        var last = new byte[size];
        bool have = false;
        int changes = 0;
        var buf = new byte[size];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int samples = 0;

        while (sw.Elapsed.TotalSeconds < seconds)
        {
            if (mem.Process.HasExited) { Console.WriteLine("target exited."); break; }
            if (mem.TryReadBytes(address, buf, size))
            {
                samples++;
                if (!have || !buf.AsSpan().SequenceEqual(last))
                {
                    have = true;
                    buf.CopyTo(last, 0);
                    changes++;
                    Console.WriteLine($"  {sw.Elapsed.TotalSeconds,5:F1}s   {Signature.ToPattern(buf)}   {Interpret(buf)}");
                }
            }
            Thread.Sleep(50);
        }

        Console.WriteLine($"\n{samples} sample(s), {changes} distinct value(s) over {sw.Elapsed.TotalSeconds:F0}s.");
        Console.WriteLine(changes > 1
            ? "→ The value DOES change here — a debugger write breakpoint should be able to catch its writer."
            : "→ The value did NOT change while polling — either it wasn't touched, or this address is a dead copy.");
    }

    private static string Interpret(byte[] v) => v.Length switch
    {
        >= 8 => $"f64 {BitConverter.ToDouble(v):G6}   i64 {BitConverter.ToInt64(v):N0}",
        >= 4 => $"f32 {BitConverter.ToSingle(v):G6}   i32 {BitConverter.ToInt32(v):N0}",
        >= 2 => $"i16 {BitConverter.ToInt16(v)}",
        _ => $"u8 {v[0]}",
    };

    /// <summary>Parse "0x14ABC" / "14ABC" — the form the Capture tab and the scanner print.</summary>
    public static bool TryParseAddress(string text, out ulong address)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[2..];
        return ulong.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address);
    }
}
