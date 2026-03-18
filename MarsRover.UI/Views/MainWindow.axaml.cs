using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MarsRover.UI.ViewModels;

namespace MarsRover.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += (_, _) => UiDisplaySettings.ApplyTo(this);

        if (DataContext is MainViewModel vm)
            BindViewModel(vm);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
                BindViewModel(vm);
        };
    }

    private void BindViewModel(MainViewModel vm)
    {
        vm.PickMapFileAsync = PickMapFileAsync;
        vm.PickModelFileAsync = PickModelFileAsync;
        vm.BackToMenuAsync = NavigateBackToMenuAsync;
    }

    /// <summary>
    /// Opens a native file picker filtered to map files.
    /// Returns the chosen file path, or null if the user cancelled.
    /// </summary>
    private async Task<string?> PickMapFileAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title            = "Load Mars Map",
            AllowMultiple    = false,
            FileTypeFilter   = new[]
            {
                new FilePickerFileType("Map files")
                {
                    Patterns = new[] { "*.txt", "*.csv" }
                }
            }
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// Opens a native file picker filtered to model JSON files only.
    /// Returns the chosen file path, or null if the user cancelled.
    /// </summary>
    private async Task<string?> PickModelFileAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title            = "Load Q-table Model",
            AllowMultiple    = false,
            FileTypeFilter   = new[]
            {
                new FilePickerFileType("Model JSON files")
                {
                    Patterns = new[] { "*.json" }
                }
            }
        };

        var files = await StorageProvider.OpenFilePickerAsync(options);
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private Task NavigateBackToMenuAsync()
    {
        var menuWindow = new MenuWindow();
        UiDisplaySettings.ApplyTo(menuWindow);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = menuWindow;

        menuWindow.Show();
        Close();
        return Task.CompletedTask;
    }
}
