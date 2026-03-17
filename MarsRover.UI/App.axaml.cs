using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MarsRover.UI.Views;

namespace MarsRover.UI;

public class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var menuWindow = new MenuWindow();
            UiDisplaySettings.ApplyTo(menuWindow);
            desktop.MainWindow = menuWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
