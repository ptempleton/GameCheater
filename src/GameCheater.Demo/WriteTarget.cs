using System.Runtime.InteropServices;

namespace GameCheater.Demo;

/// <summary>
/// A stand-in "game" for testing <c>--find-writes</c> without launching one: it allocates an
/// unmanaged 8-byte slot, prints the address, and stores to it on a timer from a worker
/// thread. Everything the real workflow exercises is here — a known address, a known writer,
/// a write coming from a thread other than the main one — so the debugger, the per-thread
/// breakpoint install, and the backward instruction decode can all be verified against a
/// ground truth before trusting them against a game.
/// </summary>
public static partial class WriteTarget
{
    private static volatile bool _stop;

    /// <summary>
    /// A second writer that deliberately lives inside a loaded module rather than in JIT'd
    /// code: it stores its result straight into our slot, so the watcher sees one writer it
    /// can build a durable AOB for (module-resident, like real game code) alongside one it
    /// can't (dynamically generated). Both paths need testing.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryPerformanceCounter(IntPtr performanceCount);

    /// <param name="seconds">Run for a fixed time instead of waiting for Enter. Used when
    /// stdin isn't a console (scripted runs), where ReadLine would return immediately.</param>
    public static void Run(int? seconds = null)
    {
        IntPtr slot = Marshal.AllocHGlobal(8);
        Marshal.WriteInt64(slot, 0, 0);

        Console.WriteLine("find-what-writes test target");
        Console.WriteLine("============================\n");
        Console.WriteLine($"  pid:     {Environment.ProcessId}");
        Console.WriteLine($"  address: {slot.ToInt64():X}   (int64, 8 bytes)\n");
        Console.WriteLine("In another *Administrator* shell, point the watcher at it:\n");
        Console.WriteLine($"  GameCheater.Cli --find-writes {Environment.ProcessId} {slot.ToInt64():X} 8\n");
        Console.WriteLine("This process writes the slot 4x/second from a worker thread.");
        Console.WriteLine(seconds is { } s ? $"Running for {s}s." : "Press Enter to stop.");
        Console.Out.Flush();

        var writer = new Thread(() =>
        {
            long value = 0;
            while (!_stop)
            {
                QueryPerformanceCounter(slot);          // writer inside a module
                Marshal.WriteInt64(slot, 0, ++value);   // writer in JIT'd code
                Thread.Sleep(250);
            }
        })
        {
            IsBackground = true,
            Name = "test-writer",
        };
        writer.Start();

        if (seconds is { } duration)
            Thread.Sleep(TimeSpan.FromSeconds(duration));
        else
            Console.ReadLine();

        _stop = true;
        writer.Join(TimeSpan.FromSeconds(2));
        Console.WriteLine($"final value: {Marshal.ReadInt64(slot, 0)}");
        Marshal.FreeHGlobal(slot);
    }
}
