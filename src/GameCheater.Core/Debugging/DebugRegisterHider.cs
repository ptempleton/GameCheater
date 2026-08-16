using System.Runtime.InteropServices;
using GameCheater.Core.Memory;
using GameCheater.Core.Native;

namespace GameCheater.Core.Debugging;

/// <summary>
/// Hides our hardware breakpoints from a game that self-inspects its debug registers.
///
/// SnowRunner (and similar anti-tamper) detects a hardware breakpoint by calling
/// <c>GetThreadContext</c> on its own threads and checking Dr0–Dr7 — if any are set, it self-
/// exits. This is the exact thing that kills <see cref="WriteWatch"/> there. We can't avoid
/// setting the debug registers (that IS the breakpoint), but we can make the game's own query
/// come back clean: hook <c>ntdll!NtGetContextThread</c> INSIDE the target with a small detour
/// that runs the real syscall and then zeroes the Dr fields whenever the caller asked for
/// <c>CONTEXT_DEBUG_REGISTERS</c>. Our own reads (from this process, through our un-hooked ntdll)
/// still see the real registers, so the breakpoint logic keeps working. This is the user-mode
/// technique ScyllaHide uses, and it's how a driver-free trainer traces SnowRunner-class values.
///
/// Windows-only, x64. Install after attaching; <see cref="Dispose"/> restores the stub.
/// </summary>
public sealed class DebugRegisterHider : IDisposable
{
    private readonly ProcessMemory _memory;
    private IntPtr _hookTarget;      // ntdll!NtGetContextThread in the target (== our address; shared ntdll)
    private IntPtr _detour;          // our allocated shellcode in the target
    private byte[]? _originalStub;   // the bytes we overwrote, for restore
    private bool _installed;

    private DebugRegisterHider(ProcessMemory memory) => _memory = memory;

    public bool Installed => _installed;

    /// <summary>Install the hook, or return null if it couldn't be built (non-Windows, or an
    /// ntdll stub shape we don't recognise). Failure is non-fatal — the caller can proceed
    /// without hiding, it just risks detection.</summary>
    public static DebugRegisterHider? TryInstall(ProcessMemory memory)
    {
        var hider = new DebugRegisterHider(memory);
        return hider.Install() ? hider : null;
    }

    private bool Install()
    {
        // ntdll is mapped at the same base in every process this boot, so its export address in
        // our process equals the target's — no need to parse the target's export table.
        IntPtr ntdll = Win32.GetModuleHandleW("ntdll.dll");
        if (ntdll == IntPtr.Zero) return false;
        _hookTarget = Win32.GetProcAddress(ntdll, "NtGetContextThread");
        if (_hookTarget == IntPtr.Zero) return false;

        // The syscall stub starts: 4C 8B D1 (mov r10,rcx) B8 <ssn:4> (mov eax,ssn). Read the SSN
        // from our own copy; bail if the shape isn't what we expect (don't patch blindly).
        byte b0 = Marshal.ReadByte(_hookTarget);
        byte b1 = Marshal.ReadByte(_hookTarget, 1);
        byte b2 = Marshal.ReadByte(_hookTarget, 2);
        byte b3 = Marshal.ReadByte(_hookTarget, 3);
        if (b0 != 0x4C || b1 != 0x8B || b2 != 0xD1 || b3 != 0xB8)
            return false;
        uint ssn = (uint)Marshal.ReadInt32(_hookTarget, 4);

        var detourCode = BuildDetour(ssn);

        _detour = Win32.VirtualAllocEx(_memory.Handle, IntPtr.Zero, (nuint)256,
            Win32.MEM_COMMIT_RESERVE, Win32.MemoryProtection.ExecuteReadWrite);
        if (_detour == IntPtr.Zero) return false;
        _memory.WriteBytes((ulong)_detour, detourCode);

        // Overwrite the stub entry with an absolute indirect jump to the detour:
        //   FF 25 00 00 00 00            jmp qword ptr [rip+0]
        //   <8-byte detour address>
        var patch = new byte[14];
        patch[0] = 0xFF; patch[1] = 0x25;
        // disp32 = 0 (bytes 2..5 already zero)
        BitConverter.GetBytes((ulong)_detour).CopyTo(patch, 6);

        _originalStub = _memory.ReadBytes((ulong)_hookTarget, patch.Length);
        _memory.WithWritable((ulong)_hookTarget, patch.Length, () => _memory.WriteBytes((ulong)_hookTarget, patch));
        _installed = true;
        return true;
    }

    /// <summary>
    /// The detour: run the real NtGetContextThread syscall, then if the caller requested
    /// CONTEXT_DEBUG_REGISTERS (ContextFlags &amp; 0x10), zero Dr0–Dr3, Dr6, Dr7 in the returned
    /// CONTEXT so the game sees no breakpoint. rcx=hThread, rdx=pContext on entry (unchanged by
    /// the syscall), matching NtGetContextThread's own calling convention.
    /// </summary>
    private static byte[] BuildDetour(uint ssn)
    {
        var code = new List<byte>
        {
            0x4C, 0x8B, 0xD1,                   // mov r10, rcx
            0xB8, (byte)ssn, (byte)(ssn >> 8), (byte)(ssn >> 16), (byte)(ssn >> 24), // mov eax, ssn
            0x0F, 0x05,                         // syscall           (rax=status, rdx preserved)
            0x50,                               // push rax          (save status)
            0x8B, 0x4A, 0x30,                   // mov ecx, [rdx+0x30]   (ContextFlags)
            0xF6, 0xC1, 0x10,                   // test cl, 0x10         (CONTEXT_DEBUG_REGISTERS)
            0x74, 0x1B,                         // jz  +0x1B -> pop rax  (skip zeroing, 27 bytes)
            0x48, 0x31, 0xC0,                   // xor rax, rax
            0x48, 0x89, 0x42, 0x48,             // mov [rdx+0x48], rax   (Dr0)
            0x48, 0x89, 0x42, 0x50,             // mov [rdx+0x50], rax   (Dr1)
            0x48, 0x89, 0x42, 0x58,             // mov [rdx+0x58], rax   (Dr2)
            0x48, 0x89, 0x42, 0x60,             // mov [rdx+0x60], rax   (Dr3)
            0x48, 0x89, 0x42, 0x68,             // mov [rdx+0x68], rax   (Dr6)
            0x48, 0x89, 0x42, 0x70,             // mov [rdx+0x70], rax   (Dr7)
            0x58,                               // pop rax           (restore status)
            0xC3,                               // ret
        };
        return code.ToArray();
    }

    public void Dispose()
    {
        if (!_installed) return;
        _installed = false;
        try
        {
            if (_originalStub is not null && _hookTarget != IntPtr.Zero && !_memory.Process.HasExited)
                _memory.WithWritable((ulong)_hookTarget, _originalStub.Length,
                    () => _memory.WriteBytes((ulong)_hookTarget, _originalStub));
        }
        catch (IOException) { /* target gone — nothing to restore */ }

        if (_detour != IntPtr.Zero && !_memory.Process.HasExited)
            Win32.VirtualFreeEx(_memory.Handle, _detour, 0, Win32.MEM_RELEASE);
        _detour = IntPtr.Zero;
    }
}
