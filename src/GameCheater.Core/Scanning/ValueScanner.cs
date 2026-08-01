using System.Runtime.InteropServices;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Scanning;

/// <summary>
/// The discovery tool — a Cheat-Engine-style value scanner. This is how cheats become
/// *yours* instead of copied: you don't look an address up, you find it live.
///
/// Two scan strategies, picked automatically:
///  - Known value (<see cref="FirstScan"/>): store only the matching addresses — tiny.
///  - Unknown value (<see cref="FirstScanUnknown"/>): a full scan can be *billions* of
///    values, so we do NOT store a record per value. We snapshot writable memory once, and
///    the first narrowing pass (increased/decreased/changed) computes the surviving addresses
///    from that snapshot. After that we're back to a small address list.
///
/// Only WRITABLE memory is scanned — game values live in writable data, and this excludes
/// gigabytes of code and mapped files that otherwise explode the candidate count.
/// </summary>
public sealed class ValueScanner<T> where T : unmanaged, IComparable<T>
{
    private readonly ProcessMemory _memory;
    private readonly int _size = Marshal.SizeOf<T>();

    // Narrowed candidates (known-value scan, or after the first unknown narrowing).
    private List<ulong> _addresses = new();
    private List<T> _lastValues = new();

    // Unknown-value scan: raw writable memory captured once, plus the (approx) value count.
    private MemorySnapshot? _snapshot;
    private long _snapshotValueCount;

    /// <summary>Byte alignment for scanning. 4 matches Cheat Engine's "fast scan" default.</summary>
    public int Alignment { get; init; } = 4;

    /// <summary>Cap on the unknown-scan snapshot (raw writable bytes held in RAM).</summary>
    public long SnapshotCap { get; init; } = 3L * 1024 * 1024 * 1024;

    public bool InSnapshotMode => _snapshot is not null;

    /// <summary>Narrowed candidate count (0 right after an unknown scan, until the first narrow).</summary>
    public int Count => _addresses.Count;

    /// <summary>Total live candidates for display — the snapshot value count while unknown, else the list.</summary>
    public long CandidateCount => _snapshot is not null ? _snapshotValueCount : _addresses.Count;

    public bool HasResults => _addresses.Count > 0;
    public IReadOnlyList<ulong> Results => _addresses;

    public ValueScanner(ProcessMemory memory) => _memory = memory;

    // --- first scans ---

    /// <summary>Find every writable address currently holding exactly <paramref name="target"/>.</summary>
    public int FirstScan(T target)
    {
        Reset();
        var comparer = EqualityComparer<T>.Default;
        SweepWritable((addr, value) =>
        {
            if (comparer.Equals(value, target))
            {
                _addresses.Add(addr);
                _lastValues.Add(value);
            }
        });
        return Count;
    }

    /// <summary>
    /// Start an "unknown initial value" hunt: snapshot writable memory (raw), then narrow with
    /// <see cref="NextScanIncreased"/>/<see cref="NextScanDecreased"/>/<see cref="NextScanChanged"/>.
    /// Returns the approximate number of values covered.
    /// </summary>
    public long FirstScanUnknown()
    {
        Reset();
        _snapshot = MemorySnapshot.Capture(_memory, r => r.IsReadable && r.IsWritable, totalCap: SnapshotCap);
        _snapshotValueCount = 0;
        foreach (var (_, bytes) in _snapshot.Blocks)
            if (bytes.Length >= _size)
                _snapshotValueCount += (bytes.Length - _size) / Alignment + 1;
        return _snapshotValueCount;
    }

    // --- next scans ---

    public int NextScanExact(T target)
    {
        var comparer = EqualityComparer<T>.Default;
        return Refine((cur, _) => comparer.Equals(cur, target));
    }

    public int NextScanChanged() => Refine((cur, prev) => cur.CompareTo(prev) != 0);
    public int NextScanUnchanged() => Refine((cur, prev) => cur.CompareTo(prev) == 0);
    public int NextScanIncreased() => Refine((cur, prev) => cur.CompareTo(prev) > 0);
    public int NextScanDecreased() => Refine((cur, prev) => cur.CompareTo(prev) < 0);
    public int NextScanGreaterThan(T value) => Refine((cur, _) => cur.CompareTo(value) > 0);
    public int NextScanLessThan(T value) => Refine((cur, _) => cur.CompareTo(value) < 0);

    public T ReadCurrent(ulong address) => _memory.Read<T>(address);

    public void Reset()
    {
        _addresses = new();
        _lastValues = new();
        _snapshot = null;
        _snapshotValueCount = 0;
    }

    // --- internals ---

    // Route a narrowing to snapshot-mode (first narrow after unknown) or list-mode.
    private int Refine(Func<T, T, bool> keep) =>
        _snapshot is not null ? RefineFromSnapshot(keep) : RefineList(keep);

    // First narrow after an unknown scan: walk the snapshot, compare each value to current
    // memory, and materialize only the survivors into the address list.
    private int RefineFromSnapshot(Func<T, T, bool> keep)
    {
        var addrs = new List<ulong>();
        var vals = new List<T>();

        foreach (var (baseAddr, oldBytes) in _snapshot!.Blocks)
        {
            var cur = new byte[oldBytes.Length];
            if (!_memory.TryReadBytes(baseAddr, cur, oldBytes.Length))
                continue;

            for (int off = 0; off + _size <= oldBytes.Length; off += Alignment)
            {
                T oldValue = MemoryMarshal.Read<T>(oldBytes.AsSpan(off, _size));
                T curValue = MemoryMarshal.Read<T>(cur.AsSpan(off, _size));
                if (keep(curValue, oldValue))
                {
                    addrs.Add(baseAddr + (ulong)off);
                    vals.Add(curValue);
                }
            }
        }

        _snapshot = null;   // out of snapshot mode — now a normal candidate list
        _snapshotValueCount = 0;
        _addresses = addrs;
        _lastValues = vals;
        return Count;
    }

    private int RefineList(Func<T, T, bool> keep)
    {
        var nextAddrs = new List<ulong>(_addresses.Count);
        var nextVals = new List<T>(_addresses.Count);
        var buffer = new byte[_size];

        for (int i = 0; i < _addresses.Count; i++)
        {
            if (!_memory.TryReadBytes(_addresses[i], buffer, _size))
                continue; // candidate memory got unmapped — drop it
            T cur = MemoryMarshal.Read<T>(buffer);
            if (keep(cur, _lastValues[i]))
            {
                nextAddrs.Add(_addresses[i]);
                nextVals.Add(cur);
            }
        }

        _addresses = nextAddrs;
        _lastValues = nextVals;
        return Count;
    }

    // Sweep writable regions, visiting each aligned value (used by known-value first scan).
    private void SweepWritable(Action<ulong, T> visit)
    {
        const int chunk = 0x10000;
        var buffer = new byte[chunk];

        foreach (var region in _memory.EnumerateRegions())
        {
            if (!region.IsReadable || !region.IsWritable)
                continue;

            ulong regionSize = region.Size;
            ulong offset = 0;
            while (offset < regionSize)
            {
                int want = (int)Math.Min((ulong)chunk, regionSize - offset);
                if (!_memory.TryReadBytes(region.Base + offset, buffer, want))
                {
                    offset += (ulong)want;
                    continue;
                }

                for (int i = 0; i + _size <= want; i += Alignment)
                {
                    T value = MemoryMarshal.Read<T>(buffer.AsSpan(i, _size));
                    visit(region.Base + offset + (ulong)i, value);
                }

                offset += (ulong)want;
            }
        }
    }
}
