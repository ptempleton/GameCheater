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
    /// ntdll stub shape we don't recognise). The caller decides whether proceeding without
    /// hiding is safe; anti-tamper-aware callers should fail closed.</summary>
    public static DebugRegisterHider? TryInstall(ProcessMemory memory)
    {
        var hider = new DebugRegisterHider(memory);
        try
        {
            if (hider.Install())
                return hider;
        }
        catch
        {
            hider.RollBackFailedInstall();
            throw;
        }

        hider.RollBackFailedInstall();
        return null;
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
        Flush(_detour, detourCode.Length);

        // Overwrite the stub entry with an absolute indirect jump to the detour:
        //   FF 25 00 00 00 00            jmp qword ptr [rip+0]
        //   <8-byte detour address>
        var patch = new byte[14];
        patch[0] = 0xFF; patch[1] = 0x25;
        // disp32 = 0 (bytes 2..5 already zero)
        BitConverter.GetBytes((ulong)_detour).CopyTo(patch, 6);

        _originalStub = _memory.ReadBytes((ulong)_hookTarget, patch.Length);
        // From this point onward the entry stub may be partially modified even if the write
        // reports failure, so rollback must restore it before freeing the detour.
        _installed = true;
        _memory.WithWritable((ulong)_hookTarget, patch.Length, () => _memory.WriteBytes((ulong)_hookTarget, patch));
        Flush(_hookTarget, patch.Length);
        return true;
    }

    private void Flush(IntPtr address, int size)
    {
        if (!Win32.FlushInstructionCache(_memory.Handle, address, (nuint)size))
            throw new IOException(
                $"FlushInstructionCache failed at 0x{address.ToInt64():X} (err {Marshal.GetLastWin32Error()}).");
    }

    private void RollBackFailedInstall()
    {
        if (_installed)
        {
            try { Dispose(); }
            catch { /* Preserve the original installation failure. */ }
            return;
        }

        if (_detour != IntPtr.Zero && !_memory.Process.HasExited)
            Win32.VirtualFreeEx(_memory.Handle, _detour, 0, Win32.MEM_RELEASE);
        _detour = IntPtr.Zero;
    }

    /// <summary>
    /// The detour: run the real NtGetContextThread syscall, then if the caller requested
    /// CONTEXT_DEBUG_REGISTERS (ContextFlags &amp; 0x10), zero Dr0–Dr3, Dr6, Dr7 in the returned
    /// CONTEXT so the game sees no breakpoint. rcx=hThread and rdx=pContext on entry. The
    /// context pointer is saved on the stack because volatile argument registers are not
    /// guaranteed to survive the syscall.
    /// </summary>
    private static byte[] BuildDetour(uint ssn)
    {
        var code = new List<byte>
        {
            0x52,                               // push rdx          (save pContext)
            0x4C, 0x8B, 0xD1,                   // mov r10, rcx
            0xB8, (byte)ssn, (byte)(ssn >> 8), (byte)(ssn >> 16), (byte)(ssn >> 24), // mov eax, ssn
            0x0F, 0x05,                         // syscall           (rax=status)
            0x5A,                               // pop rdx           (restore pContext)
            0x50,                               // push rax          (save status)
            0x85, 0xC0,                         // test eax, eax
            0x78, 0x23,                         // js  +0x23 -> pop rax  (failed syscall)
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
        bool restored = false;
        try
        {
            if (_originalStub is not null && _hookTarget != IntPtr.Zero && !_memory.Process.HasExited)
            {
                _memory.WithWritable((ulong)_hookTarget, _originalStub.Length,
                    () => _memory.WriteBytes((ulong)_hookTarget, _originalStub));
                Flush(_hookTarget, _originalStub.Length);
            }
            restored = true;
        }
        catch (IOException) when (_memory.Process.HasExited)
        {
            restored = true;
        }

        // Never free code that the patched stub may still jump to. If restoration failed while
        // the target is alive, leave the detour allocated and surface the failure to the caller.
        if (restored)
        {
            _installed = false;
            if (_detour != IntPtr.Zero && !_memory.Process.HasExited)
                Win32.VirtualFreeEx(_memory.Handle, _detour, 0, Win32.MEM_RELEASE);
            _detour = IntPtr.Zero;
        }
    }
}
