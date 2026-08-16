using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GameCheater.App.ViewModels;

namespace GameCheater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        LoadTableButton.Click += OnLoadTableClick;
        CopyAddressButton.Click += OnCopyAddressClick;
    }

    // The clipboard is reached through the window's TopLevel, so (like the file picker) this
    // lives in the view rather than the VM. The VM just formats the address and notes the copy.
    private async void OnCopyAddressClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || vm.Capture.SelectedAddressText is not { } text)
            return;

        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            await clipboard.SetTextAsync(text);
            vm.Capture.NotifyAddressCopied(text);
        }
    }

    // File picking needs the window's StorageProvider, so this lives in the view (not the VM).
    private async void OnLoadTableClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a Cheat Engine table",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Cheat Engine tables") { Patterns = ["*.CT", "*.ct", "*.xml"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            vm.LoadTable(path);
    }
}
