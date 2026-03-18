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
        vm.PickFileAsync = PickMapFileAsync;
        vm.BackToMenuAsync = NavigateBackToMenuAsync;
    }

    /// <summary>
    /// Opens a native file picker filtered to .txt and .csv files.
    /// Returns the chosen file path, or null if the user cancelled.
    /// </summary>
    private async Task<string?> PickMapFileAsync()
    {
        var options = new FilePickerOpenOptions
        {
            Title            = "Load Mars Map or Model",
            AllowMultiple    = false,
            FileTypeFilter   = new[]
            {
                new FilePickerFileType("Map files")
                {
                    Patterns = new[] { "*.txt", "*.csv" }
                },
                new FilePickerFileType("Q-table model")
                {
                    Patterns = new[] { "*.qtable.json" }
                },
                new FilePickerFileType("All files")   { Patterns = new[] { "*.*"  } }
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
