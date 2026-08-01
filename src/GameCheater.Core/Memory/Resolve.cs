namespace GameCheater.Core.Memory;

/// <summary>
/// Factories for the address resolvers a cheat runs at enable time. Unifying the three
/// ways to locate a target behind one <c>Func&lt;ProcessMemory, ulong?&gt;</c> means the
/// cheat types don't care *how* an address was found — static offset, AOB scan, or
/// pointer chain all look the same to them. Returning null means "couldn't resolve",
/// which the cheat surfaces instead of writing to garbage.
/// </summary>
public static class Resolve
{
    /// <summary>An absolute address — valid only for the current session (not ASLR-safe).
    /// Used for a live "freeze this candidate" test after a scan, before it's made durable.</summary>
    public static Func<ProcessMemory, ulong?> Absolute(ulong address)
        => _ => address;

    /// <summary>A fixed offset from the main module base (moduleBase + offset).</summary>
    public static Func<ProcessMemory, ulong?> Static(ulong moduleOffset)
        => mem => mem.MainModuleBase + moduleOffset;

    /// <summary>A fixed offset from a named module (e.g. a specific DLL). Null if the module isn't loaded.</summary>
    public static Func<ProcessMemory, ulong?> Static(string moduleName, ulong offset)
        => mem => mem.GetModuleBase(moduleName) is { } b ? b + offset : null;

    /// <summary>Scan the main module for an AOB signature, then add an offset to the match.</summary>
    public static Func<ProcessMemory, ulong?> Aob(string pattern, int offset = 0)
    {
        var sig = new Signature(pattern);
        return mem => sig.Scan(mem) is { } hit ? hit + (ulong)(long)offset : null;
    }

    /// <summary>
    /// Scan for an AOB, then treat the match as an x64 RIP-relative instruction and
    /// resolve the pointer it references. This is how you pin a "static" value that CE
    /// shows as `[game.exe+X]` but which is really `mov reg,[rip+disp32]`.
    /// </summary>
    public static Func<ProcessMemory, ulong?> AobRipRelative(string pattern, int dispOffset, int instructionLength)
    {
        var sig = new Signature(pattern);
        return mem => sig.Scan(mem) is { } hit
            ? Signature.ResolveRipRelative(mem, hit, dispOffset, instructionLength)
            : null;
    }

    /// <summary>Walk a static pointer chain (moduleBase + baseOffset, then offsets).</summary>
    public static Func<ProcessMemory, ulong?> Pointer(ulong moduleBaseOffset, params int[] offsets)
        => mem => new PointerChain(mem.MainModuleBase + moduleBaseOffset, offsets).Resolve(mem);

    /// <summary>A pointer chain whose base was found some other way (e.g. an AOB scan).</summary>
    public static Func<ProcessMemory, ulong?> Pointer(Func<ProcessMemory, ulong?> baseResolver, params int[] offsets)
        => mem => baseResolver(mem) is { } b ? new PointerChain(b, offsets).Resolve(mem) : null;
}
