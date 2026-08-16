using GameCheater.Core.Memory;

namespace GameCheater.Core.Debugging;

/// <summary>The two user-mode debugger flags a game's anti-debug code typically polls.</summary>
public readonly record struct PebDebugFlags(byte BeingDebugged, uint NtGlobalFlag)
{
    /// <summary>True when either flag shows the tell-tale of a debugger being attached.</summary>
    public bool LooksDebugged => BeingDebugged != 0 || (NtGlobalFlag & AntiDebug.DebugHeapFlags) != 0;

    public override string ToString() =>
        $"BeingDebugged={BeingDebugged}  NtGlobalFlag=0x{NtGlobalFlag:X}" +
        (LooksDebugged ? "  (debugged)" : "  (clean)");
}

/// <summary>
/// User-mode anti-anti-debug: read and clear the debugger tells that live in the target's PEB.
///
/// When a debugger attaches, Windows sets <c>PEB.BeingDebugged</c> and flips heap flags in
/// <c>PEB.NtGlobalFlag</c>. The cheap, common anti-debug check — <c>IsDebuggerPresent()</c> and
/// its heap-flag cousins — just reads those bytes, so overwriting them back to their
/// not-debugged values defeats it, provided we keep doing it (the game re-checks on a loop).
///
/// What this CANNOT do, and it matters: the kernel's own record that a process is being
/// debugged (the debug object / debug port, reached via
/// <c>NtQueryInformationProcess(ProcessDebugPort | ProcessDebugObjectHandle)</c>) is ground
/// truth we can't touch from user mode. A game that queries that instead will still detect us.
/// Masking it would mean hooking ntdll inside the target — a much bigger, riskier change.
/// </summary>
public static class AntiDebug
{
    // PEB field offsets on x64.
    private const int BeingDebuggedOffset = 0x02;
    private const int NtGlobalFlagOffset = 0xBC;

    /// <summary>The NtGlobalFlag bits set when a process runs under a debugger:
    /// FLG_HEAP_ENABLE_TAIL_CHECK | FLG_HEAP_ENABLE_FREE_CHECK | FLG_HEAP_VALIDATE_PARAMETERS.</summary>
    public const uint DebugHeapFlags = 0x70;

    /// <summary>Read the current PEB debug flags, or null if the PEB can't be located/read.</summary>
    public static PebDebugFlags? Read(ProcessMemory memory)
    {
        if (memory.GetPebBaseAddress() is not { } peb)
            return null;
        try
        {
            return new PebDebugFlags(
                memory.Read<byte>(peb + BeingDebuggedOffset),
                memory.Read<uint>(peb + NtGlobalFlagOffset));
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Force the PEB debug flags back to their not-debugged values. Returns true if it wrote.
    /// Call it once the instant the debugger attaches (before the target resumes) and then
    /// repeatedly, because the game keeps re-reading them.
    /// </summary>
    public static bool Clear(ProcessMemory memory)
    {
        if (memory.GetPebBaseAddress() is not { } peb)
            return false;
        try
        {
            memory.Write<byte>(peb + BeingDebuggedOffset, 0);
            uint flag = memory.Read<uint>(peb + NtGlobalFlagOffset);
            if ((flag & DebugHeapFlags) != 0)
                memory.Write<uint>(peb + NtGlobalFlagOffset, flag & ~DebugHeapFlags);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
