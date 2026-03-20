using Avalonia.Controls;
using Avalonia.Platform;

namespace MarsRover.UI;

public static class UiDisplaySettings
{
    public static bool FullscreenEnabled { get; set; } = true;

    public static void ApplyTo(Window window)
    {
        if (FullscreenEnabled)
        {
            window.SystemDecorations = SystemDecorations.None;
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.NoChrome;
            window.ExtendClientAreaTitleBarHeightHint = -1;
            window.WindowState = WindowState.FullScreen;
            return;
        }

        window.WindowState = WindowState.Normal;
        window.SystemDecorations = SystemDecorations.Full;
        window.ExtendClientAreaToDecorationsHint = false;
        window.ExtendClientAreaChromeHints = ExtendClientAreaChromeHints.Default;
        window.ExtendClientAreaTitleBarHeightHint = 0;
        window.CanResize = true;
    }
}
