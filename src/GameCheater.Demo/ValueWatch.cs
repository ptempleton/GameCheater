using GameCheater.Core.Memory;
using GameCheater.Core.Scanning;

namespace GameCheater.Demo;

/// <summary>
/// Interactive value-scan session — the "watch" workflow for value cheats. You seed a first
/// scan, then narrow with a comparison after each in-game (or external-trainer) change:
///   incrementing gold / time-of-day → repeated '+'  (increased)
///   set gold to X                   → '= X'          (exact)
///   item count in hand/hotbar/inv   → '= n' then '-' after using one (decreased)
/// It's our ValueScanner driven from the keyboard, so every one of those cases is covered.
/// </summary>
public static class ValueWatch
{
    public static void Run(ProcessMemory mem, string type, CaptureSession session)
    {
        switch (type.ToLowerInvariant())
        {
            case "int": Loop(new ValueScanner<int>(mem), int.Parse, mem, "int", session); break;
            case "float": Loop(new ValueScanner<float>(mem), float.Parse, mem, "float", session); break;
            case "long": Loop(new ValueScanner<long>(mem), long.Parse, mem, "long", session); break;
            case "short": Loop(new ValueScanner<short>(mem), short.Parse, mem, "short", session); break;
            case "byte": Loop(new ValueScanner<byte>(mem), byte.Parse, mem, "byte", session); break;
            case "double": Loop(new ValueScanner<double>(mem), double.Parse, mem, "double", session); break;
            default: Console.WriteLine("type must be: int | float | long | short | byte | double"); break;
        }
    }

    private static void Loop<T>(ValueScanner<T> s, Func<string, T> parse,
        ProcessMemory mem, string type, CaptureSession session) where T : unmanaged, IComparable<T>
    {
        PrintHelp();
        bool firstDone = false;

        while (true)
        {
            Console.Write($"[{s.CandidateCount:N0} candidates] > ");
            var line = Console.ReadLine()?.Trim();
            if (line is null or "q" or "quit") break;
            if (line.Length == 0) continue;

            try
            {
                switch (line)
                {
                    case "?":
                        Console.WriteLine("scanning writable memory (unknown initial)...");
                        Console.WriteLine($"{s.FirstScanUnknown():N0} candidates. Now change the value in-game, then '-' or '+' to narrow.");
                        firstDone = true;
                        break;
                    case "+": Console.WriteLine($"{s.NextScanIncreased():N0} candidates (increased)."); break;
                    case "-": Console.WriteLine($"{s.NextScanDecreased():N0} candidates (decreased)."); break;
                    case "~": Console.WriteLine($"{s.NextScanChanged():N0} candidates (changed)."); break;
                    case "==": Console.WriteLine($"{s.NextScanUnchanged():N0} candidates (unchanged)."); break;
                    case "list":
                        if (s.InSnapshotMode)
                        {
                            Console.WriteLine("too many to list yet — narrow first: change the value in-game, then '-' or '+'.");
                            break;
                        }
                        for (int i = 0; i < Math.Min(25, s.Count); i++)
                            Console.WriteLine($"   [{i + 1}] 0x{s.Results[i]:X} = {s.ReadCurrent(s.Results[i])}");
                        if (s.Count > 25) Console.WriteLine($"   … and {s.Count - 25:N0} more");
                        break;
                    case "help": PrintHelp(); break;
                    default:
                        if (line.StartsWith("save"))
                        {
                            SaveCandidate(s, mem, type, session, line);
                        }
                        else if (line.StartsWith('='))
                        {
                            var value = parse(line[1..].Trim());
                            int n = firstDone ? s.NextScanExact(value) : s.FirstScan(value);
                            firstDone = true;
                            Console.WriteLine($"{n:N0} candidates == {value}.");
                        }
                        else
                        {
                            Console.WriteLine("unknown command — type 'help'");
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error: {ex.Message}");
            }
        }
    }

    // `save [n]` — name/describe candidate n (1-based; defaults to 1) and add it to the session.
    private static void SaveCandidate<T>(ValueScanner<T> s, ProcessMemory mem, string type,
        CaptureSession session, string line) where T : unmanaged, IComparable<T>
    {
        if (s.Count == 0) { Console.WriteLine("no candidates to save — narrow the scan first."); return; }

        int index = 1;
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && int.TryParse(parts[1], out int n)) index = n;
        if (index < 1 || index > s.Count) { Console.WriteLine($"pick 1..{Math.Min(s.Count, 25)} (see 'list')."); return; }

        ulong address = s.Results[index - 1];
        Console.WriteLine($"saving 0x{address:X} = {s.ReadCurrent(address)}");
        var info = CaptureSession.Prompt();
        if (info is not { } p) { Console.WriteLine("skipped."); return; }

        session.AddValue(p.Name, p.Category, p.Description, type, mem, address);
        Console.WriteLine($"added \"{p.Name}\" ({session.Count} captured this session).");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Commands:
              = N     first scan for value N, or narrow to == N afterwards (e.g. '= 1200')
              ?       first scan, unknown value (then narrow with +/-/~)
              +       narrow to values that INCREASED  (gold ticking up, time of day)
              -       narrow to values that DECREASED  (used an item, took damage)
              ~       narrow to values that CHANGED
              ==      narrow to values that are UNCHANGED
              list    show current candidates (numbered)
              save[n] name+describe candidate n and capture it (default n=1)
              q       quit
            Typical: '= 1200' → spend → '= 1150' … until a few remain, then 'save 1'.
            Unknown: '?' → act → +/-.
            """);
    }
}
