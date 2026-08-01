using System.ComponentModel;
using GameCheater.Core.Cheats;

namespace GameCheater.App.ViewModels;

/// <summary>
/// Wraps a <see cref="Cheat"/> for the UI. The engine stays the source of truth: the
/// checkbox binds to <see cref="IsEnabled"/>, whose setter enables/disables the real cheat
/// and, if that throws (e.g. a placeholder signature won't resolve), reports the error and
/// snaps the checkbox back to the cheat's actual state instead of lying.
/// </summary>
public sealed class CheatViewModel : ViewModelBase
{
    private readonly Cheat _cheat;
    private readonly Action<string> _report;

    public CheatViewModel(Cheat cheat, Action<string> report)
    {
        _cheat = cheat;
        _report = report;
        _cheat.PropertyChanged += OnCheatChanged;
    }

    public string Name => _cheat.Name;
    public string Category => _cheat.Category;
    public string? Description => _cheat.Description;
    public bool HasValue => _cheat is IValueCheat;

    public bool IsEnabled
    {
        get => _cheat.Enabled;
        set
        {
            if (value == _cheat.Enabled) return;
            try
            {
                if (value) _cheat.Enable();
                else _cheat.Disable();
            }
            catch (Exception ex)
            {
                _report($"{_cheat.Name}: {ex.Message}");
            }
            OnPropertyChanged(); // reflect the REAL state (unchanged if enable failed)
        }
    }

    public string ValueText
    {
        get => (_cheat as IValueCheat)?.ValueText ?? "";
        set { if (_cheat is IValueCheat v) v.ValueText = value; }
    }

    private void OnCheatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Cheat.Enabled))
            OnPropertyChanged(nameof(IsEnabled));
        else if (e.PropertyName == nameof(IValueCheat.ValueText))
            OnPropertyChanged(nameof(ValueText));
    }
}
