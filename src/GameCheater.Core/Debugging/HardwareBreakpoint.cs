using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>What kind of access trips a debug-register breakpoint.</summary>
public enum BreakpointCondition
{
    Execute = 0b00,
    Write = 0b01,
    ReadWrite = 0b11,
}

/// <summary>
/// Installs and removes x86 hardware breakpoints via a thread's debug registers.
///
/// Hardware breakpoints are the right tool here because they are non-invasive: unlike an
/// int3 patch they do not modify a single byte of the game's code, so nothing has to be
/// restored if we crash, and code-integrity checks see an unmodified image. The cost is that
/// there are only four of them (Dr0–Dr3), and — crucially — they are *per thread*, not per
/// process. A game writes its state from whichever worker thread got the job, so the
/// breakpoint has to be installed on every thread and on every thread created afterwards.
/// </summary>
internal static class HardwareBreakpoint
{
    /// <summary>Number of debug-register slots the CPU provides.</summary>
    public const int SlotCount = 4;

    /// <summary>
    /// Encode a watched-region size into the two-bit LEN field. The encoding is not in
    /// numeric order (2 means eight bytes), and the CPU requires the address to be aligned
    /// to the length, so this picks the largest legal length the address supports.
    /// </summary>
    public static int EncodeLength(ulong address, int size)
    {
        if (size >= 8 && address % 8 == 0) return 0b10;   // 8 bytes
        if (size >= 4 && address % 4 == 0) return 0b11;   // 4 bytes
        if (size >= 2 && address % 2 == 0) return 0b01;   // 2 bytes
        return 0b00;                                       // 1 byte
    }

    /// <summary>How many bytes a LEN encoding actually covers — used to warn when the
    /// watched range is narrower than the value the caller asked about.</summary>
    public static int DecodeLength(int encoded) => encoded switch
    {
        0b00 => 1,
        0b01 => 2,
        0b10 => 8,
        _ => 4,
    };

    /// <summary>
    /// Arm <paramref name="slot"/> on one thread. The thread is suspended around the context
    /// swap: SetThreadContext on a thread that is actively running is not reliable.
    /// </summary>
    public static bool Arm(IntPtr thread, int slot, ulong address, BreakpointCondition condition,
        int lengthEncoding, bool suspend)
    {
        return Modify(thread, suspend, context =>
        {
            context.SetDebugAddress(slot, address);

            ulong dr7 = context.Dr7;
            dr7 |= 1UL << (slot * 2);                       // L<slot> — local (per-thread) enable
            dr7 |= 1UL << 8;                                // LE — exact data-breakpoint reporting
            dr7 &= ~(0xFUL << (16 + slot * 4));             // clear this slot's R/W + LEN field
            dr7 |= (ulong)((lengthEncoding << 2) | (int)condition) << (16 + slot * 4);
            context.Dr7 = dr7;

            context.Dr6 = 0;                                // stale hit flags confuse the next trap
        });
    }

    /// <summary>
    /// Disarm <paramref name="slot"/> on one thread. This MUST happen before detaching:
    /// a debug register left armed with no debugger attached raises an unhandled
    /// STATUS_SINGLE_STEP in the game and kills it.
    /// </summary>
    public static bool Disarm(IntPtr thread, int slot, bool suspend)
    {
        return Modify(thread, suspend, context =>
        {
            context.SetDebugAddress(slot, 0);
            ulong dr7 = context.Dr7;
            dr7 &= ~(3UL << (slot * 2));                    // clear local + global enable
            dr7 &= ~(0xFUL << (16 + slot * 4));             // clear R/W + LEN
            context.Dr7 = dr7;
            context.Dr6 = 0;
        });
    }

    /// <summary>True when Dr6 says <paramref name="slot"/> is the breakpoint that just fired.</summary>
    public static bool DidFire(ulong dr6, int slot) => (dr6 & (1UL << slot)) != 0;

    /// <summary>
    /// Check whether <paramref name="slot"/> is still armed for <paramref name="address"/> on
    /// this thread, re-arming it if not. Returns true when a re-arm was needed — the signature
    /// of a game actively clearing debug registers as an anti-tamper measure. The thread is
    /// suspended for the check since it is running free between debug events.
    /// </summary>
    public static bool ReArmIfCleared(IntPtr thread, int slot, ulong address,
        BreakpointCondition condition, int lengthEncoding)
    {
        if (thread == IntPtr.Zero)
            return false;
        if (Win32.SuspendThread(thread) == uint.MaxValue)
            return false;

        try
        {
            using var context = new ThreadContextBuffer();
            if (!context.Load(thread, Win32.CONTEXT_DEBUG_REGISTERS))
                return false;

            bool enabled = (context.Dr7 & (1UL << (slot * 2))) != 0;
            bool addressed = context.GetDebugAddress(slot) == address;
            if (enabled && addressed)
                return false;   // still armed — nobody touched it

            context.SetDebugAddress(slot, address);
            ulong dr7 = context.Dr7;
            dr7 |= 1UL << (slot * 2);
            dr7 |= 1UL << 8;
            dr7 &= ~(0xFUL << (16 + slot * 4));
            dr7 |= (ulong)((lengthEncoding << 2) | (int)condition) << (16 + slot * 4);
            context.Dr7 = dr7;
            context.Store(thread, Win32.CONTEXT_DEBUG_REGISTERS);
            return true;
        }
        finally
        {
            Win32.ResumeThread(thread);
        }
    }

    private static bool Modify(IntPtr thread, bool suspend, Action<ThreadContextBuffer> edit)
    {
        if (thread == IntPtr.Zero)
            return false;

        // Suspending is only needed when the target is running free. While we are inside a
        // debug event the whole process is already frozen, and suspending there is wasted work.
        if (suspend && Win32.SuspendThread(thread) == uint.MaxValue)
            return false;

        try
        {
            using var context = new ThreadContextBuffer();
            if (!context.Load(thread, Win32.CONTEXT_DEBUG_REGISTERS))
                return false;

            edit(context);

            return context.Store(thread, Win32.CONTEXT_DEBUG_REGISTERS);
        }
        finally
        {
            if (suspend)
                Win32.ResumeThread(thread);
        }
    }
}
