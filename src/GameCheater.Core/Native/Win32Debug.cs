using System.Runtime.InteropServices;

namespace GameCheater.Core.Native;

/// <summary>
/// The debugger half of the Win32 surface: attaching as a debugger, pumping debug events,
/// and reading/writing thread contexts (which is how hardware breakpoints get installed —
/// the debug registers Dr0–Dr3 and Dr7 live in a thread's CONTEXT, not in the process).
///
/// Same deal as the rest of <see cref="Win32"/>: these only resolve at runtime on Windows.
/// </summary>
internal static partial class Win32
{
    // --- debug event codes ---
    public const uint EXCEPTION_DEBUG_EVENT = 1;
    public const uint CREATE_THREAD_DEBUG_EVENT = 2;
    public const uint CREATE_PROCESS_DEBUG_EVENT = 3;
    public const uint EXIT_THREAD_DEBUG_EVENT = 4;
    public const uint EXIT_PROCESS_DEBUG_EVENT = 5;
    public const uint LOAD_DLL_DEBUG_EVENT = 6;
    public const uint UNLOAD_DLL_DEBUG_EVENT = 7;

    // --- ContinueDebugEvent statuses ---
    /// <summary>We handled the exception; do not pass it to the target's own handlers.</summary>
    public const uint DBG_CONTINUE = 0x00010002;
    /// <summary>Not ours — let the target's exception handlers see it, as it would without us.</summary>
    public const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

    // --- exception codes we care about ---
    /// <summary>What a hardware (debug-register) breakpoint reports.</summary>
    public const uint EXCEPTION_SINGLE_STEP = 0x80000004;
    /// <summary>The synthetic int3 the OS injects into the target when a debugger attaches.</summary>
    public const uint EXCEPTION_BREAKPOINT = 0x80000003;
    /// <summary>Raised when code touches a page whose protection we stripped — the page-guard watch.</summary>
    public const uint EXCEPTION_ACCESS_VIOLATION = 0xC0000005;

    // --- CONTEXT.ContextFlags (amd64) ---
    public const uint CONTEXT_AMD64 = 0x00100000;
    public const uint CONTEXT_CONTROL = CONTEXT_AMD64 | 0x01;
    public const uint CONTEXT_INTEGER = CONTEXT_AMD64 | 0x02;
    public const uint CONTEXT_DEBUG_REGISTERS = CONTEXT_AMD64 | 0x10;

    /// <summary>Returned by GetLastError when WaitForDebugEvent simply timed out.</summary>
    public const int ERROR_SEM_TIMEOUT = 121;

    public enum ThreadAccess : uint
    {
        GetContext = 0x0008,
        SetContext = 0x0010,
        SuspendResume = 0x0002,
        QueryInformation = 0x0040,
        Breakpoint = GetContext | SetContext | SuspendResume | QueryInformation,
    }

    /// <summary>
    /// The Win32 DEBUG_EVENT. Its payload is a union, which C# can express as an explicit
    /// layout: every event's fields are declared at their offset *within the union* plus the
    /// 16-byte header. Only the fields this engine reads are declared; <c>Size = 176</c> keeps
    /// the struct as large as the largest union member (EXCEPTION_DEBUG_INFO) so the OS never
    /// writes past the end.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 176)]
    public struct DEBUG_EVENT
    {
        [FieldOffset(0)] public uint DebugEventCode;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;

        // EXCEPTION_DEBUG_INFO → EXCEPTION_RECORD
        [FieldOffset(16)] public uint ExceptionCode;
        [FieldOffset(32)] public IntPtr ExceptionAddress;
        [FieldOffset(168)] public uint FirstChance;

        // EXCEPTION_RECORD.ExceptionInformation for an access violation: [0] is the access kind
        // (0 read, 1 write, 8 DEP) and [1] is the faulting virtual address — how the page-guard
        // watch learns which byte was written and whether it's the one we care about.
        [FieldOffset(48)] public ulong ExceptionInformation0;
        [FieldOffset(56)] public ulong ExceptionInformation1;

        /// <summary>
        /// Union offset 0, which is <c>hThread</c> for CREATE_THREAD_DEBUG_EVENT and
        /// <c>hFile</c> for CREATE_PROCESS_DEBUG_EVENT / LOAD_DLL_DEBUG_EVENT.
        /// </summary>
        [FieldOffset(16)] public IntPtr UnionHandle;

        /// <summary>Union offset 16 — <c>hThread</c> inside CREATE_PROCESS_DEBUG_INFO.</summary>
        [FieldOffset(32)] public IntPtr CreateProcessThread;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DebugActiveProcess(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DebugActiveProcessStop(uint processId);

    /// <summary>
    /// Pass false so the debuggee SURVIVES us exiting. Without this, a crash (or a stray
    /// Ctrl-C) in the trainer takes the game down with it — the default is kill-on-exit.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DebugSetProcessKillOnExit([MarshalAs(UnmanagedType.Bool)] bool killOnExit);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WaitForDebugEvent(out DEBUG_EVENT debugEvent, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ContinueDebugEvent(uint processId, uint threadId, uint continueStatus);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenThread(ThreadAccess desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

    // The CONTEXT pointer is passed raw because amd64 CONTEXT must be 16-byte aligned —
    // see ThreadContextBuffer, which allocates and aligns it by hand.
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetThreadContext(IntPtr thread, IntPtr context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadContext(IntPtr thread, IntPtr context);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint SuspendThread(IntPtr thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint ResumeThread(IntPtr thread);

    // --- PEB lookup, for reading/clearing the user-mode debugger flags a game polls ---

    /// <summary>The subset of PROCESS_BASIC_INFORMATION we read. On x64 the PEB base sits at
    /// offset 8 (after the 4-byte ExitStatus, padded to pointer alignment).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    public const int ProcessBasicInformation = 0;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(IntPtr process, int infoClass,
        out PROCESS_BASIC_INFORMATION info, uint infoLength, out uint returnLength);
}
