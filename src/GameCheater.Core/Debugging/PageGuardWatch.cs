using System.Runtime.InteropServices;
using GameCheater.Core.Memory;
using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>One instruction caught writing to the watched address by the page-guard watch.</summary>
public sealed class GuardHit
{
    public required X64Instruction Instruction { get; init; }
    public ulong Address => Instruction.Address;
    public string? Module { get; init; }
    public ulong ModuleOffset { get; init; }
    public uint ThreadId { get; init; }
    public int HitCount { get; internal set; }

    /// <summary>The watched bytes right after the write.</summary>
    public byte[] LastValue { get; internal set; } = Array.Empty<byte>();

    /// <summary>General-purpose registers at the fault (Rax,Rcx,Rdx,Rbx,Rsp,Rbp,Rsi,Rdi,R8..R15).
    /// The source a mirror was copied from usually sits in one of these.</summary>
    public ulong[] Registers { get; internal set; } = Array.Empty<ulong>();

    public string Where => Module is null ? $"0x{Address:X}" : $"{Module}+0x{ModuleOffset:X}";

    private static readonly string[] RegNames =
        { "rax", "rcx", "rdx", "rbx", "rsp", "rbp", "rsi", "rdi", "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15" };

    public string DescribeRegisters()
    {
        if (Registers.Length < 16) return "(none)";
        var parts = new List<string>();
        for (int i = 0; i < 16; i++)
            parts.Add($"{RegNames[i]}=0x{Registers[i]:X}");
        return string.Join("  ", parts);
    }
}

/// <summary>
/// "Find what writes to this address" WITHOUT debug registers — the anti-tamper-proof cousin of
/// <see cref="WriteWatch"/>. Instead of a hardware breakpoint (which SnowRunner detects by reading
/// its own Dr registers and then self-exits), this strips write permission from the target's
/// memory page. Any store to the page raises an access violation the debugger catches; the
/// faulting RIP is the writing instruction *directly* (the store faulted before completing, so no
/// backward decode is needed). To let the game proceed we briefly restore the page, single-step
/// the one instruction, then re-strip — invisible to any debug-register check.
///
/// Trade-offs vs the HW version: the whole 4 KB page is guarded, so writes to neighbours also
/// fault and get stepped over (more overhead, and a hot page stutters); and during each
/// single-step the page is briefly writable, so a store from another core in that window can be
/// missed. Fine for a few-second capture. Still Windows-only, x64, Administrator, single-player.
/// </summary>
public sealed class PageGuardWatch : IDisposable
{
    private const int PageSize = 0x1000;

    private readonly ProcessMemory _memory;
    private readonly uint _processId;
    private readonly ulong _address;
    private readonly int _valueSize;
    private readonly ulong _pageStart;
    private readonly nuint _pageSpan;
    private readonly bool _clearPeb;

    private readonly Dictionary<ulong, GuardHit> _hits = new();
    private readonly Dictionary<uint, IntPtr> _threadHandles = new();
    private readonly HashSet<uint> _stepping = new();
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _ready = new(false);

    private Win32.MemoryProtection _originalProtect;
    private Thread? _loop;
    private volatile bool _stop;
    private volatile bool _targetExited;
    private volatile bool _antiDebugEngaged;
    private bool _sawInitialBreakpoint;
    private bool _guardActive;
    private string? _startError;
    private int _totalWrites;
    private int _foreignWrites;
    private bool _disposed;

    private PageGuardWatch(ProcessMemory memory, ulong address, int size, bool clearPeb)
    {
        _memory = memory;
        _processId = (uint)memory.Process.Id;
        _address = address;
        _valueSize = Math.Clamp(size, 1, 8);
        _clearPeb = clearPeb;
        _pageStart = address & ~(ulong)(PageSize - 1);
        ulong end = address + (ulong)_valueSize;
        ulong pageEnd = (end + PageSize - 1) & ~(ulong)(PageSize - 1);
        _pageSpan = (nuint)(pageEnd - _pageStart);
    }

    /// <summary>Writes to the exact target address (what you're hunting).</summary>
    public int TotalWrites => Volatile.Read(ref _totalWrites);

    /// <summary>Writes to OTHER addresses on the same page (stepped over, not recorded). High = a
    /// busy page; the target's writer is still captured, just amid more overhead.</summary>
    public int ForeignWrites => Volatile.Read(ref _foreignWrites);

    public bool TargetExited => _targetExited;
    public bool AntiDebugEngaged => _antiDebugEngaged;

    public event Action<GuardHit>? WriterDiscovered;

    public static PageGuardWatch Start(ProcessMemory memory, ulong address, int size = 4, bool clearPebDebugFlags = true)
    {
        ArgumentNullException.ThrowIfNull(memory);
        var watch = new PageGuardWatch(memory, address, size, clearPebDebugFlags);
        watch._loop = new Thread(watch.Pump) { IsBackground = true, Name = $"PageGuardWatch 0x{address:X}" };
        watch._loop.Start();
        watch._ready.Wait();
        if (watch._startError is { } error)
        {
            watch.Dispose();
            throw new InvalidOperationException(error);
        }
        return watch;
    }

    public IReadOnlyList<GuardHit> Snapshot()
    {
        lock (_gate)
            return _hits.Values.OrderByDescending(h => h.HitCount).ToList();
    }

    private void Pump()
    {
        if (!Win32.DebugActiveProcess(_processId))
        {
            _startError = $"DebugActiveProcess failed for pid {_processId} (err {Marshal.GetLastWin32Error()}). " +
                          "Run as Administrator; close any other attached debugger.";
            _ready.Set();
            return;
        }
        Win32.DebugSetProcessKillOnExit(false);
        _ready.Set();

        try
        {
            while (!_stop)
            {
                if (Win32.WaitForDebugEvent(out var e, 50))
                {
                    uint status = Handle(ref e);
                    Win32.ContinueDebugEvent(e.ProcessId, e.ThreadId, status);
                }
                else if (Marshal.GetLastWin32Error() != Win32.ERROR_SEM_TIMEOUT)
                {
                    break;
                }
                if (_clearPeb)
                {
                    if (AntiDebug.Read(_memory) is { LooksDebugged: true })
                        _antiDebugEngaged = true;
                    AntiDebug.Clear(_memory);
                }
            }
        }
        finally
        {
            Detach();
        }
    }

    private uint Handle(ref Win32.DEBUG_EVENT e)
    {
        switch (e.DebugEventCode)
        {
            case Win32.CREATE_PROCESS_DEBUG_EVENT:
                if (e.UnionHandle != IntPtr.Zero) Win32.CloseHandle(e.UnionHandle);
                if (_clearPeb) AntiDebug.Clear(_memory);
                ArmGuard();   // the target is frozen at attach — strip the page now, before it runs
                return Win32.DBG_CONTINUE;

            case Win32.LOAD_DLL_DEBUG_EVENT:
                if (e.UnionHandle != IntPtr.Zero) Win32.CloseHandle(e.UnionHandle);
                return Win32.DBG_CONTINUE;

            case Win32.EXIT_THREAD_DEBUG_EVENT:
                CloseThread(e.ThreadId);
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
        if (e.ExceptionCode == Win32.EXCEPTION_BREAKPOINT && !_sawInitialBreakpoint)
        {
            _sawInitialBreakpoint = true;
            return Win32.DBG_CONTINUE;
        }

        if (e.ExceptionCode == Win32.EXCEPTION_ACCESS_VIOLATION
            && e.ExceptionInformation0 == 1 // write
            && e.ExceptionInformation1 >= _pageStart && e.ExceptionInformation1 < _pageStart + _pageSpan)
        {
            return OnWriteFault(e.ThreadId, e.ExceptionInformation1);
        }

        if (e.ExceptionCode == Win32.EXCEPTION_SINGLE_STEP)
        {
            lock (_gate)
            {
                if (_stepping.Remove(e.ThreadId))
                {
                    SetGuard(strip: true);          // the write finished; re-arm the page
                    ClearTrapFlag(e.ThreadId);
                    return Win32.DBG_CONTINUE;
                }
            }
        }

        return Win32.DBG_EXCEPTION_NOT_HANDLED; // not ours — let the game's handlers see it
    }

    private uint OnWriteFault(uint threadId, ulong faultAddress)
    {
        bool onTarget = faultAddress >= _address && faultAddress < _address + (ulong)_valueSize;
        if (onTarget)
            Interlocked.Increment(ref _totalWrites);
        else
            Interlocked.Increment(ref _foreignWrites);

        IntPtr thread = ThreadHandle(threadId);
        if (thread == IntPtr.Zero)
            return Win32.DBG_CONTINUE; // can't step it; swallow so the game survives (write is lost)

        using var context = new ThreadContextBuffer();
        GuardHit? discovered = null;
        if (context.Load(thread, Win32.CONTEXT_CONTROL | Win32.CONTEXT_INTEGER) && onTarget)
            discovered = RecordWriter(context, threadId);

        // Let the faulting store complete: open the page, single-step exactly this instruction.
        SetGuard(strip: false);
        SetTrapFlag(threadId);
        lock (_gate)
            _stepping.Add(threadId);

        if (discovered is not null)
            WriterDiscovered?.Invoke(discovered);
        return Win32.DBG_CONTINUE;
    }

    private GuardHit? RecordWriter(ThreadContextBuffer context, uint threadId)
    {
        ulong rip = context.Rip;
        var code = new byte[16];
        if (!_memory.TryReadBytes(rip, code, code.Length))
            return null;
        var instruction = X64Decoder.Decode(code, 0, rip);
        if (instruction is null)
            return null;

        var value = new byte[_valueSize];
        if (!_memory.TryReadBytes(_address, value, _valueSize))
            value = Array.Empty<byte>();
        var regs = context.GetIntegerRegisters();

        lock (_gate)
        {
            if (_hits.TryGetValue(rip, out var existing))
            {
                existing.HitCount++;
                existing.LastValue = value;
                existing.Registers = regs;
                return null;
            }

            _memory.TryGetModuleContaining(rip, out string module, out ulong offset);
            var hit = new GuardHit
            {
                Instruction = instruction,
                Module = string.IsNullOrEmpty(module) ? null : module,
                ModuleOffset = offset,
                ThreadId = threadId,
                HitCount = 1,
                LastValue = value,
                Registers = regs,
            };
            _hits[rip] = hit;
            return hit;
        }
    }

    // --- page protection ---

    private void ArmGuard()
    {
        if (_guardActive) return;
        if (Win32.VirtualProtectEx(_memory.Handle, (IntPtr)_pageStart, _pageSpan, StrippedProtect(), out var old))
        {
            _originalProtect = old;
            _guardActive = true;
        }
    }

    private void SetGuard(bool strip)
    {
        var target = strip ? StrippedProtect() : _originalProtect;
        Win32.VirtualProtectEx(_memory.Handle, (IntPtr)_pageStart, _pageSpan, target, out _);
        _guardActive = strip;
    }

    /// <summary>The page's protection with write removed but read/execute preserved.</summary>
    private Win32.MemoryProtection StrippedProtect()
    {
        // On the very first arm we don't yet know the page's protection — query it inline.
        if (_originalProtect == 0
            && Win32.VirtualQueryEx(_memory.Handle, (IntPtr)_pageStart, out var mbi, (nuint)Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>()) != 0)
        {
            _originalProtect = (Win32.MemoryProtection)mbi.Protect;
        }
        // Execute bits are 0x10/0x20/0x40/0x80; keep execute if present, otherwise plain read-only.
        bool executable = ((uint)_originalProtect & 0xF0) != 0;
        return executable ? Win32.MemoryProtection.ExecuteRead : Win32.MemoryProtection.ReadOnly;
    }

    private void SetTrapFlag(uint threadId) => WithContext(threadId, ctx => ctx.EFlags |= ThreadContextBuffer.TrapFlag);
    private void ClearTrapFlag(uint threadId) => WithContext(threadId, ctx => ctx.EFlags &= ~ThreadContextBuffer.TrapFlag);

    private void WithContext(uint threadId, Action<ThreadContextBuffer> edit)
    {
        IntPtr thread = ThreadHandle(threadId);
        if (thread == IntPtr.Zero) return;
        using var context = new ThreadContextBuffer();
        if (!context.Load(thread, Win32.CONTEXT_CONTROL)) return;
        edit(context);
        context.Store(thread, Win32.CONTEXT_CONTROL);
    }

    private IntPtr ThreadHandle(uint threadId)
    {
        lock (_gate)
        {
            if (_threadHandles.TryGetValue(threadId, out var known))
                return known;
        }
        IntPtr opened = Win32.OpenThread(Win32.ThreadAccess.Breakpoint, false, threadId);
        if (opened == IntPtr.Zero)
            return IntPtr.Zero;
        lock (_gate)
            _threadHandles[threadId] = opened;
        return opened;
    }

    private void CloseThread(uint threadId)
    {
        lock (_gate)
        {
            if (_threadHandles.Remove(threadId, out var handle))
                Win32.CloseHandle(handle);
            _stepping.Remove(threadId);
        }
    }

    private void Detach()
    {
        // Restore the page BEFORE detaching, or the game keeps faulting into a debugger that's gone.
        if (!_targetExited && _guardActive)
            SetGuard(strip: false);

        if (!_targetExited)
            Win32.DebugActiveProcessStop(_processId);

        lock (_gate)
        {
            foreach (var handle in _threadHandles.Values)
                Win32.CloseHandle(handle);
            _threadHandles.Clear();
            _stepping.Clear();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop = true;
        _loop?.Join(TimeSpan.FromSeconds(5));
        _loop = null;
        _ready.Dispose();
    }
}
