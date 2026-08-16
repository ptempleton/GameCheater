using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>
/// Diagnostic helper for testing debug-register hiding from inside a target process.
/// It suspends one thread briefly, requests its debug-register context directly through
/// ntdll!NtGetContextThread, and reports whether any Dr0–Dr3/Dr6/Dr7 value is visible.
/// </summary>
public static class DebugRegisterVisibilityProbe
{
    public static bool? AreRegistersVisible(uint threadId)
    {
        IntPtr thread = Win32.OpenThread(Win32.ThreadAccess.Breakpoint, false, threadId);
        if (thread == IntPtr.Zero)
            return null;

        try
        {
            if (Win32.SuspendThread(thread) == uint.MaxValue)
                return null;

            try
            {
                using var context = new ThreadContextBuffer();
                context.ContextFlags = Win32.CONTEXT_DEBUG_REGISTERS;
                if (Win32.NtGetContextThread(thread, context.Pointer) < 0)
                    return null;

                return context.Dr6 != 0 || context.Dr7 != 0 ||
                    Enumerable.Range(0, HardwareBreakpoint.SlotCount)
                        .Any(slot => context.GetDebugAddress(slot) != 0);
            }
            finally
            {
                Win32.ResumeThread(thread);
            }
        }
        finally
        {
            Win32.CloseHandle(thread);
        }
    }
}
