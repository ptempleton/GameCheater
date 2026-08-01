namespace GameCheater.Core.Memory;

/// <summary>
/// A multi-level pointer path — the other durable way to pin a cheat. Games keep
/// player/vehicle structs on the heap at addresses that change every launch, but the
/// *path* to them from a static module location is stable:
///
///   final = [[[moduleBase + baseOffset] + off0] + off1] ... + offLast
///
/// Cheat Engine's "pointer scan" produces exactly these. You resolve the chain fresh
/// each time you enable a cheat, so it survives restarts. If any hop reads as null the
/// resolve fails (returns null) rather than writing to garbage.
/// </summary>
public sealed class PointerChain
{
    /// <summary>Static base, usually moduleBase + a fixed offset (or an AOB-resolved address).</summary>
    public ulong Base { get; }

    /// <summary>Offsets applied at each dereference level; the last is added to the final pointer.</summary>
    public IReadOnlyList<int> Offsets { get; }

    public PointerChain(ulong @base, params int[] offsets)
    {
        Base = @base;
        Offsets = offsets;
    }

    /// <summary>
    /// Walk the chain and return the final address, or null if any intermediate
    /// pointer is null / unreadable. Semantics match Cheat Engine's pointer display
    /// `[[[Base]+o0]+o1]+oLast`:
    ///   p = read(Base); p = read(p + o0); p = read(p + o1); ... final = p + oLast
    /// (the LAST offset is added but NOT dereferenced — it lands on the value itself).
    /// </summary>
    public ulong? Resolve(ProcessMemory memory)
    {
        try
        {
            // No offsets: the static base address is itself the target.
            if (Offsets.Count == 0)
                return Base;

            ulong p = memory.Read<ulong>(Base);
            if (p == 0) return null;

            // Dereference through every offset except the last.
            for (int i = 0; i < Offsets.Count - 1; i++)
            {
                p = memory.Read<ulong>(p + (ulong)(long)Offsets[i]);
                if (p == 0) return null;
            }

            // Final offset is a plain add — this is the address of the value.
            return p + (ulong)(long)Offsets[^1];
        }
        catch (IOException)
        {
            return null; // any hop hit unreadable memory — treat the whole chain as unresolved
        }
    }
}
