using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using MarsRover.UI.ViewModels;
using NetCoreAudio;

namespace MarsRover.UI.Views;

public partial class MainWindow : Window
{
    private Border _transitionGlowLayer = null!;
    private bool _isAudioShuttingDown;
    private bool _ambienceStarted;
    private DispatcherTimer? _ambienceStartTimer;
    private string? _mainScreenSoundPath;
    private string? _whiteNoiseSpacePath;
    private LibVLC? _audioLibVlc;
    private MediaPlayer? _mainScreenAudioPlayer;
    private MediaPlayer? _whiteNoiseAudioPlayer;
    private Media? _mainScreenAudioMedia;
    private Media? _whiteNoiseAudioMedia;
    private readonly Player _fallbackMainScreenPlayer = new();
    private readonly Player _fallbackWhiteNoisePlayer = new();

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnMainWindowOpened;
        Closed += OnMainWindowClosed;
        _fallbackMainScreenPlayer.PlaybackFinished += OnFallbackMainScreenPlaybackFinished;
        _fallbackWhiteNoisePlayer.PlaybackFinished += OnFallbackWhiteNoisePlaybackFinished;

        if (DataContext is MainViewModel vm)
            BindViewModel(vm);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _transitionGlowLayer = this.FindControl<Border>("TransitionGlowLayer")
            ?? throw new System.InvalidOperationException("TransitionGlowLayer not found.");

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
            Title = "Load Mars Map",
            AllowMultiple = false,
            FileTypeFilter = new[]
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
            Title = "Load Q-table Model",
            AllowMultiple = false,
            FileTypeFilter = new[]
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

    private async Task NavigateBackToMenuAsync()
    {
        StopMainWindowAmbience();

        var menuWindow = new MenuWindow
        {
            Opacity = 0
        };
        UiDisplaySettings.ApplyTo(menuWindow);
        CopyWindowGeometry(this, menuWindow);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = menuWindow;

        menuWindow.Show();
        await CrossFadeWindowsAsync(this, menuWindow, _transitionGlowLayer, menuWindow.GetTransitionGlowForCrossFade());
        Close();
    }

    private static async Task CrossFadeWindowsAsync(Window from, Window to, Border? fromGlow, Border? toGlow)
    {
        const int durationMs = 5460;
        var tasks = new List<Task>
        {
            CreateOpacityAnimation(from.Opacity, 0, durationMs).RunAsync(from),
            CreateOpacityAnimation(to.Opacity, 1, durationMs).RunAsync(to)
        };

        if (fromGlow is not null)
        {
            fromGlow.Opacity = 0;
            tasks.Add(CreateGlowPulseAnimation(0.22, durationMs).RunAsync(fromGlow));
        }

        if (toGlow is not null)
        {
            toGlow.Opacity = 0;
            tasks.Add(CreateGlowPulseAnimation(0.24, durationMs).RunAsync(toGlow));
        }

        await Task.WhenAll(tasks);
    }

    private static Animation CreateOpacityAnimation(double from, double to, int durationMs)
    {
        var animation = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(durationMs),
            FillMode = FillMode.Forward,
            Easing = new SineEaseInOut()
        };

        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(OpacityProperty, from) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(OpacityProperty, to) }
        });

        return animation;
    }

    private static Animation CreateGlowPulseAnimation(double peakOpacity, int durationMs)
    {
        var animation = new Animation
        {
            Duration = System.TimeSpan.FromMilliseconds(durationMs),
            FillMode = FillMode.Forward,
            Easing = new SineEaseInOut()
        };

        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0d) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0.42d),
            Setters = { new Avalonia.Styling.Setter(OpacityProperty, peakOpacity) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(OpacityProperty, 0d) }
        });

        return animation;
    }

    private static void CopyWindowGeometry(Window source, Window target)
    {
        target.WindowState = source.WindowState;

        if (source.WindowState == WindowState.Normal)
        {
            target.Position = source.Position;
            target.Width = source.Width;
            target.Height = source.Height;
        }
    }

    private void TopBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void MapViewport_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is MainViewModel vm && sender is Control control && control.Bounds.Width > 0 && control.Bounds.Height > 0)
        {
            var pos = e.GetPosition(control);
            double focusX = Math.Clamp(pos.X / control.Bounds.Width, 0.0, 1.0);
            double focusY = Math.Clamp(pos.Y / control.Bounds.Height, 0.0, 1.0);
            vm.AdjustMapZoomFromWheel(e.Delta.Y, focusX, focusY);
            e.Handled = true;
        }
    }

    internal Border GetTransitionGlowForCrossFade() => _transitionGlowLayer;

    private void OnMainWindowOpened(object? sender, EventArgs e)
    {
        UiDisplaySettings.ApplyTo(this);
        _isAudioShuttingDown = false;

        StartAmbienceRetryTimer();
    }

    private void OnMainWindowClosed(object? sender, EventArgs e)
    {
        _ambienceStartTimer?.Stop();
        _ambienceStartTimer = null;
        StopMainWindowAmbience();
        _fallbackMainScreenPlayer.PlaybackFinished -= OnFallbackMainScreenPlaybackFinished;
        _fallbackWhiteNoisePlayer.PlaybackFinished -= OnFallbackWhiteNoisePlaybackFinished;
    }

    private void StartMainWindowAmbience()
    {
        if (_isAudioShuttingDown)
            return;

        _mainScreenSoundPath ??= ResolveAudioPath("MainScreenSound (FROM GTA Online).mp3");
        _whiteNoiseSpacePath ??= ResolveAudioPath("WhiteNoiseSpace.m4a");
        if (string.IsNullOrWhiteSpace(_mainScreenSoundPath) || string.IsNullOrWhiteSpace(_whiteNoiseSpacePath))
        {
            _ambienceStarted = false;
            return;
        }

        var libVlcStarted = false;
        try
        {
            LibVLCSharp.Shared.Core.Initialize();

            _audioLibVlc ??= new LibVLC("--no-video", "--quiet");
            _mainScreenAudioPlayer ??= new MediaPlayer(_audioLibVlc);
            _whiteNoiseAudioPlayer ??= new MediaPlayer(_audioLibVlc);

            _mainScreenAudioMedia?.Dispose();
            _whiteNoiseAudioMedia?.Dispose();

            _mainScreenAudioMedia = new Media(_audioLibVlc, new Uri(_mainScreenSoundPath));
            _mainScreenAudioMedia.AddOption(":input-repeat=-1");

            _whiteNoiseAudioMedia = new Media(_audioLibVlc, new Uri(_whiteNoiseSpacePath));
            _whiteNoiseAudioMedia.AddOption(":input-repeat=-1");

            _mainScreenAudioPlayer.Volume = 60;
            _whiteNoiseAudioPlayer.Volume = 50;

            _mainScreenAudioPlayer.Play(_mainScreenAudioMedia);
            _whiteNoiseAudioPlayer.Play(_whiteNoiseAudioMedia);
            libVlcStarted = true;
        }
        catch
        {
            libVlcStarted = false;
        }

        if (!libVlcStarted)
        {
            StartFallbackAmbience();
        }
    }

    private void StopMainWindowAmbience()
    {
        _isAudioShuttingDown = true;
        _ambienceStarted = false;

        if (_mainScreenAudioPlayer is not null)
        {
            _mainScreenAudioPlayer.Stop();
            _mainScreenAudioPlayer.Dispose();
            _mainScreenAudioPlayer = null;
        }

        if (_whiteNoiseAudioPlayer is not null)
        {
            _whiteNoiseAudioPlayer.Stop();
            _whiteNoiseAudioPlayer.Dispose();
            _whiteNoiseAudioPlayer = null;
        }

        _mainScreenAudioMedia?.Dispose();
        _mainScreenAudioMedia = null;

        _whiteNoiseAudioMedia?.Dispose();
        _whiteNoiseAudioMedia = null;

        _audioLibVlc?.Dispose();
        _audioLibVlc = null;

        if (_fallbackMainScreenPlayer.Playing)
            _ = _fallbackMainScreenPlayer.Stop();

        if (_fallbackWhiteNoisePlayer.Playing)
            _ = _fallbackWhiteNoisePlayer.Stop();
    }

    private static string? ResolveAudioPath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", fileName))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal void BeginAmbienceAfterTransition()
    {
        if (_isAudioShuttingDown || _ambienceStarted)
            return;

        _ambienceStartTimer?.Stop();
        _ambienceStartTimer = null;
        _ambienceStarted = true;
        StartMainWindowAmbience();
    }

    private void StartAmbienceRetryTimer()
    {
        _ambienceStartTimer?.Stop();

        _ambienceStartTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };

        _ambienceStartTimer.Tick += (_, _) =>
        {
            if (_isAudioShuttingDown || _ambienceStarted)
            {
                _ambienceStartTimer?.Stop();
                return;
            }

            if (Opacity >= 0.98)
            {
                BeginAmbienceAfterTransition();
            }
        };

        _ambienceStartTimer.Start();
    }

    private void StartFallbackAmbience()
    {
        if (_isAudioShuttingDown)
            return;

        if (!string.IsNullOrWhiteSpace(_mainScreenSoundPath) && !_fallbackMainScreenPlayer.Playing)
        {
            _fallbackMainScreenPlayer.SetVolume(55);
            _ = _fallbackMainScreenPlayer.Play(_mainScreenSoundPath);
        }

        if (!string.IsNullOrWhiteSpace(_whiteNoiseSpacePath) && !_fallbackWhiteNoisePlayer.Playing)
        {
            _fallbackWhiteNoisePlayer.SetVolume(35);
            _ = _fallbackWhiteNoisePlayer.Play(_whiteNoiseSpacePath);
        }
    }

    private void OnFallbackMainScreenPlaybackFinished(object? sender, EventArgs e)
    {
        if (!_isAudioShuttingDown && !string.IsNullOrWhiteSpace(_mainScreenSoundPath))
        {
            _ = _fallbackMainScreenPlayer.Play(_mainScreenSoundPath);
        }
    }

    private void OnFallbackWhiteNoisePlaybackFinished(object? sender, EventArgs e)
    {
        if (!_isAudioShuttingDown && !string.IsNullOrWhiteSpace(_whiteNoiseSpacePath))
        {
            _ = _fallbackWhiteNoisePlayer.Play(_whiteNoiseSpacePath);
        }
    }
}
