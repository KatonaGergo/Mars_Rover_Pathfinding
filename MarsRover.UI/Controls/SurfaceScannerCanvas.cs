using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MarsRover.UI.Controls;

/// <summary>
/// Simulates an orbital scanner by moving a viewport over a large static Mars map.
/// </summary>
public class SurfaceScannerCanvas : Control
{
    private readonly DispatcherTimer _scanTimer;
    private readonly Random _random = new();

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(IsActive), true);

    public static readonly StyledProperty<int> JumpTriggerProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, int>(nameof(JumpTrigger), 0);

    public static readonly StyledProperty<bool> IsTargetActiveProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(IsTargetActive), false);

    public static readonly StyledProperty<bool> ShowHudProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(ShowHud), true);

    public static readonly StyledProperty<double> LoadProgressProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, double>(nameof(LoadProgress), 1.0);

    public static readonly StyledProperty<bool> EnableLoadFxProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(EnableLoadFx), true);

    public static readonly StyledProperty<bool> UseSharedLockedViewProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(UseSharedLockedView), false);

    public static readonly StyledProperty<bool> UseExternalCameraProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, bool>(nameof(UseExternalCamera), false);

    public static readonly StyledProperty<double> ExternalCameraCenterXProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, double>(nameof(ExternalCameraCenterX), 0.5);

    public static readonly StyledProperty<double> ExternalCameraCenterYProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, double>(nameof(ExternalCameraCenterY), 0.5);

    public static readonly StyledProperty<double> ExternalCameraZoomProperty =
        AvaloniaProperty.Register<SurfaceScannerCanvas, double>(nameof(ExternalCameraZoom), 1.0);

    private static readonly Uri ScannerMapUri =
        new("avares://MarsRover.UI/Assets/MarsSurfaceScannerMap.png");

    private static bool _hasSharedLockedView;
    private static Point _sharedLockedCenter = new(0.5, 0.5);
    private static double _sharedLockedZoom = 1.08;

    private const double ScanSpeed = 0.085;
    private const double BaseViewportWidth = 0.36;
    private const double FocusPulseAmplitude = 0.013;

    private readonly ScanWaypoint[] _scanPath =
    {
        new(0.16, 0.22, 1.00, 1.00),
        new(0.84, 0.24, 0.65, 1.03),
        new(0.70, 0.42, 0.45, 1.08),
        new(0.24, 0.50, 0.90, 1.02),
        new(0.38, 0.69, 0.45, 1.10),
        new(0.86, 0.72, 0.95, 1.05),
        new(0.62, 0.84, 0.55, 1.12),
        new(0.12, 0.80, 1.20, 1.06),
        new(0.22, 0.36, 0.80, 1.01)
    };

    private Bitmap? _scannerMap;
    private bool _triedLoadingMap;
    private DateTime _lastTickUtc;
    private int _segmentIndex;
    private double _segmentProgress;
    private double _pauseRemaining;
    private double _focusPhase;
    private double _scanLinePhase;
    private Point _currentCenter = new(0.16, 0.22);
    private double _currentZoom = 1.0;
    private bool _isAttachedToVisualTree;

    // "Satellite feed" stir effect state.
    private double _stirCooldownRemaining = 2.8;
    private double _stirDurationRemaining;
    private double _stirDurationSeconds = 0.34;
    private double _stirStrengthPixels = 1.0;
    private double _stirNoiseSeed;
    private double _stirPhase;
    private Point _stirOffset;

    // Smooth snap/lock state for mineral lock-on.
    private bool _snapTransitionActive;
    private bool _isLockedOnTarget;
    private Point _snapStartCenter;
    private Point _snapTargetCenter;
    private double _snapStartZoom;
    private double _snapTargetZoom;
    private double _snapDurationSeconds;
    private double _snapElapsedSeconds;
    private int _lastHandledJumpTrigger;
    private double _loadFxPhase;

    static SurfaceScannerCanvas()
    {
        IsActiveProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleActiveChanged());
        JumpTriggerProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleJumpTriggerChanged());
        IsTargetActiveProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleTargetActiveChanged());
        ShowHudProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.InvalidateVisual());
        LoadProgressProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleLoadFxStateChanged());
        EnableLoadFxProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleLoadFxStateChanged());
        UseSharedLockedViewProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.HandleUseSharedLockedViewChanged());
        UseExternalCameraProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.InvalidateVisual());
        ExternalCameraCenterXProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.InvalidateVisual());
        ExternalCameraCenterYProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.InvalidateVisual());
        ExternalCameraZoomProperty.Changed.AddClassHandler<SurfaceScannerCanvas>((control, _) =>
            control.InvalidateVisual());
    }

    public SurfaceScannerCanvas()
    {
        ClipToBounds = true;
        _pauseRemaining = _scanPath[0].PauseSeconds;
        _scanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _scanTimer.Tick += OnScanTick;
        _lastHandledJumpTrigger = JumpTrigger;
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public int JumpTrigger
    {
        get => GetValue(JumpTriggerProperty);
        set => SetValue(JumpTriggerProperty, value);
    }

    public bool IsTargetActive
    {
        get => GetValue(IsTargetActiveProperty);
        set => SetValue(IsTargetActiveProperty, value);
    }

    public bool ShowHud
    {
        get => GetValue(ShowHudProperty);
        set => SetValue(ShowHudProperty, value);
    }

    public double LoadProgress
    {
        get => GetValue(LoadProgressProperty);
        set => SetValue(LoadProgressProperty, value);
    }

    public bool EnableLoadFx
    {
        get => GetValue(EnableLoadFxProperty);
        set => SetValue(EnableLoadFxProperty, value);
    }

    public bool UseSharedLockedView
    {
        get => GetValue(UseSharedLockedViewProperty);
        set => SetValue(UseSharedLockedViewProperty, value);
    }

    public bool UseExternalCamera
    {
        get => GetValue(UseExternalCameraProperty);
        set => SetValue(UseExternalCameraProperty, value);
    }

    public double ExternalCameraCenterX
    {
        get => GetValue(ExternalCameraCenterXProperty);
        set => SetValue(ExternalCameraCenterXProperty, value);
    }

    public double ExternalCameraCenterY
    {
        get => GetValue(ExternalCameraCenterYProperty);
        set => SetValue(ExternalCameraCenterYProperty, value);
    }

    public double ExternalCameraZoom
    {
        get => GetValue(ExternalCameraZoomProperty);
        set => SetValue(ExternalCameraZoomProperty, value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
        ApplySharedLockedViewIfRequested();
        HandleActiveChanged();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _isAttachedToVisualTree = false;
        _scanTimer.Stop();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        if (!TryEnsureScannerMap())
        {
            DrawFallbackBackground(context);
            DrawHudOverlay(context);
            return;
        }

        DrawScannerImage(context);
        DrawHudOverlay(context);
    }

    private void HandleUseSharedLockedViewChanged()
    {
        ApplySharedLockedViewIfRequested();
    }

    private void ApplySharedLockedViewIfRequested()
    {
        if (!UseSharedLockedView || !_hasSharedLockedView)
            return;

        _currentCenter = _sharedLockedCenter;
        _currentZoom = _sharedLockedZoom;
        _lastHandledJumpTrigger = JumpTrigger;
        _isLockedOnTarget = true;
        _snapTransitionActive = false;
        _pauseRemaining = 0;
        _stirOffset = new Point(0, 0);

        HandleLoadFxStateChanged();
        InvalidateVisual();
    }

    private void HandleActiveChanged()
    {
        if (IsActive && _isAttachedToVisualTree && (!_isLockedOnTarget || _snapTransitionActive || ShouldAnimateLockedFeed()))
        {
            _lastTickUtc = DateTime.UtcNow;
            if (!_scanTimer.IsEnabled)
                _scanTimer.Start();
        }
        else if (!_snapTransitionActive)
        {
            _scanTimer.Stop();
        }

        InvalidateVisual();
    }

    private void HandleTargetActiveChanged()
    {
        if (IsTargetActive)
            return;

        if (UseSharedLockedView)
        {
            ApplySharedLockedViewIfRequested();
            return;
        }

        // Returning to scan mode: unlock and continue deterministic sweep.
        _isLockedOnTarget = false;
        _snapTransitionActive = false;
        _pauseRemaining = 0.2;
        _segmentIndex = (_segmentIndex + 1) % _scanPath.Length;

        if (IsActive && _isAttachedToVisualTree && !_scanTimer.IsEnabled)
        {
            _lastTickUtc = DateTime.UtcNow;
            _scanTimer.Start();
        }

        InvalidateVisual();
    }

    private void HandleJumpTriggerChanged()
    {
        if (JumpTrigger <= 0)
            return;

        if (JumpTrigger == _lastHandledJumpTrigger)
            return;

        _lastHandledJumpTrigger = JumpTrigger;

        if (UseSharedLockedView)
        {
            ApplySharedLockedViewIfRequested();
            return;
        }

        StartSmoothSnapToRandomAndLock();
    }

    private void StartSmoothSnapToRandomAndLock()
    {
        double margin = 0.08;
        _snapStartCenter = _currentCenter;
        _snapTargetCenter = new Point(
            Lerp(margin, 1.0 - margin, _random.NextDouble()),
            Lerp(margin, 1.0 - margin, _random.NextDouble()));

        _snapStartZoom = _currentZoom;
        _snapTargetZoom = Lerp(1.05, 1.16, _random.NextDouble());
        _snapDurationSeconds = Lerp(0.45, 0.90, _random.NextDouble());
        _snapElapsedSeconds = 0;

        _isLockedOnTarget = false;
        _snapTransitionActive = true;
        _pauseRemaining = 0;

        if (IsActive && _isAttachedToVisualTree && !_scanTimer.IsEnabled)
        {
            _lastTickUtc = DateTime.UtcNow;
            _scanTimer.Start();
        }

        InvalidateVisual();
    }

    private void OnScanTick(object? sender, EventArgs e)
    {
        if (!IsActive)
            return;

        var now = DateTime.UtcNow;
        var dt = (now - _lastTickUtc).TotalSeconds;
        _lastTickUtc = now;
        dt = Math.Clamp(dt, 0.0, 0.20);
        if (dt <= 0)
            return;

        if (UseSharedLockedView && _hasSharedLockedView && !_snapTransitionActive)
        {
            _currentCenter = _sharedLockedCenter;
            _currentZoom = _sharedLockedZoom;
            _isLockedOnTarget = true;
            _pauseRemaining = 0;
            _stirOffset = new Point(0, 0);
        }

        if (_snapTransitionActive)
        {
            _snapElapsedSeconds += dt;
            double t = Math.Clamp(_snapElapsedSeconds / Math.Max(0.2, _snapDurationSeconds), 0.0, 1.0);
            double eased = EaseInOutCubic(t);

            _currentCenter = new Point(
                Lerp(_snapStartCenter.X, _snapTargetCenter.X, eased),
                Lerp(_snapStartCenter.Y, _snapTargetCenter.Y, eased));
            _currentZoom = Lerp(_snapStartZoom, _snapTargetZoom, eased);
            _stirOffset = new Point(0, 0);

            if (t >= 1.0)
            {
                _snapTransitionActive = false;
                _isLockedOnTarget = true;
                _sharedLockedCenter = _currentCenter;
                _sharedLockedZoom = _currentZoom;
                _hasSharedLockedView = true;
                HandleLoadFxStateChanged();
            }

            InvalidateVisual();
            return;
        }

        if (_isLockedOnTarget)
        {
            bool animateLockedOverlay = ShouldAnimateLockedFeed();
            if (animateLockedOverlay)
            {
                _focusPhase += dt * 0.55;
                _scanLinePhase += dt * (ShouldAnimateLockedFeed() ? 0.82 : 0.30);
                if (_scanLinePhase > 1)
                    _scanLinePhase -= Math.Floor(_scanLinePhase);
                if (ShouldAnimateLockedFeed())
                    UpdateLockedLoadFx(dt);
                else
                    _loadFxPhase += dt * 2.1;
                _stirOffset = new Point(0, 0);
                InvalidateVisual();
                return;
            }

            _stirOffset = new Point(0, 0);
            if (_scanTimer.IsEnabled)
                _scanTimer.Stop();
            return;
        }

        _focusPhase += dt * 0.65;
        _scanLinePhase += dt * 0.27;
        if (_scanLinePhase > 1)
            _scanLinePhase -= Math.Floor(_scanLinePhase);

        UpdateStirEffect(dt);

        if (_pauseRemaining > 0)
        {
            _pauseRemaining = Math.Max(0, _pauseRemaining - dt);
            InvalidateVisual();
            return;
        }

        var start = _scanPath[_segmentIndex];
        var nextIndex = (_segmentIndex + 1) % _scanPath.Length;
        var end = _scanPath[nextIndex];
        var distance = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2));
        var segmentDuration = Math.Max(0.25, distance / ScanSpeed);

        _segmentProgress += dt / segmentDuration;
        if (_segmentProgress >= 1)
        {
            _segmentIndex = nextIndex;
            _segmentProgress = 0;
            _pauseRemaining = _scanPath[_segmentIndex].PauseSeconds;
            _currentCenter = new Point(_scanPath[_segmentIndex].X, _scanPath[_segmentIndex].Y);
            _currentZoom = _scanPath[_segmentIndex].Zoom;
            InvalidateVisual();
            return;
        }

        var easedSegment = EaseInOutCubic(_segmentProgress);
        _currentCenter = new Point(
            Lerp(start.X, end.X, easedSegment),
            Lerp(start.Y, end.Y, easedSegment));

        var focusPulse = Math.Sin(_focusPhase * Math.PI * 2) * FocusPulseAmplitude;
        _currentZoom = Math.Clamp(Lerp(start.Zoom, end.Zoom, easedSegment) + focusPulse, 0.95, 1.16);

        InvalidateVisual();
    }

    private bool ShouldAnimateLockedFeed()
    {
        double progress = Math.Clamp(LoadProgress, 0.0, 1.0);
        return EnableLoadFx && progress > 0.0 && progress < 1.0;
    }

    private void HandleLoadFxStateChanged()
    {
        if (!IsActive || !_isAttachedToVisualTree)
        {
            InvalidateVisual();
            return;
        }

        if (_isLockedOnTarget && ShouldAnimateLockedFeed())
        {
            if (!_scanTimer.IsEnabled)
            {
                _lastTickUtc = DateTime.UtcNow;
                _scanTimer.Start();
            }
        }
        else if (_isLockedOnTarget && !_snapTransitionActive && _scanTimer.IsEnabled)
        {
            _scanTimer.Stop();
        }

        InvalidateVisual();
    }

    private void UpdateLockedLoadFx(double dt)
    {
        _loadFxPhase += dt * 10.0;
        double ramp = 1.0 - Math.Clamp(LoadProgress, 0.0, 1.0);
        _loadFxPhase += ramp * 2.4;
        _stirOffset = new Point(0, 0);
    }

    private void UpdateStirEffect(double dt)
    {
        _stirPhase += dt * 42.0;

        if (_stirDurationRemaining > 0)
        {
            _stirDurationRemaining = Math.Max(0, _stirDurationRemaining - dt);
            double t = _stirDurationRemaining / Math.Max(0.001, _stirDurationSeconds);
            double envelope = 0.30 + (0.70 * t);

            double x = Math.Sin((_stirPhase * 0.85) + _stirNoiseSeed) * _stirStrengthPixels * envelope;
            double y = Math.Cos((_stirPhase * 1.03) + (_stirNoiseSeed * 0.7)) * (_stirStrengthPixels * 0.78) * envelope;
            _stirOffset = new Point(x, y);
            return;
        }

        _stirCooldownRemaining -= dt;
        if (_stirCooldownRemaining <= 0)
        {
            _stirDurationSeconds = Lerp(0.14, 0.52, _random.NextDouble());
            _stirDurationRemaining = _stirDurationSeconds;
            _stirStrengthPixels = Lerp(0.8, 2.6, _random.NextDouble());
            _stirNoiseSeed = _random.NextDouble() * Math.PI * 2;
            _stirCooldownRemaining = Lerp(2.4, 5.8, _random.NextDouble());
            return;
        }

        double idleX = Math.Sin(_stirPhase * 0.12) * 0.22;
        double idleY = Math.Cos(_stirPhase * 0.10) * 0.18;
        _stirOffset = new Point(idleX, idleY);
    }

    private void DrawScannerImage(DrawingContext context)
    {
        if (_scannerMap == null)
            return;

        Point center = _currentCenter;
        double zoom = _currentZoom;
        if (UseExternalCamera)
        {
            center = new Point(ClampUnit(ExternalCameraCenterX), ClampUnit(ExternalCameraCenterY));
            zoom = ClampExternalZoom(ExternalCameraZoom);
        }
        else if (UseSharedLockedView && _hasSharedLockedView)
        {
            center = _sharedLockedCenter;
            zoom = _sharedLockedZoom;
        }

        var source = CalculateSourceRect(Bounds.Size, _scannerMap.Size, center, zoom);
        var destination = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawImage(_scannerMap, source, destination);
    }

    private Rect CalculateSourceRect(Size destinationSize, Size sourceSize, Point center, double zoom)
    {
        var width = sourceSize.Width;
        var height = sourceSize.Height;
        var destinationAspect = destinationSize.Width / Math.Max(1, destinationSize.Height);

        var viewportWidth = Math.Clamp(width * (BaseViewportWidth / Math.Max(0.1, zoom)), width * 0.15, width * 0.72);
        var viewportHeight = viewportWidth / destinationAspect;

        if (viewportHeight > height * 0.90)
        {
            viewportHeight = height * 0.90;
            viewportWidth = viewportHeight * destinationAspect;
        }

        if (viewportWidth > width * 0.90)
        {
            viewportWidth = width * 0.90;
            viewportHeight = viewportWidth / destinationAspect;
        }

        var centerX = center.X * width;
        var centerY = center.Y * height;
        var x = Math.Clamp(centerX - (viewportWidth / 2), 0, width - viewportWidth);
        var y = Math.Clamp(centerY - (viewportHeight / 2), 0, height - viewportHeight);

        return new Rect(x, y, viewportWidth, viewportHeight);
    }

    private void DrawHudOverlay(DrawingContext context)
    {
        double width = Bounds.Width;
        double height = Bounds.Height;
        double ox = 0;
        double oy = 0;

        var shade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(12, 24, 11, 6), 0),
                new GradientStop(Color.FromArgb(22, 42, 17, 8), 1)
            }
        };
        context.FillRectangle(shade, new Rect(0, 0, width, height));

        bool animateLines = ShowHud || ShouldAnimateLockedFeed();
        if (animateLines)
        {
            var rasterPen = new Pen(new SolidColorBrush(Color.FromArgb(26, 224, 248, 255)), 0.9);
            double rasterStart = (_scanLinePhase * 7.0) % 7.0;
            for (double y = -7 + rasterStart; y < height + 7; y += 7)
                context.DrawLine(rasterPen, new Point(ox, y + oy), new Point(width + ox, y + oy));

            var verticalPen = new Pen(new SolidColorBrush(Color.FromArgb(14, 198, 226, 255)), 0.8);
            double columnStart = (_scanLinePhase * 34.0) % 62.0;
            for (double x = -62 + columnStart; x < width + 62; x += 62)
                context.DrawLine(verticalPen, new Point(x + ox, oy), new Point(x + ox, height + oy));

            for (int i = 0; i < 3; i++)
            {
                double phase = _scanLinePhase + (i * 0.31);
                phase -= Math.Floor(phase);
                double scanY = (phase * height) + oy;
                byte alpha = (byte)(152 - (i * 36));
                double thickness = i == 0 ? 1.8 : 1.2;
                var sweepPen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 170, 112)), thickness);
                context.DrawLine(sweepPen, new Point(ox, scanY), new Point(width + ox, scanY));
            }

            DrawLoadGlitchOverlay(context, width, height, ox, oy);
        }

        if (!ShowHud)
            return;

        var hudPen = new Pen(new SolidColorBrush(Color.FromArgb(184, 248, 241, 228)), 1.35);
        var inactiveColor = Color.FromArgb(220, 244, 242, 238);
        var activeColor = Color.FromArgb(232, 255, 52, 34);
        var targetColor = IsTargetActive ? activeColor : inactiveColor;
        var targetPen = new Pen(new SolidColorBrush(targetColor), 2.05);

        double rulerY = 34 + oy;
        double rulerStartX = (width * 0.24) + ox;
        double rulerEndX = (width * 0.76) + ox;
        context.DrawLine(hudPen, new Point(rulerStartX, rulerY), new Point(rulerEndX, rulerY));
        const int topTickCount = 28;
        for (int i = 0; i <= topTickCount; i++)
        {
            double t = i / (double)topTickCount;
            double x = Lerp(rulerStartX, rulerEndX, t);
            double len = i % 5 == 0 ? 13 : 7;
            context.DrawLine(hudPen, new Point(x, rulerY), new Point(x, rulerY + len));
        }

        double sideX = (width - 34) + ox;
        double sideTop = (height * 0.29) + oy;
        double sideBottom = (height * 0.82) + oy;
        context.DrawLine(hudPen, new Point(sideX, sideTop), new Point(sideX, sideBottom));
        const int sideTickCount = 18;
        for (int i = 0; i <= sideTickCount; i++)
        {
            double t = i / (double)sideTickCount;
            double y = Lerp(sideTop, sideBottom, t);
            double len = i % 4 == 0 ? 22 : 11;
            context.DrawLine(hudPen, new Point(sideX, y), new Point(sideX + len, y));
        }

        DrawPlusMark(context, hudPen, new Point(96 + ox, 56 + oy), 8);
        DrawPlusMark(context, hudPen, new Point((width - 96) + ox, 56 + oy), 8);
        DrawPlusMark(context, hudPen, new Point(96 + ox, (height - 56) + oy), 8);
        DrawPlusMark(context, hudPen, new Point((width - 96) + ox, (height - 56) + oy), 8);

        var center = new Point((width * 0.5) + ox, (height * 0.5) + oy);
        double lockWidth = Math.Min(212, width * 0.34);
        double lockHeight = Math.Min(158, height * 0.30);
        double cornerLen = Math.Max(16, Math.Min(lockWidth, lockHeight) * 0.18);

        double left = center.X - (lockWidth * 0.5);
        double right = center.X + (lockWidth * 0.5);
        double top = center.Y - (lockHeight * 0.5);
        double bottom = center.Y + (lockHeight * 0.5);

        DrawCornerBracket(context, targetPen, new Point(left, top), cornerLen, cornerLen, 1, 1);
        DrawCornerBracket(context, targetPen, new Point(right, top), cornerLen, cornerLen, -1, 1);
        DrawCornerBracket(context, targetPen, new Point(left, bottom), cornerLen, cornerLen, 1, -1);
        DrawCornerBracket(context, targetPen, new Point(right, bottom), cornerLen, cornerLen, -1, -1);

        double arm = 24;
        context.DrawLine(targetPen, new Point(center.X - arm, center.Y), new Point(center.X - 10, center.Y));
        context.DrawLine(targetPen, new Point(center.X + 10, center.Y), new Point(center.X + arm, center.Y));
        context.DrawLine(targetPen, new Point(center.X, center.Y - arm), new Point(center.X, center.Y - 10));
        context.DrawLine(targetPen, new Point(center.X, center.Y + 10), new Point(center.X, center.Y + arm));

        double xArm = 8;
        context.DrawLine(targetPen, new Point(center.X - xArm, center.Y - xArm), new Point(center.X - 2, center.Y - 2));
        context.DrawLine(targetPen, new Point(center.X + xArm, center.Y - xArm), new Point(center.X + 2, center.Y - 2));
        context.DrawLine(targetPen, new Point(center.X - xArm, center.Y + xArm), new Point(center.X - 2, center.Y + 2));
        context.DrawLine(targetPen, new Point(center.X + xArm, center.Y + xArm), new Point(center.X + 2, center.Y + 2));

        DrawStatusBar(context, center, width, oy, IsTargetActive);
    }

    private void DrawLoadGlitchOverlay(DrawingContext context, double width, double height, double ox, double oy)
    {
        if (!ShouldAnimateLockedFeed())
            return;

        double ramp = 1.0 - Math.Clamp(LoadProgress, 0.0, 1.0);
        if (ramp <= 0.01)
            return;

        byte bandAlpha = (byte)Math.Clamp(22 + (ramp * 84), 18, 110);
        byte edgeAlpha = (byte)Math.Clamp(38 + (ramp * 108), 26, 146);
        var bandBrush = new SolidColorBrush(Color.FromArgb(bandAlpha, 255, 176, 112));
        var edgePen = new Pen(new SolidColorBrush(Color.FromArgb(edgeAlpha, 255, 222, 182)), 1.0 + (ramp * 0.8));

        for (int i = 0; i < 3; i++)
        {
            double phase = (_scanLinePhase * (2.1 + (i * 0.45))) + (i * 0.23);
            phase -= Math.Floor(phase);
            double y = (phase * height) + oy;
            double h = 5 + (i * 3) + (ramp * 7);
            double jitter = Math.Sin((_loadFxPhase * 9.0) + (i * 2.1)) * (1.6 + (ramp * 5.8));
            double shift = (Math.Sin((_scanLinePhase * 36.0) + (i * 1.7)) * (2.4 + (ramp * 9.0))) + jitter;
            var rect = new Rect(ox + shift - 8, y, width + 16, h);
            context.FillRectangle(bandBrush, rect);
            context.DrawLine(edgePen, new Point(rect.X, rect.Y + rect.Height), new Point(rect.X + rect.Width, rect.Y + rect.Height));
        }
    }

    private static void DrawStatusBar(DrawingContext context, Point center, double width, double oy, bool isActive)
    {
        var lineColor = isActive ? Color.FromArgb(230, 255, 42, 28) : Color.FromArgb(214, 238, 238, 238);
        var linePen = new Pen(new SolidColorBrush(lineColor), 2.2);

        double barsY = center.Y + 98;
        context.DrawLine(linePen, new Point(center.X - 148, barsY), new Point(center.X + 148, barsY));

        string statusText = isActive ? "ACTIVE" : "INACTIVE";
        double panelW = 172;
        double panelH = 34;
        var panelRect = new Rect(center.X - (panelW / 2), barsY + 8, panelW, panelH);

        var bgBrush = isActive
            ? new SolidColorBrush(Color.FromArgb(56, 255, 26, 26))
            : new SolidColorBrush(Color.FromArgb(16, 255, 255, 255));
        var borderPen = new Pen(new SolidColorBrush(lineColor), 1.4);
        var textBrush = isActive
            ? new SolidColorBrush(Color.FromArgb(255, 255, 40, 32))
            : new SolidColorBrush(Color.FromArgb(238, 245, 245, 245));

        context.FillRectangle(bgBrush, panelRect);
        context.DrawRectangle(null, borderPen, panelRect);

        var ft = new FormattedText(
            statusText,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Bahnschrift", FontStyle.Normal, FontWeight.SemiBold),
            16,
            textBrush);

        context.DrawText(ft, new Point(panelRect.X + ((panelRect.Width - ft.Width) * 0.5), panelRect.Y + ((panelRect.Height - ft.Height) * 0.5)));
    }

    private static void DrawPlusMark(DrawingContext context, Pen pen, Point center, double size)
    {
        context.DrawLine(pen, new Point(center.X - size, center.Y), new Point(center.X + size, center.Y));
        context.DrawLine(pen, new Point(center.X, center.Y - size), new Point(center.X, center.Y + size));
    }

    private static void DrawCornerBracket(
        DrawingContext context,
        Pen pen,
        Point corner,
        double lengthX,
        double lengthY,
        int directionX,
        int directionY)
    {
        context.DrawLine(pen, corner, new Point(corner.X + (lengthX * directionX), corner.Y));
        context.DrawLine(pen, corner, new Point(corner.X, corner.Y + (lengthY * directionY)));
    }

    private void DrawFallbackBackground(DrawingContext context)
    {
        var fallbackBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(255, 36, 15, 10), 0),
                new GradientStop(Color.FromArgb(255, 12, 7, 7), 1)
            }
        };

        context.FillRectangle(fallbackBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }

    private bool TryEnsureScannerMap()
    {
        if (_scannerMap != null)
            return true;
        if (_triedLoadingMap)
            return false;

        _triedLoadingMap = true;
        try
        {
            using var stream = AssetLoader.Open(ScannerMapUri);
            _scannerMap = new Bitmap(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static double Lerp(double from, double to, double t)
        => from + ((to - from) * t);

    private static double ClampUnit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.5;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private static double ClampExternalZoom(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 1.0;
        return Math.Clamp(value, 1.0, 2.6);
    }

    private static double EaseInOutCubic(double t)
        => t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private readonly record struct ScanWaypoint(double X, double Y, double PauseSeconds, double Zoom);
}
