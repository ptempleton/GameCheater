using GameCheater.Core.Memory;

namespace GameCheater.Core.Scanning;

/// <summary>A ready-to-author code patch discovered by diffing before/after an external trainer.</summary>
public sealed class CodePatchSuggestion
{
    public ulong Address { get; init; }
    public byte[] Original { get; init; } = Array.Empty<byte>();
    public byte[] Patched { get; init; } = Array.Empty<byte>();

    /// <summary>AOB pattern over the ORIGINAL bytes around the site — what to scan for at enable time.</summary>
    public string Signature { get; init; } = "";

    /// <summary>Offset of the patch within a <see cref="Signature"/> match.</summary>
    public int PatchOffset { get; init; }

    /// <summary>A copy-pasteable PatchCheat for a trainer definition.</summary>
    public string ToCSharp()
    {
        string bytes = string.Join(", ", Array.ConvertAll(Patched, b => "0x" + b.ToString("X2")));
        return $"new PatchCheat(Resolve.Aob(\"{Signature}\", {PatchOffset}), new byte[] {{ {bytes} }}) " +
               "{ Name = \"...\", Category = \"...\" }";
    }
}

/// <summary>
/// Turns raw memory diffs into actionable cheat suggestions — the "capture" half of the
/// watch-an-external-trainer workflow.
/// </summary>
public static class Oracle
{
    /// <summary>
    /// Build a durable code-patch suggestion from a diff hit: an AOB signature over the
    /// original bytes (with <paramref name="context"/> bytes of surrounding code for
    /// uniqueness) plus the exact patch bytes the trainer wrote.
    /// </summary>
    public static CodePatchSuggestion BuildCodeSuggestion(MemorySnapshot codeBaseline, MemoryChange change, int context = 12)
    {
        ulong ctxStart = change.Address - (ulong)context;
        int total = context + change.Old.Length + context;

        byte[] sigBytes;
        int offset;
        if (codeBaseline.TryGetOriginal(ctxStart, total, out var window))
        {
            sigBytes = window;
            offset = context;
        }
        else
        {
            // Couldn't grab surrounding context — fall back to just the changed bytes.
            sigBytes = change.Old;
            offset = 0;
        }

        return new CodePatchSuggestion
        {
            Address = change.Address,
            Original = change.Old,
            Patched = change.New,
            Signature = Signature.ToPattern(sigBytes),
            PatchOffset = offset,
        };
    }
}
