using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using MarsRover.UI.ViewModels;
using NetCoreAudio;
using Avalonia.Controls.Primitives; 

namespace MarsRover.UI.Views;

public partial class MenuWindow : Window
{
    private readonly Random _random = new();
    private readonly Player _musicPlayer = new();
    private readonly List<Border> _signalSegments = new();
    private readonly List<Border> _dustParticles = new();
    private readonly List<Border> _glitchArtifacts = new();
    private int _signalLevel = 95;
    private int _activeSegments;
    private double _pulsePhase;
    private byte _musicVolume = 50;
    private bool _isClosing;
    private string? _musicPath;
    private DispatcherTimer? _signalTimer;
    private DispatcherTimer? _pulseTimer;
    private DispatcherTimer? _glitchTimer;
    private DispatcherTimer? _dustTimer;
    private DispatcherTimer? _musicLoopTimer;

    private Grid _videoHostLayer = null!;
    private Border _videoFallbackPanel = null!;
    private StackPanel _signalSegmentsPanel = null!;
    private Canvas _dynamicDustLayer = null!;
    private Canvas _glitchArtifactsLayer = null!;
    private Canvas _frameCornerGuides = null!;
    private TextBlock _signalText = null!;
    private TextBlock _onlineLabel = null!;
    private Control _onlineDot = null!;
    private Border _glitchLine = null!;
    private Border _settingsOverlay = null!;
    private CheckBox _fullscreenModeCheckBox = null!;
    private VideoView? _videoView;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;

    public MenuWindow()
    {
        InitializeComponent();

        Opened += OnOpened;
        Closed += OnClosed;
        SizeChanged += OnMenuSizeChanged;

        _musicPlayer.PlaybackFinished += MusicPlayerOnPlaybackFinished;
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _videoHostLayer = this.FindControl<Grid>("VideoHostLayer")
            ?? throw new InvalidOperationException("VideoHostLayer not found.");
        _videoFallbackPanel = this.FindControl<Border>("VideoFallbackPanel")
            ?? throw new InvalidOperationException("VideoFallbackPanel not found.");
        _signalSegmentsPanel = this.FindControl<StackPanel>("SignalSegmentsPanel")
            ?? throw new InvalidOperationException("SignalSegmentsPanel not found.");
        _dynamicDustLayer = this.FindControl<Canvas>("DynamicDustLayer")
            ?? throw new InvalidOperationException("DynamicDustLayer not found.");
        _glitchArtifactsLayer = this.FindControl<Canvas>("GlitchArtifactsLayer")
            ?? throw new InvalidOperationException("GlitchArtifactsLayer not found.");
        _frameCornerGuides = this.FindControl<Canvas>("FrameCornerGuides")
            ?? throw new InvalidOperationException("FrameCornerGuides not found.");
        _signalText = this.FindControl<TextBlock>("SignalText")
            ?? throw new InvalidOperationException("SignalText not found.");
        _onlineLabel = this.FindControl<TextBlock>("OnlineLabel")
            ?? throw new InvalidOperationException("OnlineLabel not found.");
        _onlineDot = this.FindControl<Control>("OnlineDot")
            ?? throw new InvalidOperationException("OnlineDot not found.");
        _glitchLine = this.FindControl<Border>("GlitchLine")
            ?? throw new InvalidOperationException("GlitchLine not found.");
        _settingsOverlay = this.FindControl<Border>("SettingsOverlay")
            ?? throw new InvalidOperationException("SettingsOverlay not found.");
        _fullscreenModeCheckBox = this.FindControl<CheckBox>("FullscreenModeCheckBox")
            ?? throw new InvalidOperationException("FullscreenModeCheckBox not found.");

        _fullscreenModeCheckBox.IsChecked = UiDisplaySettings.FullscreenEnabled;

        BuildSignalSegments();
        UpdateSignalSegments(_signalLevel);
        _signalText.Text = $"SIG {_signalLevel}%";
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        _isClosing = false;
        UiDisplaySettings.ApplyTo(this);
        UpdateFrameCornerVisibility();
        BuildHudArtifacts();
        InitializeVideo();
        StartHudEffects();
        PlayMusic();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosing = true;

        _signalTimer?.Stop();
        _pulseTimer?.Stop();
        _glitchTimer?.Stop();
        _dustTimer?.Stop();
        _musicLoopTimer?.Stop();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        _currentMedia?.Dispose();
        _currentMedia = null;

        _libVlc?.Dispose();
        _libVlc = null;

        if (_videoView is not null)
        {
            _videoView.MediaPlayer = null;
            _videoHostLayer.Children.Clear();
            _videoView = null;
        }

        _musicPlayer.PlaybackFinished -= MusicPlayerOnPlaybackFinished;
        if (_musicPlayer.Playing)
            _ = _musicPlayer.Stop();
    }

    private void InitializeVideo()
    {
        if (!IsNativeVideoEnabled())
            return;

        var videoPath = ResolveVideoPath();
        if (videoPath is null)
            return;

        try
        {
            LibVLCSharp.Shared.Core.Initialize();

            _libVlc = new LibVLC("--no-audio", "--no-video-title-show");
            _mediaPlayer = new MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = true
            };

            _videoView = new VideoView
            {
                MediaPlayer = _mediaPlayer
            };
            _videoHostLayer.Children.Clear();
            _videoHostLayer.Children.Add(_videoView);

            _currentMedia = new Media(_libVlc, new Uri(videoPath));
            _currentMedia.AddOption(":input-repeat=-1");

            _mediaPlayer.Play(_currentMedia);
        }
        catch
        {
            _videoFallbackPanel.IsVisible = false;
        }
    }

    private static bool IsNativeVideoEnabled()
    {
        var value = Environment.GetEnvironmentVariable("MARS_UI_VIDEO");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveVideoPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "mars-feed.mp4"),
            Path.Combine(AppContext.BaseDirectory, "mars-feed.mp4"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "mars-feed.mp4"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private void StartHudEffects()
    {
        _signalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(980) };
        _signalTimer.Tick += (_, _) =>
        {
            _signalLevel = Math.Clamp(_signalLevel + _random.Next(-5, 6), 78, 99);
            _signalText.Text = $"SIG {_signalLevel}%";
            UpdateSignalSegments(_signalLevel);
        };
        _signalTimer.Start();

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulsePhase += 0.23;
            var wave = 0.5 + (Math.Sin(_pulsePhase) * 0.5);

            _onlineDot.Opacity = 0.25 + (wave * 0.75);
            _onlineLabel.Opacity = 0.45 + (wave * 0.55);
            AnimateSignalPulse(wave);
        };
        _pulseTimer.Start();

        _dustTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _dustTimer.Tick += (_, _) => UpdateDustParticles(forceRedistribute: false);
        _dustTimer.Start();

        _glitchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1450) };
        _glitchTimer.Tick += async (_, _) =>
        {
            if (_random.NextDouble() > 0.3)
                return;

            var maxY = Math.Max(140, (int)(Bounds.Height - 180));
            var y = _random.Next(120, maxY);
            var maxX = Math.Max(40, (int)(Bounds.Width - 260));
            var x = _random.Next(20, maxX);
            var width = _random.Next(140, (int)Math.Max(180, Math.Min(620, Bounds.Width * 0.42)));

            _glitchLine.Width = width;
            _glitchLine.Opacity = 0.035 + (_random.NextDouble() * 0.09);
            _glitchLine.Margin = new Thickness(x, y, 0, 0);
            _glitchLine.IsVisible = true;
            TriggerGlitchArtifact();

            await System.Threading.Tasks.Task.Delay(_random.Next(45, 120));
            _glitchLine.IsVisible = false;
            foreach (var artifact in _glitchArtifacts)
                artifact.IsVisible = false;
        };
        _glitchTimer.Start();

        _musicLoopTimer?.Stop();
        _musicLoopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _musicLoopTimer.Tick += (_, _) =>
        {
            if (!_isClosing && !_musicPlayer.Playing)
            {
                PlayMusic();
            }
        };
        _musicLoopTimer.Start();
    }

    private void BuildHudArtifacts()
    {
        if (_dustParticles.Count == 0)
        {
            const int dustCount = 64;
            for (var i = 0; i < dustCount; i++)
            {
                var size = 0.8 + (_random.NextDouble() * 1.8);
                var dust = new Border
                {
                    Width = size,
                    Height = size,
                    CornerRadius = new CornerRadius(size / 2),
                    Background = new SolidColorBrush(Color.Parse(RandomDustColor())),
                    Opacity = 0.02 + (_random.NextDouble() * 0.06)
                };
                _dustParticles.Add(dust);
                _dynamicDustLayer.Children.Add(dust);
            }
        }

        if (_glitchArtifacts.Count == 0)
        {
            for (var i = 0; i < 4; i++)
            {
                var artifact = new Border
                {
                    Width = 120,
                    Height = 1.2,
                    Background = new SolidColorBrush(Color.Parse("#2EFFB36E")),
                    IsVisible = false
                };
                _glitchArtifacts.Add(artifact);
                _glitchArtifactsLayer.Children.Add(artifact);
            }
        }

        UpdateDustParticles(forceRedistribute: true);
    }

    private void OnMenuSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateFrameCornerVisibility();
        UpdateDustParticles(forceRedistribute: true);
    }

    private void UpdateFrameCornerVisibility()
    {
        _frameCornerGuides.IsVisible = WindowState == WindowState.FullScreen;
    }

    private void UpdateDustParticles(bool forceRedistribute)
    {
        if (_dustParticles.Count == 0) return;

        var width = Math.Max(20, Bounds.Width);
        var height = Math.Max(20, Bounds.Height);

        foreach (var dust in _dustParticles)
        {
            if (forceRedistribute || _random.NextDouble() < 0.2)
            {
                var x = _random.NextDouble() * Math.Max(1, width - dust.Width - 2);
                var y = _random.NextDouble() * Math.Max(1, height - dust.Height - 2);
                Canvas.SetLeft(dust, x);
                Canvas.SetTop(dust, y);
            }

            dust.Opacity = 0.012 + (_random.NextDouble() * 0.05);
            if (_random.NextDouble() < 0.06)
            {
                var size = 0.8 + (_random.NextDouble() * 1.9);
                dust.Width = size;
                dust.Height = size;
                dust.CornerRadius = new CornerRadius(size / 2);
            }
        }
    }

    private void TriggerGlitchArtifact()
    {
        if (_glitchArtifacts.Count == 0) return;

        var artifact = _glitchArtifacts[_random.Next(_glitchArtifacts.Count)];
        bool vertical = _random.NextDouble() < 0.32;

        artifact.Width = vertical
            ? _random.Next(1, 3)
            : _random.Next(80, (int)Math.Max(90, Math.Min(320, Bounds.Width * 0.25)));
        artifact.Height = vertical
            ? _random.Next(22, 88)
            : _random.Next(1, 3);
        artifact.Opacity = 0.025 + (_random.NextDouble() * 0.08);

        var x = _random.NextDouble() * Math.Max(1, Bounds.Width - artifact.Width - 4);
        var y = _random.NextDouble() * Math.Max(1, Bounds.Height - artifact.Height - 4);
        Canvas.SetLeft(artifact, x);
        Canvas.SetTop(artifact, y);
        artifact.IsVisible = true;
    }

    private string RandomDustColor()
    {
        return _random.Next(3) switch
        {
            0 => "#44FF8F47",
            1 => "#38FFC28A",
            _ => "#31E5A162"
        };
    }

    private void BuildSignalSegments()
    {
        if (_signalSegments.Count > 0)
            return;

        const int segmentCount = 44;
        for (var i = 0; i < segmentCount; i++)
        {
            var segment = new Border
            {
                Width = 8,
                Height = 8,
                CornerRadius = new CornerRadius(0.7),
                Background = new SolidColorBrush(Color.Parse("#2D1C120C")),
                Opacity = 0.88
            };

            _signalSegments.Add(segment);
            _signalSegmentsPanel.Children.Add(segment);
        }
    }

    private void UpdateSignalSegments(int level)
    {
        if (_signalSegments.Count == 0)
            return;

        _activeSegments = Math.Clamp((int)Math.Round((_signalSegments.Count * level) / 100.0), 0, _signalSegments.Count);

        for (var i = 0; i < _signalSegments.Count; i++)
        {
            var segment = _signalSegments[i];
            if (i < _activeSegments)
            {
                var ratio = _activeSegments <= 1 ? 0.0 : i / (double)(_activeSegments - 1);
                if (ratio < 0.72)
                {
                    segment.Background = new SolidColorBrush(Color.Parse("#FFF27E2A"));
                }
                else if (ratio < 0.9)
                {
                    segment.Background = new SolidColorBrush(Color.Parse("#D9DE6E2E"));
                }
                else
                {
                    segment.Background = new SolidColorBrush(Color.Parse("#A1B95E33"));
                }
            }
            else if (i < _activeSegments + 2)
            {
                segment.Background = new SolidColorBrush(Color.Parse("#5B6A3B27"));
            }
            else
            {
                segment.Background = new SolidColorBrush(Color.Parse("#2D1C120C"));
            }

            segment.Opacity = i < _activeSegments ? 0.96 : 0.82;
        }
    }

    private void AnimateSignalPulse(double wave)
    {
        if (_activeSegments <= 0 || _signalSegments.Count == 0)
            return;

        var head = _activeSegments - 1;
        _signalSegments[head].Opacity = 0.58 + (wave * 0.42);

        if (_activeSegments > 1)
            _signalSegments[head - 1].Opacity = 0.7 + (wave * 0.25);
    }

    private void TopBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void StartMission_OnClick(object? sender, RoutedEventArgs e)
    {
        var mainWindow = new MainWindow
        {
            DataContext = new MainViewModel()
        };
        UiDisplaySettings.ApplyTo(mainWindow);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = mainWindow;

        mainWindow.Show();
        Close();
    }

    private void Settings_OnClick(object? sender, RoutedEventArgs e)
    {
        _fullscreenModeCheckBox.IsChecked = UiDisplaySettings.FullscreenEnabled;
        _settingsOverlay.IsVisible = !_settingsOverlay.IsVisible;
    }

    private void CloseSettings_OnClick(object? sender, RoutedEventArgs e)
    {
        _settingsOverlay.IsVisible = false;
    }

    private void Exit_OnClick(object? sender, RoutedEventArgs e)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Close();
    }

    private void FullscreenMode_OnChanged(object? sender, RoutedEventArgs e)
    {
        UiDisplaySettings.FullscreenEnabled = _fullscreenModeCheckBox.IsChecked != false;
        UiDisplaySettings.ApplyTo(this);
        UpdateFrameCornerVisibility();
    }

    private void MusicPlayerOnPlaybackFinished(object? sender, EventArgs e)
    {
        if (!_isClosing)
            PlayMusic();
    }

    private void PlayMusic()
    {
        if (_isClosing || _musicPlayer.Playing)
            return;

        _musicPath ??= ResolveMusicPath();
        if (string.IsNullOrWhiteSpace(_musicPath))
            return;

        _musicPlayer.SetVolume(_musicVolume);
        _ = _musicPlayer.Play(_musicPath);
    }

    private static string? ResolveMusicPath()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "BackgroundMusic.mp3"),
            Path.Combine(AppContext.BaseDirectory, "BackgroundMusic.mp3"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Assets", "BackgroundMusic.mp3"))
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public void VolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _musicVolume = (byte)Math.Clamp(e.NewValue, 0, 100);
        if (!_isClosing)
        {
            _musicPlayer.SetVolume(_musicVolume);
        }
    }
   


}

