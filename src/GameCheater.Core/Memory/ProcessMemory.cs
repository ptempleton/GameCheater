using System.Diagnostics;
using System.Runtime.InteropServices;
using GameCheater.Core.Native;

namespace GameCheater.Core.Memory;

/// <summary>
/// Layer 1 — the memory-access layer. A thin, stable wrapper around a target
/// process: attach, read/write typed values and bytes, enumerate modules, and
/// change page protection for code patches. Everything else in the engine sits
/// on top of this and nothing else touches Win32 directly.
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private IntPtr _handle;

    public Process Process { get; }
    public bool IsAttached => _handle != IntPtr.Zero && !Process.HasExited;

    /// <summary>Base address of the main module (the .exe). Signatures scan relative to modules.</summary>
    public ulong MainModuleBase { get; }
    public int MainModuleSize { get; }

    private ProcessMemory(Process process, IntPtr handle)
    {
        Process = process;
        _handle = handle;
        var main = process.MainModule
            ?? throw new InvalidOperationException("Target has no main module (are you elevated?).");
        MainModuleBase = (ulong)main.BaseAddress.ToInt64();
        MainModuleSize = main.ModuleMemorySize;
    }

    /// <summary>
    /// Attach to the first running process with the given name (no ".exe").
    /// Returns null if the game isn't running so callers can poll cleanly.
    /// </summary>
    public static ProcessMemory? Attach(string processName)
    {
        var proc = Process.GetProcessesByName(processName).FirstOrDefault();
        if (proc is null)
            return null;

        var handle = Win32.OpenProcess(Win32.ProcessAccess.Trainer, false, (uint)proc.Id);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess failed for '{processName}' (err {Marshal.GetLastWin32Error()}). " +
                "Run the trainer as Administrator.");

        return new ProcessMemory(proc, handle);
    }

    /// <summary>
    /// Attach by process id. Needed whenever the name is ambiguous — two copies of the same
    /// game, or the trainer watching another instance of itself during testing.
    /// </summary>
    public static ProcessMemory? AttachToId(int processId)
    {
        Process proc;
        try
        {
            proc = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null;   // not running
        }

        var handle = Win32.OpenProcess(Win32.ProcessAccess.Trainer, false, (uint)processId);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"OpenProcess failed for pid {processId} (err {Marshal.GetLastWin32Error()}). " +
                "Run the trainer as Administrator.");

        return new ProcessMemory(proc, handle);
    }

    /// <summary>
    /// Find the module that contains <paramref name="address"/> and express it as
    /// module + offset — the durable, ASLR-safe way to store a static address the scanner
    /// found. Returns false for heap addresses (those need a pointer scan instead).
    /// </summary>
    public bool TryGetModuleContaining(ulong address, out string module, out ulong offset)
    {
        foreach (ProcessModule m in Process.Modules)
        {
            ulong b = (ulong)m.BaseAddress.ToInt64();
            ulong size = (ulong)m.ModuleMemorySize;
            if (address >= b && address < b + size)
            {
                module = m.ModuleName ?? "";
                offset = address - b;
                return true;
            }
        }
        module = "";
        offset = 0;
        return false;
    }

    /// <summary>
    /// The module containing <paramref name="address"/> as a base+size window. An AOB has to
    /// be scanned inside the module its target lives in — scanning the main module for code
    /// that sits in a DLL (or in JIT-generated memory) silently finds nothing.
    /// </summary>
    public bool TryGetModuleRange(ulong address, out string module, out ulong moduleBase, out ulong moduleSize)
    {
        foreach (ProcessModule m in Process.Modules)
        {
            ulong b = (ulong)m.BaseAddress.ToInt64();
            ulong size = (ulong)m.ModuleMemorySize;
            if (address >= b && address < b + size)
            {
                module = m.ModuleName ?? "";
                moduleBase = b;
                moduleSize = size;
                return true;
            }
        }
        module = "";
        moduleBase = 0;
        moduleSize = 0;
        return false;
    }

    /// <summary>Resolve a loaded module's base address by name (e.g. a specific DLL). Null if not loaded.</summary>
    public ulong? GetModuleBase(string moduleName)
    {
        foreach (ProcessModule m in Process.Modules)
        {
            if (string.Equals(m.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                return (ulong)m.BaseAddress.ToInt64();
        }
        return null;
    }

    // --- raw bytes ---

    public byte[] ReadBytes(ulong address, int count)
    {
        var buffer = new byte[count];
        if (!Win32.ReadProcessMemory(_handle, (IntPtr)address, buffer, (nuint)count, out var read)
            || read != (nuint)count)
        {
            throw new IOException(
                $"ReadProcessMemory failed at 0x{address:X} ({(int)read}/{count} bytes).");
        }
        return buffer;
    }

    /// <summary>Non-throwing read used by the scanner, which walks many pages that may be unreadable.</summary>
    public bool TryReadBytes(ulong address, byte[] buffer, int count)
        => Win32.ReadProcessMemory(_handle, (IntPtr)address, buffer, (nuint)count, out var read)
           && read == (nuint)count;

    public void WriteBytes(ulong address, byte[] bytes)
    {
        if (!Win32.WriteProcessMemory(_handle, (IntPtr)address, bytes, (nuint)bytes.Length, out var written)
            || written != (nuint)bytes.Length)
        {
            throw new IOException(
                $"WriteProcessMemory failed at 0x{address:X} ({(int)written}/{bytes.Length} bytes).");
        }
    }

    // --- typed values (T must be a blittable value type: int, float, long, byte, etc.) ---

    public T Read<T>(ulong address) where T : unmanaged
    {
        var bytes = ReadBytes(address, Marshal.SizeOf<T>());
        return MemoryMarshal.Read<T>(bytes);
    }

    public void Write<T>(ulong address, T value) where T : unmanaged
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        WriteBytes(address, bytes);
    }

    /// <summary>
    /// Temporarily make a region writable-executable, run <paramref name="write"/>, then
    /// restore the original protection. This is how code patches poke instruction bytes
    /// without leaving the page permanently writable.
    /// </summary>
    public void WithWritable(ulong address, int size, Action write)
    {
        if (!Win32.VirtualProtectEx(_handle, (IntPtr)address, (nuint)size,
                Win32.MemoryProtection.ExecuteReadWrite, out var old))
        {
            throw new IOException($"VirtualProtectEx failed at 0x{address:X} (err {Marshal.GetLastWin32Error()}).");
        }
        try
        {
            write();
        }
        finally
        {
            Win32.VirtualProtectEx(_handle, (IntPtr)address, (nuint)size, old, out _);
        }
    }

    /// <summary>
    /// Enumerate committed memory regions (with protection) starting at <paramref name="start"/>.
    /// Callers filter by <see cref="MemoryRegion.IsReadable"/>/<c>IsWritable</c>/<c>IsExecutable</c>.
    /// Used by the snapshot/diff oracle to grab code vs data pages.
    /// </summary>
    public IEnumerable<MemoryRegion> EnumerateRegions(ulong start = 0x10000, ulong length = 0x7FFF_FFFF_FFFF)
    {
        ulong address = start;
        ulong end = start + length;
        nuint mbiSize = (nuint)Marshal.SizeOf<Win32.MEMORY_BASIC_INFORMATION>();

        while (address < end)
        {
            if (Win32.VirtualQueryEx(_handle, (IntPtr)address, out var mbi, mbiSize) == 0)
                break;

            ulong regionBase = (ulong)mbi.BaseAddress.ToInt64();
            ulong regionSize = (ulong)mbi.RegionSize.ToInt64();
            if (regionSize == 0)
                break;

            if (mbi.State == Win32.MEM_COMMIT)
                yield return new MemoryRegion { Base = regionBase, Size = regionSize, Protect = mbi.Protect };

            address = regionBase + regionSize;
        }
    }

    /// <summary>
    /// Committed, readable regions overlapping [start, start+length), clipped to that window.
    /// The value scanner uses this to skip guard/no-access pages instead of blindly reading.
    /// </summary>
    internal IEnumerable<(ulong Base, int Size)> EnumerateReadableRegions(ulong start, ulong length)
    {
        ulong end = start + length;
        foreach (var r in EnumerateRegions(start, length))
        {
            if (!r.IsReadable)
                continue;
            ulong from = Math.Max(r.Base, start);
            ulong to = Math.Min(r.Base + r.Size, end);
            if (to > from)
                yield return (from, (int)Math.Min(to - from, int.MaxValue));
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Win32.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
