using System.Windows.Input;

namespace GameCheater.App.ViewModels;

/// <summary>An ICommand for async handlers (e.g. the Refresh button), with a busy guard so it
/// can't run twice concurrently and can disable itself while running.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => !_running;

    public async void Execute(object? parameter)
    {
        if (_running) return;
        _running = true;
        RaiseCanExecuteChanged();
        try { await _execute(); }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public event EventHandler? CanExecuteChanged;
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
