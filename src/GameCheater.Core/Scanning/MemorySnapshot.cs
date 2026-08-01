using GameCheater.Core.Memory;

namespace GameCheater.Core.Scanning;

/// <summary>
/// A captured copy of selected regions of the target's memory, and the ability to diff that
/// capture against memory later. This is the oracle's core: snapshot before an external
/// trainer acts, then diff after, and what changed is what the trainer hit.
///
/// Filter by region protection at capture time — executable pages for code-patch cheats
/// (small, and code doesn't change normally so the diff is clean), writable pages for value
/// cheats (larger and noisier; pair with the "changed then held / increasing" logic).
/// </summary>
public sealed class MemorySnapshot
{
    private readonly List<(ulong Base, byte[] Bytes)> _blocks = new();
    public long TotalBytes { get; private set; }
    public int BlockCount => _blocks.Count;

    /// <summary>The captured blocks — used by the value scanner to compute survivors without
    /// storing a candidate record per value (an unknown-value scan can be billions of values).</summary>
    internal IReadOnlyList<(ulong Base, byte[] Bytes)> Blocks => _blocks;

    public static MemorySnapshot CaptureCode(ProcessMemory mem) => Capture(mem, r => r.IsExecutable);
    public static MemorySnapshot CaptureWritable(ProcessMemory mem) => Capture(mem, r => r.IsWritable);

    /// <summary>Capture every region matching <paramref name="filter"/>, chunked and capped for safety.</summary>
    public static MemorySnapshot Capture(ProcessMemory mem, Func<MemoryRegion, bool> filter,
        long perRegionCap = 256L * 1024 * 1024, long totalCap = 2L * 1024 * 1024 * 1024)
    {
        var snap = new MemorySnapshot();
        const int chunk = 1 << 20;
        var buf = new byte[chunk];

        foreach (var region in mem.EnumerateRegions())
        {
            if (!filter(region)) continue;

            int size = (int)Math.Min(region.Size, (ulong)perRegionCap);
            if (size <= 0) continue;

            var regionBuf = new byte[size];
            int got = 0;
            while (got < size)
            {
                int want = Math.Min(chunk, size - got);
                if (!mem.TryReadBytes(region.Base + (ulong)got, buf, want))
                    break; // hit an unreadable page — keep the readable prefix
                Array.Copy(buf, 0, regionBuf, got, want);
                got += want;
            }

            if (got > 0)
            {
                snap._blocks.Add((region.Base, got == size ? regionBuf : regionBuf[..got]));
                snap.TotalBytes += got;
            }

            if (snap.TotalBytes >= totalCap) break;
        }

        return snap;
    }

    /// <summary>Diff this snapshot against current memory; returns each contiguous changed run.</summary>
    public List<MemoryChange> DiffAgainstCurrent(ProcessMemory mem, int joinGap = 7)
    {
        var changes = new List<MemoryChange>();
        foreach (var (baseAddr, oldBytes) in _blocks)
        {
            var cur = new byte[oldBytes.Length];
            if (!mem.TryReadBytes(baseAddr, cur, oldBytes.Length))
                continue; // region unmapped now — skip
            CollectRuns(baseAddr, oldBytes, cur, joinGap, changes);
        }
        return changes;
    }

    /// <summary>Read the original (snapshot-time) bytes at an address, if within a captured block.</summary>
    public bool TryGetOriginal(ulong address, int length, out byte[] bytes)
    {
        foreach (var (baseAddr, b) in _blocks)
        {
            if (address >= baseAddr && address + (ulong)length <= baseAddr + (ulong)b.Length)
            {
                int start = (int)(address - baseAddr);
                bytes = b[start..(start + length)];
                return true;
            }
        }
        bytes = Array.Empty<byte>();
        return false;
    }

    // Group differing bytes into runs, tolerating small equal gaps so a multi-byte value
    // (e.g. a 4-byte int whose middle bytes happen to match) stays a single change.
    private static void CollectRuns(ulong baseAddr, byte[] a, byte[] b, int joinGap, List<MemoryChange> outList)
    {
        int i = 0;
        int n = a.Length;
        while (i < n)
        {
            if (a[i] == b[i]) { i++; continue; }

            int start = i;
            int lastDiff = i;
            int j = i + 1;
            while (j < n && (j - lastDiff) <= joinGap)
            {
                if (a[j] != b[j]) lastDiff = j;
                j++;
            }

            int end = lastDiff + 1; // inclusive of the last differing byte
            outList.Add(new MemoryChange
            {
                Address = baseAddr + (ulong)start,
                Old = a[start..end],
                New = b[start..end],
            });
            i = end;
        }
    }
}
