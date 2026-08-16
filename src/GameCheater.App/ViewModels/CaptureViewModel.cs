using System.Collections.ObjectModel;
using GameCheater.Core.Cheats;
using GameCheater.Core.Definitions;
using GameCheater.Core.Memory;
using GameCheater.Core.Scanning;

namespace GameCheater.App.ViewModels;

/// <summary>
/// The "Capture" tab: a point-and-click value scanner. Pick a type, first-scan (known value or
/// unknown), narrow with +/-/~/=, then Freeze a candidate (live test) or Save it as a cheat
/// (durable JSON). Scans run off the UI thread so the window stays responsive.
/// </summary>
public sealed class CaptureViewModel : ViewModelBase
{
    private readonly Func<ProcessMemory?> _memory;
    private readonly Func<Trainer?> _trainer;
    private readonly Func<string> _gameName;

    private IValueScanSession? _session;
    private Cheat? _testCheat;              // the current test freeze (not shown in the Cheats list)
    private readonly TrainerDefinition _captured = new();

    public ObservableCollection<string> Types { get; } = new(ValueScan.Types);
    public ObservableCollection<ScanCandidate> Candidates { get; } = new();

    public AsyncRelayCommand FirstScanCommand { get; }
    public AsyncRelayCommand UnknownScanCommand { get; }
    public AsyncRelayCommand IncreasedCommand { get; }
    public AsyncRelayCommand DecreasedCommand { get; }
    public AsyncRelayCommand ChangedCommand { get; }
    public AsyncRelayCommand UnchangedCommand { get; }
    public AsyncRelayCommand ExactNarrowCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand FreezeCommand { get; }
    public RelayCommand UnfreezeCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand ExportCandidatesCommand { get; }

    public CaptureViewModel(Func<ProcessMemory?> memory, Func<Trainer?> trainer, Func<string> gameName)
    {
        _memory = memory;
        _trainer = trainer;
        _gameName = gameName;

        FirstScanCommand = new AsyncRelayCommand(() => RunFirstScan(unknown: false));
        UnknownScanCommand = new AsyncRelayCommand(() => RunFirstScan(unknown: true));
        IncreasedCommand = new AsyncRelayCommand(() => Narrow(s => s.NarrowIncreased(), "increased"));
        DecreasedCommand = new AsyncRelayCommand(() => Narrow(s => s.NarrowDecreased(), "decreased"));
        ChangedCommand = new AsyncRelayCommand(() => Narrow(s => s.NarrowChanged(), "changed"));
        UnchangedCommand = new AsyncRelayCommand(() => Narrow(s => s.NarrowUnchanged(), "unchanged"));
        ExactNarrowCommand = new AsyncRelayCommand(NarrowExactOrRange);
        ResetCommand = new RelayCommand(ResetScan);
        FreezeCommand = new RelayCommand(Freeze, () => SelectedCandidate is not null);
        UnfreezeCommand = new RelayCommand(Unfreeze);
        SaveCommand = new RelayCommand(SaveCheat, () => SelectedCandidate is not null);
        ExportCandidatesCommand = new RelayCommand(ExportCandidates);
    }

    /// <summary>
    /// Dump every current candidate address to a file the CLI bisect-freeze reads. When a scan
    /// narrows to a cluster of values that all track the same thing (dozens of engine-integrity
    /// mirrors) and no narrow can split them, this hands the set to <c>--bisect</c>, which
    /// freezes half at a time to find the one authoritative value. Capped so a still-huge scan
    /// doesn't write a giant file — narrow to a manageable set first.
    /// </summary>
    private void ExportCandidates()
    {
        if (_session is null || !_session.FirstScanDone) { Status = "Do a scan first."; return; }
        const int cap = 20000;
        if (_session.CandidateCount > cap)
        {
            Status = $"Too many candidates ({_session.CandidateCount:N0}) — narrow below {cap:N0} first.";
            return;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameCheater");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "candidates.txt");

        // "0xADDR=value" — the value lets the bisect skip non-integrity candidates (pointers,
        // zeros, huge numbers) that would hang the game if frozen.
        var lines = new List<string> { _session.TypeName };
        lines.AddRange(_session.Top(cap).Select(c => $"0x{c.Address:X}={c.Value}"));
        File.WriteAllText(path, string.Join('\n', lines));

        Status = $"Exported {lines.Count - 1} candidate(s) to {path}";
    }

    private string _selectedType = "float";
    public string SelectedType { get => _selectedType; set => SetField(ref _selectedType, value); }

    private string _firstValue = "";
    public string FirstValue { get => _firstValue; set => SetField(ref _firstValue, value); }

    private string _narrowValue = "";
    public string NarrowValue { get => _narrowValue; set => SetField(ref _narrowValue, value); }

    private string _freezeValue = "";
    public string FreezeValue { get => _freezeValue; set => SetField(ref _freezeValue, value); }

    private string _cheatName = "";
    public string CheatName { get => _cheatName; set => SetField(ref _cheatName, value); }

    private string _cheatCategory = "General";
    public string CheatCategory { get => _cheatCategory; set => SetField(ref _cheatCategory, value); }

    private string _cheatDescription = "";
    public string CheatDescription { get => _cheatDescription; set => SetField(ref _cheatDescription, value); }

    private long _candidateCount;
    public long CandidateCount { get => _candidateCount; private set => SetField(ref _candidateCount, value); }

    private string _status = "Attach to a game (Start Engine), then scan for a value.";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private ScanCandidate? _selectedCandidate;
    public ScanCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetField(ref _selectedCandidate, value))
            {
                // A typed set-value is only safe for the candidate it was entered for. Carrying
                // it to another row can write an arbitrary value into an unrelated address.
                ClearTestFreeze(_trainer());
                FreezeValue = "";
                FreezeCommand.RaiseCanExecuteChanged();
                SaveCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanCopyAddress));
            }
        }
    }

    /// <summary>Enables the Copy button only when there's a candidate to copy.</summary>
    public bool CanCopyAddress => SelectedCandidate is not null;

    /// <summary>The selected candidate's address as a copyable hex string, in the same format
    /// the list shows it (e.g. 0x22A6BA5FD18), or null if nothing is selected.</summary>
    public string? SelectedAddressText => SelectedCandidate is { } c ? $"0x{c.Address:X}" : null;

    /// <summary>Note in the status line that an address was copied. Clipboard access lives in the
    /// view (it needs the window's TopLevel), so the view calls back here once the copy is done.</summary>
    public void NotifyAddressCopied(string text) => Status = $"Copied {text} to the clipboard.";

    private async Task RunFirstScan(bool unknown)
    {
        var mem = _memory();
        if (mem is null) { Status = "Start the engine first (attach to the running game)."; return; }

        try
        {
            _session = ValueScan.Create(mem, SelectedType);
            Status = unknown ? "Scanning writable memory…" : $"Scanning for {FirstValue}…";
            bool range = TryParseRange(FirstValue, out var lo, out var hi);
            await Task.Run(() =>
            {
                if (unknown) _session.FirstScanUnknown();
                else if (range) _session.FirstScanRange(lo, hi);
                else _session.FirstScanExact(FirstValue);
            });
            RefreshCandidates();
            Status = unknown
                ? $"{CandidateCount:N0} candidates. Change the value in-game, then − / +."
                : range
                    ? $"{CandidateCount:N0} candidates in {lo}–{hi}."
                    : $"{CandidateCount:N0} candidates == {FirstValue}.";
        }
        catch (Exception ex) { Status = $"Scan failed: {ex.Message}"; }
    }

    private async Task Narrow(Action<IValueScanSession> op, string label)
    {
        if (_session is null) { Status = "Do a first scan first."; return; }
        try
        {
            Status = $"Narrowing ({label})…";
            await Task.Run(() => op(_session));
            RefreshCandidates();
            Status = $"{CandidateCount:N0} candidates ({label}).";
        }
        catch (Exception ex) { Status = $"Narrow failed: {ex.Message}"; }
    }

    private Task NarrowExactOrRange()
    {
        if (TryParseRange(NarrowValue, out var lo, out var hi))
            return Narrow(s => s.NarrowRange(lo, hi), $"in {lo}-{hi}");
        return Narrow(s => s.NarrowExact(NarrowValue), $"== {NarrowValue}");
    }

    // "188-190" -> a range; a single number -> not a range. (For positive values like fuel.)
    private static bool TryParseRange(string? text, out string lo, out string hi)
    {
        lo = hi = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('-', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        lo = parts[0];
        hi = parts[1];
        return true;
    }

    private void RefreshCandidates()
    {
        Candidates.Clear();
        if (_session is null) return;
        foreach (var c in _session.Top(200))
            Candidates.Add(c);
        CandidateCount = _session.CandidateCount;
    }

    // A throwaway test freeze: holds the candidate at its CURRENT value (never writes an
    // arbitrary value that could crash the game) so you can drive and see if the gauge stops.
    // Not added to the Cheats list — clear it with Unfreeze (or the top-bar Disable All).
    private void Freeze()
    {
        var trainer = _trainer();
        if (_session is null || SelectedCandidate is not { } cand || trainer is null)
        {
            Status = "Nothing to freeze — attach and select a candidate first.";
            return;
        }
        try
        {
            ClearTestFreeze(trainer);
            // Blank value box = hold at current (safe). Type a value = set-and-hold, so you can
            // watch whether the on-screen gauge jumps (that's how you spot the *authoritative* address).
            string? value = NullIfBlank(FreezeValue);
            var cheat = _session.CreateFreeze(cand.Address, value, "capture test", "Capture", null);
            trainer.Add(cheat);
            cheat.Enable();
            _testCheat = cheat;
            Status = value is null
                ? $"Holding 0x{cand.Address:X} at current — drive and watch the gauge. Unfreeze to stop."
                : $"Set 0x{cand.Address:X} = {value} — did the gauge jump? If yes, that's the real one. Unfreeze to stop.";
        }
        catch (Exception ex) { Status = $"Freeze failed: {ex.Message}"; }
    }

    private void Unfreeze()
    {
        ClearTestFreeze(_trainer());
        FreezeValue = "";
        Status = "Test freeze cleared.";
    }

    private void ClearTestFreeze(Trainer? trainer)
    {
        if (_testCheat is null) return;
        try { _testCheat.Disable(); } catch { /* best effort */ }
        trainer?.Cheats.Remove(_testCheat);
        _testCheat = null;
    }

    private void SaveCheat()
    {
        var mem = _memory();
        if (_session is null || SelectedCandidate is not { } cand || mem is null)
        {
            Status = "Nothing to save — attach and select a candidate first.";
            return;
        }
        try
        {
            string name = string.IsNullOrWhiteSpace(CheatName) ? $"Value @ 0x{cand.Address:X}" : CheatName;
            var def = _session.CreateDefinition(mem, cand.Address, FreezeValue, name, CheatCategory, NullIfBlank(CheatDescription));
            _captured.Game = _gameName();
            _captured.Process = _trainer()?.ProcessName ?? "";
            _captured.Cheats.Add(def);
            string path = WriteCaptured();
            Status = $"Saved \"{name}\" → {path}";
        }
        catch (Exception ex) { Status = $"Save failed: {ex.Message}"; }
    }

    private string WriteCaptured()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameCheater", "captured");
        Directory.CreateDirectory(dir);
        string slug = new string(_gameName().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (string.IsNullOrEmpty(slug)) slug = "game";
        string path = Path.Combine(dir, $"{slug}.json");
        File.WriteAllText(path, TrainerDefinitionLoader.ToJson(_captured));
        return path;
    }

    private void ResetScan()
    {
        _session?.Reset();
        Candidates.Clear();
        CandidateCount = 0;
        SelectedCandidate = null;
        FreezeValue = "";
        Status = "Scan reset.";
    }

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
