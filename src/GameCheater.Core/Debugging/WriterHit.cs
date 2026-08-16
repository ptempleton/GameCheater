using GameCheater.Core.Cheats;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Debugging;

/// <summary>
/// One instruction caught writing to the watched address, plus how often it did so.
/// Hits are aggregated per writer instruction — a value is usually touched by two or three
/// distinct sites (the tick that consumes it, the one that refills it, a UI mirror), and
/// telling those apart by hit count is how you pick the one worth patching.
/// </summary>
public sealed class WriterHit
{
    /// <summary>
    /// A stable 1-based number assigned when this writer is first seen. The report sorts by
    /// hit count and therefore re-orders as the game runs, so commands have to key off this
    /// rather than a row position — otherwise "restore #1" restores whatever floated to the
    /// top since you last looked.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>The instruction that performed the store.</summary>
    public required X64Instruction Instruction { get; init; }

    /// <summary>Absolute address of the writing instruction this session (not ASLR-safe).</summary>
    public ulong Address => Instruction.Address;

    /// <summary>Module + offset for the writer, when it lives in a loaded module.</summary>
    public string? Module { get; init; }
    public ulong ModuleOffset { get; init; }

    /// <summary>Thread that first tripped the breakpoint here.</summary>
    public uint ThreadId { get; init; }

    /// <summary>How many times this instruction has written the watched bytes.</summary>
    public int HitCount { get; internal set; }

    /// <summary>The watched bytes as they read immediately after the most recent write.</summary>
    public byte[] LastValue { get; internal set; } = Array.Empty<byte>();

    /// <summary>Best-effort interpretation of <see cref="LastValue"/> for display.</summary>
    public string DescribeValue()
    {
        var v = LastValue;
        return v.Length switch
        {
            >= 8 => $"i64 {BitConverter.ToInt64(v):N0}   f64 {BitConverter.ToDouble(v):G6}   " +
                    $"i32 {BitConverter.ToInt32(v):N0}   f32 {BitConverter.ToSingle(v):G6}",
            >= 4 => $"i32 {BitConverter.ToInt32(v):N0}   f32 {BitConverter.ToSingle(v):G6}",
            >= 2 => $"i16 {BitConverter.ToInt16(v)}",
            1 => $"u8 {v[0]}",
            _ => "(unreadable)",
        };
    }

    public string Where => Module is null
        ? $"0x{Address:X}"
        : $"{Module}+0x{ModuleOffset:X}";
}

/// <summary>
/// A ready-to-author code patch that disables a writer: the AOB signature to find the
/// instruction again after a relaunch, and the NOPs to overwrite it with. This is the
/// payoff of the whole exercise — the durable form of "stop the code that drains fuel".
/// </summary>
public sealed class WriterPatch
{
    public required X64Instruction Writer { get; init; }

    /// <summary>AOB over the original bytes around the writer, with its displacement and
    /// immediate wildcarded so the pattern survives data moving between builds.</summary>
    public required string Signature { get; init; }

    /// <summary>Offset of the writer instruction within a <see cref="Signature"/> match.</summary>
    public required int PatchOffset { get; init; }

    /// <summary>The bytes to write — a NOP for every byte of the writer instruction.</summary>
    public required byte[] PatchBytes { get; init; }

    /// <summary>The module the writer lives in, or null when it sits in dynamically
    /// generated code (a JIT, or an unpacked/allocated block) that no AOB can re-find.</summary>
    public required string? Module { get; init; }

    /// <summary>
    /// How many times <see cref="Signature"/> matches inside <see cref="Module"/>, counted only
    /// as far as 2 (the scan stops early — we only need to know whether it is ambiguous).
    /// 1 is what you want; 2 means the pattern could silently patch the wrong site.
    /// </summary>
    public required int MatchCount { get; init; }

    /// <summary>True when this is safe to ship: it lives in a module and its signature is unique.</summary>
    public bool IsUnique => Module is not null && MatchCount == 1;

    /// <summary>Why this patch isn't durable, or null when it is fine.</summary>
    public string? Warning => Module switch
    {
        null => "the writer is in dynamically generated code, not a module — an AOB cannot " +
                "find it again after a relaunch, so this can only be used in this session",
        _ => MatchCount switch
        {
            1 => null,
            0 => "the generated signature didn't match anywhere on re-scan — treat it as unusable",
            _ => "the signature matches more than once, so it could patch the wrong site",
        },
    };

    /// <summary>A copy-pasteable cheat for a hand-written trainer.</summary>
    public string ToCSharp() =>
        $"new PatchCheat(Resolve.Aob(\"{Signature}\", {PatchOffset}), PatchCheat.Nops({PatchBytes.Length})) " +
        "{ Name = \"...\", Category = \"...\" }";

    /// <summary>
    /// Build the patch for <paramref name="writer"/>: take a window of surrounding code,
    /// wildcard every address-sized field in it, and widen until the signature matches exactly
    /// once. Both halves matter. Wildcarding is what makes the pattern survive a relaunch — the
    /// loader rewrites baked-in addresses when a module lands at a new base, so a literal
    /// disp32 would stop matching. Uniqueness is what makes it safe: a pattern that matches
    /// twice will eventually NOP the wrong code.
    /// </summary>
    public static WriterPatch? Build(ProcessMemory memory, X64Instruction writer)
    {
        // The signature is only meaningful inside the module the writer belongs to.
        bool inModule = memory.TryGetModuleRange(writer.Address, out string module,
            out ulong moduleBase, out ulong moduleSize);

        int[] contexts = { 8, 12, 16, 24, 32, 48 };
        WriterPatch? widest = null;

        foreach (int context in contexts)
        {
            ulong start = writer.Address - (ulong)context;
            int total = context + writer.Length + context;
            var window = new byte[total];
            if (!memory.TryReadBytes(start, window, total))
                continue;

            string pattern = BuildPattern(window, start, writer, context);

            int matches = inModule
                ? new Signature(pattern).ScanAll(memory, moduleBase, moduleSize).Take(2).Count()
                : 0;

            widest = new WriterPatch
            {
                Writer = writer,
                Signature = pattern,
                PatchOffset = context,
                PatchBytes = PatchCheat.Nops(writer.Length),
                Module = inModule ? module : null,
                MatchCount = matches,
            };

            if (inModule && matches == 1)
                return widest;

            if (!inModule)
                return widest;   // no module to disambiguate against — widening won't help
        }

        return widest;
    }

    /// <summary>
    /// Render the window as an AOB, blanking the address-sized fields of every instruction in
    /// it. Instruction boundaries are recovered by decoding forward from the writer and by
    /// walking backwards through the leading context the same way a trapped RIP is resolved.
    /// Any byte we can't attribute to a decoded instruction is left literal, which is the safe
    /// direction to fail: an over-specific pattern misses, an over-wildcarded one mis-matches.
    /// </summary>
    private static string BuildPattern(byte[] window, ulong windowStart, X64Instruction writer, int context)
    {
        var tokens = new string[window.Length];
        for (int i = 0; i < window.Length; i++)
            tokens[i] = window[i].ToString("X2");

        writer.ApplyWildcards(tokens, context, minFieldBytes: 4);

        // Backwards through the leading context, one instruction at a time.
        ulong at = writer.Address;
        while (at > windowStart)
        {
            var previous = X64Decoder.FindWriterEndingAt(window, windowStart, at,
                maxLookback: 32, preferMemoryWriters: false);
            if (previous is null || previous.Address < windowStart)
                break;
            previous.ApplyWildcards(tokens, (int)(previous.Address - windowStart), minFieldBytes: 4);
            at = previous.Address;
        }

        // Forwards through the trailing context.
        ulong next = writer.EndAddress;
        ulong windowEnd = windowStart + (ulong)window.Length;
        while (next < windowEnd)
        {
            var following = X64Decoder.Decode(window, (int)(next - windowStart), windowStart);
            if (following is null)
                break;
            following.ApplyWildcards(tokens, (int)(following.Address - windowStart), minFieldBytes: 4);
            next = following.EndAddress;
        }

        return string.Join(' ', tokens);
    }
}
