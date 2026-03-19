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

    private static readonly Uri ScannerMapUri =
        new("avares://MarsRover.UI/Assets/MarsSurfaceScannerMap.png");

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttachedToVisualTree = true;
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
            if (ShowHud)
                DrawHudOverlay(context);
            return;
        }

        DrawScannerImage(context);
        if (ShowHud)
            DrawHudOverlay(context);
    }

    private void HandleActiveChanged()
    {
        if (IsActive && _isAttachedToVisualTree && (!_isLockedOnTarget || _snapTransitionActive))
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
                _scanTimer.Stop();
            }

            InvalidateVisual();
            return;
        }

        if (_isLockedOnTarget)
            return;

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

        var stirredCenter = new Point(
            _currentCenter.X + (_stirOffset.X / Math.Max(1.0, _scannerMap.Size.Width)),
            _currentCenter.Y + (_stirOffset.Y / Math.Max(1.0, _scannerMap.Size.Height)));

        var source = CalculateSourceRect(Bounds.Size, _scannerMap.Size, stirredCenter, _currentZoom);

        const double overscan = 5.0;
        var destination = new Rect(
            _stirOffset.X - overscan,
            _stirOffset.Y - overscan,
            Bounds.Width + (overscan * 2),
            Bounds.Height + (overscan * 2));

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
        double ox = _stirOffset.X;
        double oy = _stirOffset.Y;

        var shade = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(94, 12, 6, 3), 0),
                new GradientStop(Color.FromArgb(148, 28, 11, 6), 1)
            }
        };
        context.FillRectangle(shade, new Rect(0, 0, width, height));

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

    private static double EaseInOutCubic(double t)
        => t < 0.5
            ? 4 * t * t * t
            : 1 - Math.Pow(-2 * t + 2, 3) / 2;

    private readonly record struct ScanWaypoint(double X, double Y, double PauseSeconds, double Zoom);
}
