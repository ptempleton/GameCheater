namespace GameCheater.Core.Scanning;

/// <summary>
/// A contiguous run of bytes that differ between a snapshot and later memory — one thing an
/// external trainer (or the game) changed. Carries the old and new bytes plus convenience
/// numeric readings so a value change shows as e.g. <c>45 → 999</c>.
/// </summary>
public sealed class MemoryChange
{
    public ulong Address { get; init; }
    public byte[] Old { get; init; } = Array.Empty<byte>();
    public byte[] New { get; init; } = Array.Empty<byte>();
    public int Length => New.Length;

    public long? OldInt => AsInt(Old);
    public long? NewInt => AsInt(New);
    public float? OldFloat => Old.Length == 4 ? BitConverter.ToSingle(Old) : null;
    public float? NewFloat => New.Length == 4 ? BitConverter.ToSingle(New) : null;

    private static long? AsInt(byte[] b) => b.Length switch
    {
        1 => b[0],
        2 => BitConverter.ToInt16(b),
        4 => BitConverter.ToInt32(b),
        8 => BitConverter.ToInt64(b),
        _ => null,
    };
}
