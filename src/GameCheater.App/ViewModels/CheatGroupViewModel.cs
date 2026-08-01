using System.Collections.ObjectModel;

namespace GameCheater.App.ViewModels;

/// <summary>A category of cheats (e.g. "Vehicle", "Player") shown as a collapsible group,
/// matching the grouped trainer layout.</summary>
public sealed class CheatGroupViewModel : ViewModelBase
{
    public string Category { get; }
    public ObservableCollection<CheatViewModel> Cheats { get; } = new();

    private bool _isExpanded = true;
    public bool IsExpanded { get => _isExpanded; set => SetField(ref _isExpanded, value); }

    public CheatGroupViewModel(string category) => Category = category;

    public string Header => $"{Category}  ({Cheats.Count})";
}
