using System.Globalization;
using GameCheater.Core.Memory;

namespace GameCheater.Core.Cheats;

/// <summary>
/// A value-write cheat: infinite fuel, set money, max repair points, etc. "On" writes
/// <see cref="Value"/> to the target once and — if <c>freeze</c> — keeps re-writing it
/// on the Trainer's freeze loop so the game can't decrement it. "Off" stops writing and
/// lets the game own the value again.
///
/// <see cref="Value"/> is live-editable and raises change notifications, so a UI slider
/// or number box ("set money to X") two-way binds to it and edits take effect instantly
/// while enabled — this is the "adjust settings" case.
/// </summary>
public sealed class FreezeCheat<T> : Cheat, IValueCheat where T : unmanaged
{
    private readonly Func<ProcessMemory, ulong?> _resolve;
    private readonly bool _freeze;
    private readonly bool _resolveEachTick;
    private readonly bool _freezeAtCurrent;
    private ulong _address;
    private bool _userSetValue;

    private T _value;
    public T Value
    {
        get => _value;
        set
        {
            _userSetValue = true;      // once the user picks a value, stop auto-reading current
            if (EqualityComparer<T>.Default.Equals(_value, value)) return;
            _value = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ValueText));
            if (Enabled) TryWrite();   // live update while running
        }
    }

    /// <summary>String view of <see cref="Value"/> for UI binding (see <see cref="IValueCheat"/>).</summary>
    public string ValueText
    {
        get => Convert.ToString(_value, CultureInfo.InvariantCulture) ?? "";
        set
        {
            try { Value = (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture); }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
            {
                // Ignore un-parseable input; the box will revert to the last good value.
                OnPropertyChanged(nameof(ValueText));
            }
        }
    }

    /// <param name="resolver">How to locate the target address at enable time.</param>
    /// <param name="value">Initial value to write / hold.</param>
    /// <param name="freeze">True = keep re-writing on the loop; false = write once.</param>
    /// <param name="resolveEachTick">
    /// True for pointer-chain targets that can move (cheap to re-walk each tick); false
    /// for AOB-resolved targets (re-scanning every tick would be far too expensive).
    /// </param>
    /// <param name="freezeAtCurrentValue">
    /// True to read the target's current value on first enable and hold THAT, instead of
    /// <paramref name="value"/>. Used for cheats loaded from a .CT, where we don't know a
    /// target number — we just freeze whatever the game has until the user types one.
    /// </param>
    public FreezeCheat(Func<ProcessMemory, ulong?> resolver, T value,
        bool freeze = true, bool resolveEachTick = false, bool freezeAtCurrentValue = false)
    {
        _resolve = resolver;
        _value = value;
        _freeze = freeze;
        _resolveEachTick = resolveEachTick;
        _freezeAtCurrent = freezeAtCurrentValue;
    }

    protected override void OnEnable()
    {
        _address = _resolve(Memory)
            ?? throw new InvalidOperationException($"Cheat '{Name}': address failed to resolve.");

        // Freeze-at-current: capture the live value on first enable (unless the user already
        // picked one), so a loaded table entry holds the game's own value rather than 0.
        if (_freezeAtCurrent && !_userSetValue)
        {
            try
            {
                _value = Memory.Read<T>(_address);
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(ValueText));
            }
            catch (IOException) { /* couldn't read — fall back to the constructed value */ }
        }

        TryWrite();
        if (_freeze)
            Owner!.RegisterFreeze(this, Tick);
    }

    protected override void OnDisable()
    {
        if (_freeze)
            Owner!.UnregisterFreeze(this);
        // A value-write cheat intentionally does not restore the original value — there
        // was nothing to save; the game simply resumes owning the address.
    }

    private void Tick()
    {
        if (_resolveEachTick)
        {
            var addr = _resolve(Memory);
            if (addr is null) return;   // struct not present this frame — skip, retry next tick
            _address = addr.Value;
        }
        TryWrite();
    }

    private void TryWrite()
    {
        try { Memory.Write(_address, _value); }
        catch (IOException) { /* transient (loading screen, unmap) — next tick retries */ }
    }
}
