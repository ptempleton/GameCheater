using System.Runtime.InteropServices;
using GameCheater.Core.Memory;
using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>
/// "Find what writes to this address" — the self-contained equivalent of Cheat Engine's
/// debugger, and the piece that makes code cheats possible without any external tool.
///
/// How it works: attach to the game as a real debugger, put a hardware *write* breakpoint on
/// the target address in every thread, and pump the debug event loop. Each time the game
/// stores to that address the CPU traps, and the trapping thread's RIP tells us which code
/// did it. Because the trap fires after the store retires, RIP points at the *next*
/// instruction, so <see cref="X64Decoder.FindWriterEndingAt"/> walks back to the real writer.
/// NOP that instruction and the game stops consuming the value — which is the only way to
/// cheat a continuously-recomputed value like SnowRunner's fuel, where freezing the number
/// achieves nothing because the number is only a mirror.
///
/// Constraints worth knowing:
/// <list type="bullet">
/// <item>Windows-only, x64-only, and requires Administrator (SeDebugPrivilege).</item>
/// <item>The game is frozen for the duration of every single hit. A breakpoint on a
///   hot address will make it stutter badly — watch, capture, then stop.</item>
/// <item>Only one debugger may be attached to a process at a time.</item>
/// <item>Single-player only: never attach to a process under EAC/BattlEye.</item>
/// </list>
///
/// Everything is torn down on <see cref="Dispose"/>: breakpoints are cleared from every
/// thread *before* detaching, which is not optional — a debug register left armed with no
/// debugger listening raises an unhandled exception and kills the game.
/// </summary>
public sealed class WriteWatch : IDisposable
{
    private readonly ProcessMemory _memory;
    private readonly uint _processId;
    private readonly ulong _address;
    private readonly int _lengthEncoding;
    private readonly int _slot;
    private readonly int _valueSize;
    private readonly bool _clearPeb;
    private readonly bool _periodicReArm;
    private readonly bool _hideDebugRegisters;

    private readonly Dictionary<uint, IntPtr> _threads = new();
    private readonly List<IntPtr> _ownedThreadHandles = new();
    private readonly Dictionary<ulong, WriterHit> _hits = new();
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Thread? _loop;
    private volatile bool _stop;
    private volatile bool _targetExited;
    private bool _sawInitialBreakpoint;
    private string? _startError;
    private int _totalHits;
    private int _unresolvedHits;
    private int _armFailures;
    private int _armedOk;
    private int _reArms;
    private volatile bool _antiDebugEngaged;
    private volatile bool _debugRegistersHidden;
    private DebugRegisterHider? _debugRegisterHider;
    private string? _teardownError;
    private bool _disposed;

    private WriteWatch(ProcessMemory memory, ulong address, int size, int slot, bool clearPeb,
        bool periodicReArm, bool hideDebugRegisters)
    {
        _memory = memory;
        _processId = (uint)memory.Process.Id;
        _address = address;
        _slot = slot;
        _lengthEncoding = HardwareBreakpoint.EncodeLength(address, size);
        _valueSize = Math.Clamp(size, 1, 8);
        _clearPeb = clearPeb;
        _periodicReArm = periodicReArm;
        _hideDebugRegisters = hideDebugRegisters;
    }

    /// <summary>Bytes the CPU is actually watching. Less than the requested size when the
    /// address isn't aligned enough for a wider breakpoint — writes to the uncovered tail
    /// won't be caught.</summary>
    public int WatchedBytes => HardwareBreakpoint.DecodeLength(_lengthEncoding);

    /// <summary>Total breakpoint trips, including ones we couldn't resolve to an instruction.</summary>
    public int TotalHits => Volatile.Read(ref _totalHits);

    /// <summary>Trips whose writer couldn't be decoded (unreadable code page, or an encoding
    /// the length decoder doesn't cover). A handful is normal; a flood means something's off.</summary>
    public int UnresolvedHits => Volatile.Read(ref _unresolvedHits);

    /// <summary>Threads the breakpoint could not be installed on. Non-zero means writes from
    /// those threads are invisible to this session.</summary>
    public int ArmFailures => Volatile.Read(ref _armFailures);

    /// <summary>How many times the breakpoint had to be re-installed because the target had
    /// wiped it. A climbing count means the game clears debug registers as anti-tamper — which
    /// would otherwise silently disable the watch and look like "no writes".</summary>
    public int ReArms => Volatile.Read(ref _reArms);

    /// <summary>Threads currently armed with the watch (0 after teardown).</summary>
    public int ThreadCount
    {
        get { lock (_gate) return _threads.Count; }
    }

    /// <summary>Total successful breakpoint installs over the session — a persistent count that
    /// survives teardown, so "did we arm anything at all?" is answerable after the game exits.</summary>
    public int ThreadsArmed => Volatile.Read(ref _armedOk);

    /// <summary>True once the game process has exited out from under us.</summary>
    public bool TargetExited => _targetExited;

    /// <summary>
    /// True when the PEB-clearing anti-anti-debug fired — i.e. attaching set a debugger flag
    /// that we then scrubbed. Signals that the game watches for debuggers, so a bare attach
    /// (or an external debugger) would have been detected.
    /// </summary>
    public bool AntiDebugEngaged => _antiDebugEngaged;

    /// <summary>True when the target-side NtGetContextThread hook was installed successfully.</summary>
    public bool DebugRegistersHidden => _debugRegistersHidden;

    /// <summary>A cleanup failure that occurred while restoring the target or detaching.</summary>
    public string? TeardownError => _teardownError;

    /// <summary>
    /// Raised the first time a given instruction is seen writing. Fires on the debug loop
    /// thread *while the game is frozen* — do only cheap work here (a UI must marshal).
    /// </summary>
    public event Action<WriterHit>? WriterDiscovered;

    /// <summary>
    /// Attach and start watching writes to <paramref name="address"/>.
    /// Throws if the debugger can't attach (not elevated, already debugged, or not Windows).
    ///
    /// <paramref name="clearPebDebugFlags"/> scrubs the PEB debugger tells on attach and keeps
    /// scrubbing them — on by default because it is harmless to an unprotected game and defeats
    /// the common <c>IsDebuggerPresent</c>-style check that would otherwise make the target
    /// quietly exit the instant we attach (SnowRunner does exactly this). It cannot defeat a
    /// kernel-side check; see <see cref="AntiDebug"/>.
    /// </summary>
    public static WriteWatch Start(ProcessMemory memory, ulong address, int size = 4, int slot = 0,
        bool clearPebDebugFlags = true, bool periodicReArm = false, bool hideDebugRegisters = false)
    {
        ArgumentNullException.ThrowIfNull(memory);
        if (slot is < 0 or >= HardwareBreakpoint.SlotCount)
            throw new ArgumentOutOfRangeException(nameof(slot), "Only debug slots 0–3 exist.");

        var watch = new WriteWatch(memory, address, size, slot, clearPebDebugFlags, periodicReArm,
            hideDebugRegisters);
        watch._loop = new Thread(watch.Pump)
        {
            IsBackground = true,
            Name = $"WriteWatch 0x{address:X}",
        };
        watch._loop.Start();

        // The whole debug session is thread-affine, so the caller waits here for the attach
        // to succeed or fail on that thread rather than getting a half-built object back.
        watch._ready.Wait();
        if (watch._startError is { } error)
        {
            watch.Dispose();
            throw new InvalidOperationException(error);
        }
        return watch;
    }

    /// <summary>Writers found so far, busiest first. Safe to call from any thread.</summary>
    public IReadOnlyList<WriterHit> Snapshot()
    {
        lock (_gate)
            return _hits.Values.OrderByDescending(h => h.HitCount).ToList();
    }

    // --- the debug loop (everything below runs on _loop) ---

    private void Pump()
    {
        if (!Win32.DebugActiveProcess(_processId))
        {
            _startError = $"DebugActiveProcess failed for pid {_processId} " +
                          $"(err {Marshal.GetLastWin32Error()}). Run as Administrator, and check " +
                          "nothing else (Cheat Engine, a debugger) is already attached.";
            _ready.Set();
            return;
        }

        // Do this immediately: without it, the game is killed if this process ever exits.
        Win32.DebugSetProcessKillOnExit(false);

        var upkeep = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!_stop)
            {
                if (Win32.WaitForDebugEvent(out var debugEvent, 50))
                {
                    bool isInitialEvent = debugEvent.DebugEventCode == Win32.CREATE_PROCESS_DEBUG_EVENT;
                    uint status = Handle(ref debugEvent);
                    Win32.ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, status);
                    if (isInitialEvent)
                        _ready.Set();
                }
                else if (Marshal.GetLastWin32Error() != Win32.ERROR_SEM_TIMEOUT)
                {
                    break;   // a real failure — don't spin on it
                }

                // Re-scrub between events: the game re-reads the PEB flags on its own loop, so
                // a one-shot clear at attach isn't enough to stay hidden.
                MaybeClearPeb();

                // Optional: re-install the breakpoint if the game cleared it. OFF by default —
                // it suspends every thread, which some games treat as a tamper signal (SnowRunner
                // self-exits). Only worth enabling once we know a game clears debug registers.
                if (_periodicReArm && upkeep.ElapsedMilliseconds >= 400)
                {
                    ReArmAll();
                    upkeep.Restart();
                }
            }
        }
        finally
        {
            if (!_ready.IsSet)
            {
                _startError ??= "The debug session ended before the initial process event arrived.";
                _ready.Set();
            }
            Detach();
        }
    }

    /// <summary>Re-install the breakpoint on any thread that lost it, counting the repairs.</summary>
    private void ReArmAll()
    {
        List<IntPtr> threads;
        lock (_gate)
            threads = _threads.Values.ToList();

        foreach (var thread in threads)
        {
            if (HardwareBreakpoint.ReArmIfCleared(thread, _slot, _address,
                    BreakpointCondition.Write, _lengthEncoding))
            {
                Interlocked.Increment(ref _reArms);
            }
        }
    }

    /// <summary>Force the PEB debugger flags back to "not debugged", noting when a clear was
    /// actually needed (which tells us the game is watching for us).</summary>
    private void MaybeClearPeb()
    {
        if (!_clearPeb)
            return;
        if (AntiDebug.Read(_memory) is { LooksDebugged: true })
            _antiDebugEngaged = true;
        AntiDebug.Clear(_memory);
    }

    private uint Handle(ref Win32.DEBUG_EVENT e)
    {
        switch (e.DebugEventCode)
        {
            case Win32.CREATE_PROCESS_DEBUG_EVENT:
                // The union carries hFile at offset 0 (ours to close) and hThread at 16.
                if (e.UnionHandle != IntPtr.Zero)
                    Win32.CloseHandle(e.UnionHandle);
                // This is the first event — the target is frozen and hasn't run since attach
                // set its debugger flag. Scrub it here, before we let it resume, so an
                // anti-debug poll never sees a debugged PEB.
                MaybeClearPeb();
                if (_hideDebugRegisters)
                {
                    try
                    {
                        _debugRegisterHider = DebugRegisterHider.TryInstall(_memory);
                        if (_debugRegisterHider is null)
                            _startError = "Debug-register hiding was requested, but the " +
                                          "NtGetContextThread hook could not be installed safely.";
                        else
                            _debugRegistersHidden = true;
                    }
                    catch (Exception ex)
                    {
                        _startError = $"Debug-register hiding failed: {ex.Message}";
                    }

                    if (_startError is not null)
                    {
                        _stop = true;
                        return Win32.DBG_CONTINUE;
                    }
                }
                Track(e.ThreadId, e.CreateProcessThread);
                return Win32.DBG_CONTINUE;

            case Win32.CREATE_THREAD_DEBUG_EVENT:
                // A breakpoint is per-thread, so every thread the game spawns while we're
                // watching has to be armed too, or writes from it go unseen.
                Track(e.ThreadId, e.UnionHandle);
                return Win32.DBG_CONTINUE;

            case Win32.EXIT_THREAD_DEBUG_EVENT:
                lock (_gate)
                    _threads.Remove(e.ThreadId);   // the system closes the handle for us
                return Win32.DBG_CONTINUE;

            case Win32.LOAD_DLL_DEBUG_EVENT:
                if (e.UnionHandle != IntPtr.Zero)
                    Win32.CloseHandle(e.UnionHandle);
                return Win32.DBG_CONTINUE;

            case Win32.EXIT_PROCESS_DEBUG_EVENT:
                _targetExited = true;
                _stop = true;
                return Win32.DBG_CONTINUE;

            case Win32.EXCEPTION_DEBUG_EVENT:
                return HandleException(ref e);

            default:
                return Win32.DBG_CONTINUE;
        }
    }

    private uint HandleException(ref Win32.DEBUG_EVENT e)
    {
        // Anything but a definite "not mine" gets swallowed. If we can't read the thread's
        // Dr6 to decide, assume it was our watchpoint: passing a stray single-step back to a
        // game that isn't expecting one kills it, and we're the reason single-steps exist here.
        if (e.ExceptionCode == Win32.EXCEPTION_SINGLE_STEP && OnBreakpoint(e.ThreadId) != false)
            return Win32.DBG_CONTINUE;

        // The OS injects one int3 into the target when a debugger attaches. Swallow that one;
        // pass everything else through untouched so the game's own handlers behave normally.
        if (e.ExceptionCode == Win32.EXCEPTION_BREAKPOINT && !_sawInitialBreakpoint)
        {
            _sawInitialBreakpoint = true;
            return Win32.DBG_CONTINUE;
        }

        return Win32.DBG_EXCEPTION_NOT_HANDLED;
    }

    /// <summary>
    /// Record a breakpoint trip. Returns true when it was ours, false when Dr6 proves it
    /// wasn't, and null when we couldn't tell (caller should still swallow it).
    /// </summary>
    private bool? OnBreakpoint(uint threadId)
    {
        IntPtr thread = ResolveThread(threadId);
        if (thread == IntPtr.Zero)
            return null;

        using var context = new ThreadContextBuffer();
        if (!context.Load(thread, Win32.CONTEXT_CONTROL | Win32.CONTEXT_DEBUG_REGISTERS))
            return null;

        if (!HardwareBreakpoint.DidFire(context.Dr6, _slot))
            return false;   // somebody else's single-step — let the game handle it

        ulong rip = context.Rip;

        // Dr6 is sticky: the CPU never clears it, so leaving it set makes the next trap
        // look like it came from every slot that has ever fired.
        context.Dr6 = 0;
        context.Store(thread, Win32.CONTEXT_DEBUG_REGISTERS);

        Interlocked.Increment(ref _totalHits);

        var writer = ResolveWriter(rip);
        if (writer is null)
        {
            Interlocked.Increment(ref _unresolvedHits);
            return true;   // still ours — swallowing it keeps the game alive
        }

        var value = new byte[_valueSize];
        if (!_memory.TryReadBytes(_address, value, _valueSize))
            value = Array.Empty<byte>();

        WriterHit? discovered = null;
        lock (_gate)
        {
            if (_hits.TryGetValue(writer.Address, out var existing))
            {
                existing.HitCount++;
                existing.LastValue = value;
            }
            else
            {
                _memory.TryGetModuleContaining(writer.Address, out string module, out ulong offset);
                discovered = new WriterHit
                {
                    Index = _hits.Count + 1,
                    Instruction = writer,
                    Module = string.IsNullOrEmpty(module) ? null : module,
                    ModuleOffset = offset,
                    ThreadId = threadId,
                    HitCount = 1,
                    LastValue = value,
                };
                _hits[writer.Address] = discovered;
            }
        }

        if (discovered is not null)
            WriterDiscovered?.Invoke(discovered);

        return true;
    }

    /// <summary>
    /// Read the code just before the trapped RIP and decode backwards to the storing
    /// instruction. The window shrinks on failure because RIP can sit near the start of a
    /// page whose predecessor isn't mapped.
    /// </summary>
    private X64Instruction? ResolveWriter(ulong rip)
    {
        ReadOnlySpan<int> windows = [64, 32, 16];
        foreach (int window in windows)
        {
            if (rip < (ulong)window)
                continue;

            ulong start = rip - (ulong)window;
            var code = new byte[window];
            if (!_memory.TryReadBytes(start, code, window))
                continue;

            var writer = X64Decoder.FindWriterEndingAt(code, start, rip,
                maxLookback: Math.Min(window, 48));
            if (writer is not null)
                return writer;
        }
        return null;
    }

    private IntPtr ResolveThread(uint threadId)
    {
        lock (_gate)
        {
            if (_threads.TryGetValue(threadId, out var known))
                return known;
        }

        // Shouldn't happen — we see a CREATE_THREAD event for every thread — but a handle we
        // opened ourselves is better than passing an unhandled single-step to the game.
        IntPtr opened = Win32.OpenThread(Win32.ThreadAccess.Breakpoint, false, threadId);
        if (opened == IntPtr.Zero)
            return IntPtr.Zero;

        lock (_gate)
        {
            _ownedThreadHandles.Add(opened);
            _threads[threadId] = opened;
        }
        return opened;
    }

    /// <summary>Record a thread and arm it. No suspend needed: inside a debug event the
    /// whole process is already stopped.</summary>
    private void Track(uint threadId, IntPtr handle)
    {
        if (handle == IntPtr.Zero)
            return;

        lock (_gate)
            _threads[threadId] = handle;

        if (HardwareBreakpoint.Arm(handle, _slot, _address, BreakpointCondition.Write,
                _lengthEncoding, suspend: false))
        {
            Interlocked.Increment(ref _armedOk);
        }
        else
        {
            Interlocked.Increment(ref _armFailures);
        }
    }

    private void Detach()
    {
        // Order matters: clear every breakpoint FIRST. Detaching with a debug register still
        // armed leaves the game raising single-step exceptions that nothing will handle.
        if (!_targetExited)
        {
            List<IntPtr> threads;
            lock (_gate)
                threads = _threads.Values.ToList();

            foreach (var thread in threads)
                HardwareBreakpoint.Disarm(thread, _slot, suspend: true);

            // Suspend all tracked threads together while restoring the multi-byte ntdll stub;
            // this prevents another thread from executing a half-restored instruction stream.
            var suspended = new List<IntPtr>();
            foreach (var thread in threads)
            {
                if (Win32.SuspendThread(thread) != uint.MaxValue)
                    suspended.Add(thread);
            }

            try
            {
                _debugRegisterHider?.Dispose();
                _debugRegisterHider = null;
                _debugRegistersHidden = false;
            }
            catch (Exception ex)
            {
                // The hider deliberately keeps its detour allocated when restoration fails,
                // so detaching cannot leave the target jumping into released memory.
                _teardownError = $"Failed to restore debug-register hook: {ex.Message}";
            }
            finally
            {
                foreach (var thread in suspended)
                    Win32.ResumeThread(thread);
            }

            if (!Win32.DebugActiveProcessStop(_processId) && _teardownError is null)
                _teardownError = $"DebugActiveProcessStop failed (err {Marshal.GetLastWin32Error()}).";
        }

        lock (_gate)
        {
            foreach (var handle in _ownedThreadHandles)
                Win32.CloseHandle(handle);
            _ownedThreadHandles.Clear();
            _threads.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _stop = true;
        _loop?.Join(TimeSpan.FromSeconds(5));
        _loop = null;
        _ready.Dispose();
    }
}
