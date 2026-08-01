using GameCheater.Core.Memory;

namespace GameCheater.Core.Cheats;

/// <summary>
/// A code-patch cheat: "no damage", "no reload", "infinite items" implemented by
/// disabling the instruction that decrements a value rather than fighting it with writes.
/// "On" overwrites the instruction bytes (commonly NOPs); "Off" restores the exact
/// original bytes saved at enable time. This mirrors a Cheat Engine script's
/// [ENABLE]/[DISABLE] pair — enable patches, disable reverts.
///
/// The critical invariant: read and stash the original bytes BEFORE writing the patch,
/// so disable (and teardown on exit) can always put memory back the way it was.
/// </summary>
public sealed class PatchCheat : Cheat
{
    private readonly Func<ProcessMemory, ulong?> _resolve;
    private readonly byte[] _patch;
    private ulong _address;
    private byte[]? _original;

    public PatchCheat(Func<ProcessMemory, ulong?> resolver, params byte[] patchBytes)
    {
        if (patchBytes.Length == 0)
            throw new ArgumentException("Patch must contain at least one byte.", nameof(patchBytes));
        _resolve = resolver;
        _patch = patchBytes;
    }

    /// <summary>Convenience: a run of <paramref name="count"/> NOP (0x90) bytes, e.g.
    /// <c>new PatchCheat(resolver, PatchCheat.Nops(7)) { Name = "No Damage" }</c>.</summary>
    public static byte[] Nops(int count)
    {
        var nops = new byte[count];
        Array.Fill(nops, (byte)0x90);
        return nops;
    }

    protected override void OnEnable()
    {
        _address = _resolve(Memory)
            ?? throw new InvalidOperationException($"Cheat '{Name}': address failed to resolve.");
        _original = Memory.ReadBytes(_address, _patch.Length);   // save original FIRST
        Memory.WithWritable(_address, _patch.Length, () => Memory.WriteBytes(_address, _patch));
    }

    protected override void OnDisable()
    {
        if (_original is null) return;
        Memory.WithWritable(_address, _original.Length, () => Memory.WriteBytes(_address, _original));
        _original = null;
    }
}
