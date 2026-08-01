using Avalonia.Controls;
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
