using System.ComponentModel;
using System.Runtime.CompilerServices;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Cheats;

/// <summary>
/// Layer 2 — a single toggleable cheat. This is the unit the whole product is built
/// around: your own authored cheats and cheats loaded from a .CT both become one of
/// these. It implements <see cref="INotifyPropertyChanged"/> so a UI checkbox can bind
/// directly to <see cref="Enabled"/> and reflect the *real* engine state — if an enable
/// fails, the box doesn't lie and flip.
///
/// The golden rule lives here: addresses are resolved inside OnEnable (per-session,
/// post-ASLR), never stored across launches. Subclasses implement the actual apply/undo.
/// </summary>
public abstract class Cheat : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public string Category { get; init; } = "General";
    public string? Description { get; init; }

    /// <summary>Set by <see cref="Trainer.Add"/>; gives the cheat access to the attached process.</summary>
    internal Trainer? Owner { get; set; }

    /// <summary>The live process handle. Throws if the cheat was enabled before attaching.</summary>
    protected ProcessMemory Memory =>
        Owner?.Memory ?? throw new InvalidOperationException(
            $"Cheat '{Name}' has no attached process. Call Trainer.Attach() first.");

    private bool _enabled;
    public bool Enabled
    {
        get => _enabled;
        private set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Optional global hotkey that toggles this cheat, e.g. "F1" (null = none).</summary>
    private string? _hotKey;
    public string? HotKey
    {
        get => _hotKey;
        set
        {
            if (_hotKey == value) return;
            _hotKey = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Apply the cheat. Resolves its address now, applies, and records undo state.</summary>
    public void Enable()
    {
        if (Enabled) return;
        OnEnable();       // if this throws, Enabled stays false — UI stays honest
        Enabled = true;
    }

    /// <summary>Revert the cheat: stop freezing / restore original bytes.</summary>
    public void Disable()
    {
        if (!Enabled) return;
        try { OnDisable(); }
        finally { Enabled = false; }
    }

    public void Toggle()
    {
        if (Enabled) Disable();
        else Enable();
    }

    /// <summary>
    /// Called by the Trainer when the target process vanished. We can't (and mustn't)
    /// write to dead memory, so just reset the flag; the freeze registry is already gone.
    /// </summary>
    internal void MarkDisabledExternally() => Enabled = false;

    protected abstract void OnEnable();
    protected abstract void OnDisable();

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
