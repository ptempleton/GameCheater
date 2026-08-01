namespace GameCheater.Core.Memory;

/// <summary>
/// A committed region of the target's address space with its page protection. The diff /
/// oracle tooling uses the protection flags to snapshot the right memory: executable pages
/// for code-patch cheats, writable pages for value cheats.
/// </summary>
public readonly struct MemoryRegion
{
    public ulong Base { get; init; }
    public ulong Size { get; init; }
    public uint Protect { get; init; }

    // Win32 page-protection bits.
    private const uint Guard = 0x100;
    private const uint NoAccess = 0x01;
    private const uint ExecMask = 0x10 | 0x20 | 0x40 | 0x80;         // EXECUTE, EXECUTE_READ, EXECUTE_READWRITE, EXECUTE_WRITECOPY
    private const uint WriteMask = 0x04 | 0x08 | 0x40 | 0x80;        // READWRITE, WRITECOPY, EXECUTE_READWRITE, EXECUTE_WRITECOPY
    private const uint ReadMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    public bool IsAccessible => Protect != 0 && (Protect & Guard) == 0 && (Protect & NoAccess) == 0;
    public bool IsExecutable => IsAccessible && (Protect & ExecMask) != 0;
    public bool IsWritable => IsAccessible && (Protect & WriteMask) != 0;
    public bool IsReadable => IsAccessible && (Protect & ReadMask) != 0;
}
