using System.Runtime.InteropServices;
using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>
/// A hand-managed amd64 <c>CONTEXT</c> block.
///
/// Why not a plain struct: <c>GetThreadContext</c> on x64 requires the CONTEXT to be
/// 16-byte aligned (it contains XMM state), and the CLR only guarantees 8-byte alignment
/// for a struct containing <c>ulong</c>s — a misaligned buffer fails with ERROR_NOACCESS,
/// intermittently, depending on where it happened to land. So we allocate unmanaged memory,
/// round the pointer up to 16, and read/write the handful of fields we need by offset. That
/// also spares us declaring all 1232 bytes of a structure we use six fields of.
/// </summary>
internal sealed class ThreadContextBuffer : IDisposable
{
    /// <summary>sizeof(CONTEXT) on amd64.</summary>
    private const int ContextSize = 1232;

    // Field offsets within amd64 CONTEXT.
    private const int OffsetContextFlags = 0x30;
    private const int OffsetDr0 = 0x48;   // Dr0..Dr3 are contiguous 8-byte slots
    private const int OffsetDr6 = 0x68;
    private const int OffsetDr7 = 0x70;
    private const int OffsetEFlags = 0x44;
    private const int OffsetIntegerRegs = 0x78; // Rax, Rcx, Rdx, Rbx, Rsp, Rbp, Rsi, Rdi, R8..R15
    private const int OffsetRip = 0xF8;

    /// <summary>x86 trap flag: when set, the CPU raises a single-step exception after one instruction.</summary>
    public const ulong TrapFlag = 0x100;

    private IntPtr _allocation;

    /// <summary>The 16-byte-aligned CONTEXT pointer to hand to Get/SetThreadContext.</summary>
    public IntPtr Pointer { get; }

    public ThreadContextBuffer()
    {
        _allocation = Marshal.AllocHGlobal(ContextSize + 16);
        long aligned = ((long)_allocation + 15) & ~15L;
        Pointer = (IntPtr)aligned;
        for (int i = 0; i < ContextSize; i += 8)
            Marshal.WriteInt64(Pointer, i, 0);
    }

    public uint ContextFlags
    {
        get => (uint)Marshal.ReadInt32(Pointer, OffsetContextFlags);
        set => Marshal.WriteInt32(Pointer, OffsetContextFlags, (int)value);
    }

    public ulong Rip => (ulong)Marshal.ReadInt64(Pointer, OffsetRip);

    /// <summary>The flags register. Set <see cref="TrapFlag"/> to arm a one-instruction single-step.</summary>
    public ulong EFlags
    {
        get => (uint)Marshal.ReadInt32(Pointer, OffsetEFlags);
        set => Marshal.WriteInt32(Pointer, OffsetEFlags, (int)(uint)value);
    }

    /// <summary>The 16 general-purpose registers in CONTEXT order: Rax, Rcx, Rdx, Rbx, Rsp, Rbp,
    /// Rsi, Rdi, R8..R15. Captured at a fault so we can see where a writer pulled its value from.</summary>
    public ulong[] GetIntegerRegisters()
    {
        var regs = new ulong[16];
        for (int i = 0; i < 16; i++)
            regs[i] = (ulong)Marshal.ReadInt64(Pointer, OffsetIntegerRegs + i * 8);
        return regs;
    }

    public ulong Dr6
    {
        get => (ulong)Marshal.ReadInt64(Pointer, OffsetDr6);
        set => Marshal.WriteInt64(Pointer, OffsetDr6, (long)value);
    }

    public ulong Dr7
    {
        get => (ulong)Marshal.ReadInt64(Pointer, OffsetDr7);
        set => Marshal.WriteInt64(Pointer, OffsetDr7, (long)value);
    }

    public ulong GetDebugAddress(int slot) => (ulong)Marshal.ReadInt64(Pointer, OffsetDr0 + slot * 8);

    public void SetDebugAddress(int slot, ulong address) =>
        Marshal.WriteInt64(Pointer, OffsetDr0 + slot * 8, (long)address);

    /// <summary>Load <paramref name="thread"/>'s context, requesting only <paramref name="flags"/>.</summary>
    public bool Load(IntPtr thread, uint flags)
    {
        ContextFlags = flags;
        return Win32.GetThreadContext(thread, Pointer);
    }

    /// <summary>Write the buffer back, applying only the parts named by <paramref name="flags"/>.</summary>
    public bool Store(IntPtr thread, uint flags)
    {
        ContextFlags = flags;
        return Win32.SetThreadContext(thread, Pointer);
    }

    public void Dispose()
    {
        if (_allocation != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_allocation);
            _allocation = IntPtr.Zero;
        }
    }
}
