using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Cheats;

/// <summary>
/// A per-game trainer: the process to attach to plus its list of cheats. Owns the two
/// pieces of shared machinery every trainer needs — the freeze loop (one timer re-writing
/// all frozen values) and clean teardown (restore every patched byte before detaching, or
/// the game can crash on exit). Also watches for the process vanishing so it never writes
/// to dead memory.
///
/// The <see cref="Cheats"/> collection is observable and each cheat raises change
/// notifications, so this object binds directly as a UI's data context: a list of
/// checkboxes over <see cref="Cheats"/>, each bound to <c>Enabled</c>.
/// </summary>
public sealed class Trainer : IDisposable
{
    public string Game { get; }
    public string ProcessName { get; }
    public ProcessMemory? Memory { get; private set; }
    public bool IsAttached => Memory?.IsAttached == true;

    /// <summary>How often frozen values are re-written, in milliseconds. ~40ms (25Hz) is plenty.</summary>
    public int FreezeIntervalMs { get; init; } = 40;

    public ObservableCollection<Cheat> Cheats { get; } = new();

    /// <summary>Raised when the target process exits while attached (so a UI can reset).</summary>
    public event EventHandler? Detached;

    private readonly ConcurrentDictionary<Cheat, Action> _freezes = new();
    private Timer? _timer;
    private int _ticking;

    public Trainer(string game, string processName)
    {
        Game = game;
        ProcessName = processName;
    }

    /// <summary>Register a cheat with this trainer. Returns it for fluent setup.</summary>
    public TCheat Add<TCheat>(TCheat cheat) where TCheat : Cheat
    {
        cheat.Owner = this;
        Cheats.Add(cheat);
        return cheat;
    }

    /// <summary>Attach to the running game. Returns false if it isn't running (poll and retry).</summary>
    public bool Attach()
    {
        if (IsAttached) return true;
        Memory = ProcessMemory.Attach(ProcessName);
        if (Memory is null) return false;
        _timer = new Timer(_ => Tick(), null, FreezeIntervalMs, FreezeIntervalMs);
        return true;
    }

    internal void RegisterFreeze(Cheat cheat, Action tick) => _freezes[cheat] = tick;
    internal void UnregisterFreeze(Cheat cheat) => _freezes.TryRemove(cheat, out _);

    private void Tick()
    {
        // Skip if a previous tick is still running (slow scan, paused game) — no overlap.
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (Memory is null) return;
            if (!Memory.IsAttached) { HandleProcessGone(); return; }

            foreach (var action in _freezes.Values)
            {
                try { action(); }
                catch { /* one bad cheat must not kill the loop for the others */ }
            }
        }
        finally { Interlocked.Exchange(ref _ticking, 0); }
    }

    private void HandleProcessGone()
    {
        _timer?.Dispose();
        _timer = null;
        _freezes.Clear();
        // Process is dead — reset flags but do NOT try to restore bytes (there's nothing to write to).
        foreach (var cheat in Cheats)
            cheat.MarkDisabledExternally();
        Detached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Turn everything off, restoring patched bytes. Safe to call anytime.</summary>
    public void DisableAll()
    {
        foreach (var cheat in Cheats)
        {
            try { cheat.Disable(); }
            catch { /* best-effort restore across all cheats */ }
        }
    }

    /// <summary>
    /// Stop the engine but keep the trainer and its cheat list intact, so it can be
    /// re-attached later (the UI's Start/Stop). Restores patched bytes while the process
    /// is still alive, then releases the handle. <see cref="Dispose"/> is the terminal form.
    /// </summary>
    public void Detach()
    {
        _timer?.Dispose();
        _timer = null;
        if (IsAttached)
            DisableAll();          // restore original bytes while the process is alive
        _freezes.Clear();
        Memory?.Dispose();
        Memory = null;
        foreach (var cheat in Cheats)
            cheat.MarkDisabledExternally();
    }

    public void Dispose() => Detach();
}
