using System.Globalization;
using System.Text;

namespace GameCheater.Core.Memory;

/// <summary>
/// An AOB (array-of-bytes) signature — a byte pattern with wildcards used to locate
/// code or data regardless of ASLR. This is the durable way to pin a cheat: instead
/// of storing "money lives at 0x7FF6…" (invalid next launch), you store a byte pattern
/// that uniquely identifies the surrounding code, and re-scan for it every time.
///
/// Pattern syntax: space-separated hex bytes, with "??" or "?" as a wildcard.
///   e.g.  "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ??"
/// </summary>
public sealed class Signature
{
    private readonly byte[] _bytes;
    private readonly bool[] _mask; // true = must match, false = wildcard

    public string Pattern { get; }

    public Signature(string pattern)
    {
        Pattern = pattern;
        var tokens = pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        _bytes = new byte[tokens.Length];
        _mask = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] is "??" or "?")
            {
                _mask[i] = false;
            }
            else
            {
                _bytes[i] = byte.Parse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                _mask[i] = true;
            }
        }

        if (_bytes.Length == 0)
            throw new ArgumentException("Signature pattern is empty.", nameof(pattern));
    }

    /// <summary>
    /// Scan the process's main module for the first match. Returns the absolute address
    /// of the match start, or null if not found. Reads region-by-region so guard pages
    /// don't abort the scan.
    /// </summary>
    public ulong? Scan(ProcessMemory memory)
        => Scan(memory, memory.MainModuleBase, (ulong)memory.MainModuleSize);

    public ulong? Scan(ProcessMemory memory, ulong start, ulong length)
    {
        // 64KB chunks with an overlap so a match straddling a chunk boundary isn't missed.
        const int chunk = 0x10000;
        int overlap = _bytes.Length - 1;
        var buffer = new byte[chunk];

        foreach (var (regionBase, regionSize) in memory.EnumerateReadableRegions(start, length))
        {
            int offset = 0;
            while (offset < regionSize)
            {
                int want = Math.Min(chunk, regionSize - offset);
                if (!memory.TryReadBytes(regionBase + (ulong)offset, buffer, want))
                {
                    offset += want;
                    continue;
                }

                int limit = want - _bytes.Length + 1;
                for (int i = 0; i < limit; i++)
                {
                    if (Matches(buffer, i))
                        return regionBase + (ulong)(offset + i);
                }

                if (want < chunk) break;
                offset += chunk - overlap; // step back by overlap to catch boundary-straddling matches
            }
        }
        return null;
    }

    private bool Matches(byte[] haystack, int at)
    {
        for (int i = 0; i < _bytes.Length; i++)
        {
            if (_mask[i] && haystack[at + i] != _bytes[i])
                return false;
        }
        return true;
    }

    /// <summary>Render bytes as a space-separated hex AOB pattern, e.g. <c>48 8B 05</c>.</summary>
    public static string ToPattern(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (int i = 0; i < bytes.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2"));
        }
        return sb.ToString();
    }

    /// <summary>
    /// Resolve an x64 RIP-relative reference embedded in the matched instruction.
    /// Many "static" pointers in tables are really a `mov reg,[rip+disp32]`; you match
    /// the instruction, then read the 4-byte displacement and compute the target:
    ///   target = matchAddress + instructionLength + disp32
    /// <paramref name="dispOffset"/> is the offset of the disp32 within the match;
    /// <paramref name="instructionLength"/> is the full instruction length.
    /// </summary>
    public static ulong ResolveRipRelative(ProcessMemory memory, ulong matchAddress,
        int dispOffset, int instructionLength)
    {
        int disp = memory.Read<int>(matchAddress + (ulong)dispOffset);
        return matchAddress + (ulong)instructionLength + (ulong)(long)disp;
    }
}
