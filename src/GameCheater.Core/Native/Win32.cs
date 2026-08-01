using System.Runtime.InteropServices;

namespace GameCheater.Core.Native;

/// <summary>
/// Thin P/Invoke surface over the Win32 memory APIs. Everything the engine does to
/// another process bottoms out here. These entry points only resolve on Windows;
/// on macOS/Linux the DllImport lookup fails at call time (compilation is fine).
/// </summary>
internal static partial class Win32
{
    // --- OpenProcess access rights ---
    [Flags]
    public enum ProcessAccess : uint
    {
        VmOperation = 0x0008,
        VmRead = 0x0010,
        VmWrite = 0x0020,
        QueryInformation = 0x0400,
        // What the engine needs to read, write, and re-protect memory.
        Trainer = VmOperation | VmRead | VmWrite | QueryInformation,
    }

    // --- VirtualProtect page protections ---
    [Flags]
    public enum MemoryProtection : uint
    {
        NoAccess = 0x01,
        ReadOnly = 0x02,
        ReadWrite = 0x04,
        WriteCopy = 0x08,
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        Guard = 0x100,
        NoCache = 0x200,
    }

    // --- VirtualQuery region state ---
    public const uint MEM_COMMIT = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public IntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(ProcessAccess desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadProcessMemory(IntPtr process, IntPtr baseAddress,
        byte[] buffer, nuint size, out nuint numberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteProcessMemory(IntPtr process, IntPtr baseAddress,
        byte[] buffer, nuint size, out nuint numberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtectEx(IntPtr process, IntPtr address,
        nuint size, MemoryProtection newProtect, out MemoryProtection oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nuint VirtualQueryEx(IntPtr process, IntPtr address,
        out MEMORY_BASIC_INFORMATION buffer, nuint length);
}
