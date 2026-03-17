using Avalonia.Controls;

namespace MarsRover.UI;

public static class UiDisplaySettings
{
    public static bool FullscreenEnabled { get; set; } = true;

    public static void ApplyTo(Window window)
    {
        window.WindowState = FullscreenEnabled
            ? WindowState.FullScreen
            : WindowState.Normal;
    }
}
