using GameCheater.Core.Debugging;
using GameCheater.Core.Memory;

namespace GameCheater.Demo;

/// <summary>
/// Front end for <see cref="PageGuardWatch"/> — "find what writes to this address" that beats
/// hardware-breakpoint-detecting anti-tamper (SnowRunner) by using page protection instead of
/// debug registers. Point it at a value the game recomputes (a damage mirror) to see which
/// instruction stores it and, from the register snapshot, where that value came from.
/// </summary>
public static class FindWritesGuard
{
    public static void Run(ProcessMemory mem, ulong address, int size)
    {
        Console.WriteLine($"Target: 0x{address:X}  ({size} bytes)");
        if (mem.TryGetModuleContaining(address, out var module, out var moduleOffset))
            Console.WriteLine($"        inside {module}+0x{moduleOffset:X}");

        Console.WriteLine("\nAttaching as a debugger and guarding the target's page (no debug registers)...");
        PageGuardWatch watch;
        try
        {
            watch = PageGuardWatch.Start(mem, address, size);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed: {ex.Message}");
            return;
        }

        try
        {
            Console.WriteLine("Attached. Every write to the page faults + single-steps, so expect heavy stutter.\n");
            Collect(watch, 12);
            Report(watch);
            CommandLoop(watch);
        }
        finally
        {
            Console.WriteLine("Restoring page protection and detaching...");
            watch.Dispose();
        }
    }

    private static void Collect(PageGuardWatch watch, int seconds)
    {
        Console.WriteLine($"Collecting for {seconds}s — make the value change in-game (take damage).");
        int before = -1;
        for (int i = 0; i < seconds * 4; i++)
        {
            Thread.Sleep(250);
            if (watch.TargetExited) { Console.WriteLine("\nThe game exited."); return; }
            if (watch.TotalWrites != before)
            {
                before = watch.TotalWrites;
                Console.Write($"\r  {before} write(s) to target, {watch.Snapshot().Count} writer(s), " +
                              $"{watch.ForeignWrites} page-neighbour write(s)…   ");
            }
        }
        Console.WriteLine();
    }

    private static void Report(PageGuardWatch watch)
    {
        var writers = watch.Snapshot();
        Console.WriteLine();
        if (watch.AntiDebugEngaged)
            Console.WriteLine("(anti-debug neutralised via PEB; page-guard uses no debug registers.)");

        if (writers.Count == 0)
        {
            Console.WriteLine("No writes to the exact target address seen.");
            Console.WriteLine($"  ({watch.ForeignWrites} write(s) hit other bytes on the same page.)");
            Console.WriteLine("  • Make sure the value actually changes during the window (take damage while it runs).");
            Console.WriteLine("  • 'w 20' watches longer.");
            return;
        }

        Console.WriteLine($"{writers.Count} instruction(s) wrote to the target ({watch.TotalWrites} write(s) total):\n");
        for (int i = 0; i < writers.Count; i++)
        {
            var w = writers[i];
            Console.WriteLine($"  [{i + 1}] {w.Where}   {w.HitCount}x   thread {w.ThreadId}");
            Console.WriteLine($"      {w.Instruction.ToPattern()}   ({w.Instruction.Length} bytes)");
            Console.WriteLine($"      value after: {DescribeValue(w.LastValue)}");
        }
        Console.WriteLine($"\n  ({watch.ForeignWrites} write(s) to page neighbours were stepped over.)");
        Console.WriteLine("\n'r <n>' shows writer <n>'s registers — the source the value was copied from is usually one of them.");
    }

    private static void CommandLoop(PageGuardWatch watch)
    {
        PrintHelp();
        while (true)
        {
            Console.Write("> ");
            Console.Out.Flush();
            var line = Console.ReadLine()?.Trim();
            if (line is null or "q") break;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var writers = watch.Snapshot();
            switch (parts)
            {
                case []: Report(watch); break;
                case ["help"]: PrintHelp(); break;
                case ["w", var s] when int.TryParse(s, out int secs):
                    Collect(watch, Math.Clamp(secs, 1, 120));
                    Report(watch);
                    break;
                case ["r", var idx] when int.TryParse(idx, out int n) && n >= 1 && n <= writers.Count:
                    var w = writers[n - 1];
                    Console.WriteLine($"  {w.Where}  {w.Instruction.ToPattern()}");
                    Console.WriteLine($"  registers: {w.DescribeRegisters()}");
                    break;
                default: Console.WriteLine("commands: (Enter) re-list, w <secs>, r <n> registers, q"); break;
            }
        }
    }

    private static string DescribeValue(byte[] v) => v.Length switch
    {
        >= 8 => $"i64 {BitConverter.ToInt64(v):N0}   f64 {BitConverter.ToDouble(v):G6}",
        >= 4 => $"i32 {BitConverter.ToInt32(v):N0}   f32 {BitConverter.ToSingle(v):G6}",
        >= 2 => $"i16 {BitConverter.ToInt16(v)}",
        1 => $"u8 {v[0]}",
        _ => "(unreadable)",
    };

    private static void PrintHelp()
    {
        Console.WriteLine("""

            Page-guard find-what-writes (no debug registers — beats HW-breakpoint anti-tamper):
              (Enter)   re-list writers
              w <secs>  watch longer
              r <n>     dump writer <n>'s registers (find the source address the value came from)
              q         quit (restores page protection, detaches)
            """);
    }
}
