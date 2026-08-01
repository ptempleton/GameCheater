using System.Collections.ObjectModel;
using GameCheater.App.Models;
using GameCheater.Core.Cheats;
using GameCheater.Core.Definitions;
using GameCheater.Core.Distribution;
using GameCheater.Core.Tables;

namespace GameCheater.App.ViewModels;

/// <summary>
/// The shell's main view model: pick a game → Start/Stop the engine → toggle cheats, load a
/// .CT, and Refresh trainers from the cheats repo. A thin coordinator over the Core runtime.
/// </summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly CheatRepositoryClient _repo = new();
    private Trainer? _trainer;

    public ObservableCollection<GameDef> Games { get; } = new();
    public ObservableCollection<CheatViewModel> Cheats { get; } = new();
    public ObservableCollection<CheatGroupViewModel> CheatGroups { get; } = new();
    public RelayCommand ToggleEngineCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>The "Capture" tab — point-and-click value scanning over the attached game.</summary>
    public CaptureViewModel Capture { get; }

    public MainViewModel()
    {
        ToggleEngineCommand = new RelayCommand(ToggleEngine, () => _trainer is not null);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        Capture = new CaptureViewModel(
            memory: () => _trainer?.Memory,
            trainer: () => _trainer,
            addCheat: cheat => Cheats.Add(new CheatViewModel(cheat, s => Status = s)),
            gameName: () => _trainer?.Game ?? SelectedGame?.Display ?? "Game");

        // Keep the grouped view in sync as cheats are added/cleared.
        Cheats.CollectionChanged += (_, _) => RebuildGroups();

        // Start from the embedded baseline + whatever we cached last run, then pull fresh.
        RebuildGames(_repo.LoadCached());
        _ = RefreshAsync();
    }

    /// <summary>Rebuild the collapsible category groups shown in the Cheats tab.</summary>
    private void RebuildGroups()
    {
        CheatGroups.Clear();
        foreach (var group in Cheats.GroupBy(c => c.Category).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var vm = new CheatGroupViewModel(group.Key);
            foreach (var cheat in group)
                vm.Cheats.Add(cheat);
            CheatGroups.Add(vm);
        }
    }

    /// <summary>
    /// Rebuild the game picker: embedded baseline (GameCatalog) overlaid by fetched
    /// definitions (fetched wins). Preserves the current selection by name.
    /// </summary>
    private void RebuildGames(IEnumerable<TrainerDefinition> defs)
    {
        var map = new Dictionary<string, GameDef>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in GameCatalog.All)
            map[g.Display] = g;
        foreach (var d in defs)
            map[d.Game] = new GameDef(d.Game, d.Process, () => TrainerDefinitionLoader.ToTrainer(d));

        var previouslySelected = SelectedGame?.Display;
        Games.Clear();
        foreach (var g in map.Values.OrderBy(g => g.Display, StringComparer.OrdinalIgnoreCase))
            Games.Add(g);

        SelectedGame = Games.FirstOrDefault(g => g.Display == previouslySelected) ?? Games.FirstOrDefault();
    }

    /// <summary>Pull the latest definitions from the cheats repo and update the picker live (no restart).</summary>
    public async Task RefreshAsync()
    {
        Status = "Refreshing trainers…";
        var result = await _repo.RefreshAsync();
        RebuildGames(result.Definitions);
        Status = result.Error is null
            ? $"Trainers up to date — {result.Count} game definition(s) from the cheats repo."
            : $"Offline — using cached trainers ({result.Count}).";
    }

    private GameDef? _selectedGame;
    public GameDef? SelectedGame
    {
        get => _selectedGame;
        set { if (SetField(ref _selectedGame, value)) OnGameChanged(); }
    }

    private string _status = "Select a game to begin.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private string _report = "";
    public string Report { get => _report; private set => SetField(ref _report, value); }

    private bool _isAttached;
    public bool IsAttached
    {
        get => _isAttached;
        private set
        {
            if (SetField(ref _isAttached, value))
                OnPropertyChanged(nameof(EngineButtonText));
        }
    }

    public string EngineButtonText => IsAttached ? "Stop Engine" : "Start Engine";
    public string? ProcessName => _trainer?.ProcessName;

    private void OnGameChanged()
    {
        _trainer?.Dispose();          // tear down any previous session (restores patches)
        IsAttached = false;
        Cheats.Clear();
        Report = "";

        if (SelectedGame is null)
        {
            _trainer = null;
            ToggleEngineCommand.RaiseCanExecuteChanged();
            return;
        }

        _trainer = SelectedGame.Build();
        _trainer.Detached += (_, _) =>
        {
            IsAttached = false;
            Status = $"{SelectedGame.Display} closed — detached.";
        };

        foreach (var cheat in _trainer.Cheats)
            Cheats.Add(new CheatViewModel(cheat, s => Status = s));

        Status = Cheats.Count > 0
            ? $"{SelectedGame.Display}: {Cheats.Count} cheats loaded. Start the game, then Start Engine."
            : $"{SelectedGame.Display}: no authored cheats yet — load a .CT or use the scanner.";

        OnPropertyChanged(nameof(ProcessName));
        ToggleEngineCommand.RaiseCanExecuteChanged();
    }

    private void ToggleEngine()
    {
        if (_trainer is null) return;

        if (IsAttached)
        {
            _trainer.Detach();
            IsAttached = false;
            Status = "Engine stopped.";
            return;
        }

        try
        {
            if (_trainer.Attach())
            {
                IsAttached = true;
                Status = $"Attached to {_trainer.ProcessName}.exe.";
            }
            else
            {
                Status = $"{_trainer.ProcessName}.exe is not running — start the game first. " +
                         "(Attaching only works on Windows.)";
            }
        }
        catch (Exception ex)
        {
            Status = $"Attach failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Parse a user-supplied .CT, add the convertible value/pointer entries as live cheats
    /// on the current trainer, and show the routing report for the rest.
    /// </summary>
    public void LoadTable(string path)
    {
        if (_trainer is null)
        {
            Status = "Pick a game before loading a table.";
            return;
        }

        try
        {
            var table = CtParser.ParseFile(path);
            var loaded = CtLoader.Build(table);

            foreach (var cheat in loaded.Cheats)
            {
                _trainer.Add(cheat);                                  // register so the freeze loop drives it
                Cheats.Add(new CheatViewModel(cheat, s => Status = s));
            }

            var lines = new List<string>
            {
                $"{loaded.Cheats.Count} added as cheats, {loaded.Scripts} need CE backend, " +
                $"{loaded.Unconverted.Count} unconverted.",
                "",
            };
            if (loaded.Scripts > 0)
                lines.Add($"CE backend (Lua/AA): {loaded.Scripts} entries");
            if (loaded.Unconverted.Count > 0)
                lines.Add($"Unconverted: {string.Join(", ", loaded.Unconverted)}");

            Report = string.Join("\n", lines);
            Status = $"Loaded {Path.GetFileName(path)} — added {loaded.Cheats.Count} cheats to {_trainer.Game}.";
        }
        catch (Exception ex)
        {
            Status = $"Failed to load table: {ex.Message}";
        }
    }
}
