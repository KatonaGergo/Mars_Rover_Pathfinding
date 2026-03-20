using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;

namespace MarsRover.UI.Controls;

/// <summary>
/// Renders the 50x50 Mars map.
/// </summary>
public class MapCanvas : Control
{
    // Styled properties

    public static readonly StyledProperty<GameMap?> GameMapProperty =
        AvaloniaProperty.Register<MapCanvas, GameMap?>(nameof(GameMap));

    public static readonly StyledProperty<int> RoverXProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(RoverX));

    public static readonly StyledProperty<int> RoverYProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(RoverY));

    public static readonly StyledProperty<bool> IsNightProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(IsNight));

    // Ghost trail, list of (x, y, event, reward) up to current ghost position
    public static readonly StyledProperty<List<StepRecord>?> GhostTrailProperty =
        AvaloniaProperty.Register<MapCanvas, List<StepRecord>?>(nameof(GhostTrail));

    // Index into the trail that represents the ghost rover's current position
    public static readonly StyledProperty<int> GhostIndexProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(GhostIndex));

    public static readonly StyledProperty<bool> IsGhostModeProperty =
        AvaloniaProperty.Register<MapCanvas, bool>(nameof(IsGhostMode));

    public static readonly StyledProperty<double> TileRevealProgressProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(TileRevealProgress), 1.0);

    public static readonly StyledProperty<double> CameraCenterXProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(CameraCenterX), 0.5);

    public static readonly StyledProperty<double> CameraCenterYProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(CameraCenterY), 0.5);

    public static readonly StyledProperty<double> CameraZoomProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(CameraZoom), 1.0);

    public static readonly StyledProperty<double> ZoomFxFocusXProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(ZoomFxFocusX), 0.5);

    public static readonly StyledProperty<double> ZoomFxFocusYProperty =
        AvaloniaProperty.Register<MapCanvas, double>(nameof(ZoomFxFocusY), 0.5);

    public static readonly StyledProperty<int> ZoomFxTriggerProperty =
        AvaloniaProperty.Register<MapCanvas, int>(nameof(ZoomFxTrigger), 0);

    public GameMap? GameMap
    {
        get => GetValue(GameMapProperty);
        set => SetValue(GameMapProperty, value);
    }

    public int RoverX
    {
        get => GetValue(RoverXProperty);
        set => SetValue(RoverXProperty, value);
    }

    public int RoverY
    {
        get => GetValue(RoverYProperty);
        set => SetValue(RoverYProperty, value);
    }

    public bool IsNight
    {
        get => GetValue(IsNightProperty);
        set => SetValue(IsNightProperty, value);
    }

    public List<StepRecord>? GhostTrail
    {
        get => GetValue(GhostTrailProperty);
        set => SetValue(GhostTrailProperty, value);
    }

    public int GhostIndex
    {
        get => GetValue(GhostIndexProperty);
        set => SetValue(GhostIndexProperty, value);
    }

    public bool IsGhostMode
    {
        get => GetValue(IsGhostModeProperty);
        set => SetValue(IsGhostModeProperty, value);
    }

    public double TileRevealProgress
    {
        get => GetValue(TileRevealProgressProperty);
        set => SetValue(TileRevealProgressProperty, value);
    }

    public double CameraCenterX
    {
        get => GetValue(CameraCenterXProperty);
        set => SetValue(CameraCenterXProperty, value);
    }

    public double CameraCenterY
    {
        get => GetValue(CameraCenterYProperty);
        set => SetValue(CameraCenterYProperty, value);
    }

    public double CameraZoom
    {
        get => GetValue(CameraZoomProperty);
        set => SetValue(CameraZoomProperty, value);
    }

    public double ZoomFxFocusX
    {
        get => GetValue(ZoomFxFocusXProperty);
        set => SetValue(ZoomFxFocusXProperty, value);
    }

    public double ZoomFxFocusY
    {
        get => GetValue(ZoomFxFocusYProperty);
        set => SetValue(ZoomFxFocusYProperty, value);
    }

    public int ZoomFxTrigger
    {
        get => GetValue(ZoomFxTriggerProperty);
        set => SetValue(ZoomFxTriggerProperty, value);
    }

    // Brushes and colors

    private static readonly IBrush NightOverlay = new SolidColorBrush(Color.FromArgb(90, 0, 5, 20));
    private static readonly IBrush GridBrush = new SolidColorBrush(Color.FromArgb(52, 255, 240, 224));
    private static readonly Pen GridPen = new Pen(GridBrush, 0.6);

    // Ghost trail palette (cool contrast)
    private static readonly Color TrailMove = Color.FromArgb(190, 0x3E, 0xD8, 0xFF);
    private static readonly Color TrailMine = Color.FromArgb(215, 0x5A, 0xF2, 0xA0);
    private static readonly Color TrailBatteryLow = Color.FromArgb(210, 0xE8, 0xFF, 0x66);
    private static readonly Color TrailBatteryCrit = Color.FromArgb(225, 0xFF, 0x4D, 0xA6);
    private static readonly Color TrailDead = Color.FromArgb(245, 0xFF, 0x3B, 0x3B);
    private static readonly Color TrailHome = Color.FromArgb(215, 0x7F, 0xA9, 0xFF);
    private static readonly Color TrailStandby = Color.FromArgb(160, 0xA5, 0xAF, 0xBD);

    private static readonly Color TrailOutline = Color.FromArgb(165, 12, 16, 24);
    private static readonly Color EventRingShadow = Color.FromArgb(145, 10, 14, 22);
    private static readonly TimeSpan ZoomFxDuration = TimeSpan.FromMilliseconds(430);

    static MapCanvas()
    {
        GameMapProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        RoverXProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        RoverYProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        IsNightProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        GhostTrailProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        GhostIndexProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        IsGhostModeProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        TileRevealProgressProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        CameraCenterXProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        CameraCenterYProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        CameraZoomProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        ZoomFxFocusXProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        ZoomFxFocusYProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        ZoomFxTriggerProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.StartZoomFx());
    }

    private readonly Bitmap _rockImg = new(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/akadaly.png")));
    private readonly Bitmap _mineralB = new(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/kekasvany.png")));
    private readonly Bitmap _mineralY = new(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/sargaasvany.png")));
    private readonly Bitmap _mineralG = new(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/zoldasvany.png")));
    private readonly Bitmap _rover = new(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/rover2.png")));
    private readonly DispatcherTimer _zoomFxTimer;
    private DateTime _zoomFxStartedUtc;
    private bool _isZoomFxActive;

    public MapCanvas()
    {
        _zoomFxTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _zoomFxTimer.Tick += (_, _) =>
        {
            if (!_isZoomFxActive)
            {
                _zoomFxTimer.Stop();
                return;
            }

            if ((DateTime.UtcNow - _zoomFxStartedUtc) >= ZoomFxDuration)
            {
                _isZoomFxActive = false;
                _zoomFxTimer.Stop();
            }

            InvalidateVisual();
        };
    }

    // Rendering

    public override void Render(DrawingContext ctx)
    {
        var map = GameMap;
        if (map == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        CameraViewport viewport = CalculateViewport();
        double cellW = Bounds.Width / viewport.Width;
        double cellH = Bounds.Height / viewport.Height;

        DrawTiles(ctx, map, viewport, cellW, cellH);
        DrawGrid(ctx, viewport, cellW, cellH);

        if (IsNight && !IsGhostMode)
            ctx.FillRectangle(NightOverlay, new Rect(0, 0, Bounds.Width, Bounds.Height));

        if (IsGhostMode)
            DrawGhostOverlay(ctx, viewport, cellW, cellH);
        else
        {
            bool roverTileVisible = TileRevealProgress >= 1.0 || IsTileVisibleByReveal(RoverX, RoverY);
            if (roverTileVisible)
                DrawRover(ctx, RoverX, RoverY, viewport, cellW, cellH, 0.15, 0.7);
        }

        DrawZoomSpyglassFx(ctx);
    }

    private void DrawTiles(DrawingContext ctx, GameMap map, CameraViewport viewport, double cellW, double cellH)
    {
        int minTileX = Math.Max(0, (int)Math.Floor(viewport.Left));
        int maxTileX = Math.Min(GameMap_.Width - 1, (int)Math.Ceiling(viewport.Right) - 1);
        int minTileY = Math.Max(0, (int)Math.Floor(viewport.Top));
        int maxTileY = Math.Min(GameMap_.Height - 1, (int)Math.Ceiling(viewport.Bottom) - 1);

        for (int y = minTileY; y <= maxTileY; y++)
        {
            for (int x = minTileX; x <= maxTileX; x++)
            {
                if (!IsTileVisibleByReveal(x, y))
                    continue;

                var rect = TileToScreenRect(x, y, viewport, cellW, cellH);
                var type = map.GetTile(x, y);
                bool hasMineral = map.HasMineral(x, y);

                Bitmap? imageToDraw = type switch
                {
                    TileType.Obstacle => _rockImg,
                    TileType.MineralB when hasMineral => _mineralB,
                    TileType.MineralY when hasMineral => _mineralY,
                    TileType.MineralG when hasMineral => _mineralG,
                    _ => null
                };

                if (imageToDraw != null)
                    ctx.DrawImage(imageToDraw, rect);
            }
        }
    }

    private void DrawGrid(DrawingContext ctx, CameraViewport viewport, double cellW, double cellH)
    {
        int firstHorizontal = Math.Max(0, (int)Math.Floor(viewport.Top));
        int lastHorizontal = Math.Min(GameMap_.Height, (int)Math.Ceiling(viewport.Bottom));
        for (int y = firstHorizontal; y <= lastHorizontal; y++)
        {
            double sy = (y - viewport.Top) * cellH;
            ctx.DrawLine(GridPen, new Point(0, sy), new Point(Bounds.Width, sy));
        }

        int firstVertical = Math.Max(0, (int)Math.Floor(viewport.Left));
        int lastVertical = Math.Min(GameMap_.Width, (int)Math.Ceiling(viewport.Right));
        for (int x = firstVertical; x <= lastVertical; x++)
        {
            double sx = (x - viewport.Left) * cellW;
            ctx.DrawLine(GridPen, new Point(sx, 0), new Point(sx, Bounds.Height));
        }
    }

    private void DrawGhostOverlay(DrawingContext ctx, CameraViewport viewport, double cellW, double cellH)
    {
        var trail = GhostTrail;
        if (trail == null || trail.Count == 0)
            return;

        int upTo = Math.Min(GhostIndex, trail.Count);

        for (int i = 0; i < upTo; i++)
        {
            var step = trail[i];
            double age = (double)(upTo - i) / upTo;
            var src = GetTrailColor(step.Event);
            byte alpha = (byte)(src.A * (1.0 - (age * 0.7)));
            var col = new Color(alpha, src.R, src.G, src.B);

            double cx = ((step.X + 0.5) - viewport.Left) * cellW;
            double cy = ((step.Y + 0.5) - viewport.Top) * cellH;
            if (!IsWithinViewportBounds(cx, cy, cellW, cellH))
                continue;

            double r = Math.Min(cellW, cellH) * 0.28;
            var fill = new SolidColorBrush(col);
            var outline = new Pen(new SolidColorBrush(TrailOutline), 1.0);
            ctx.DrawEllipse(fill, outline, new Point(cx, cy), r, r);
        }

        for (int i = 0; i < upTo; i++)
        {
            var step = trail[i];
            if (step.Event is StepEvent.Move or StepEvent.Standby)
                continue;

            double cx = ((step.X + 0.5) - viewport.Left) * cellW;
            double cy = ((step.Y + 0.5) - viewport.Top) * cellH;
            if (!IsWithinViewportBounds(cx, cy, cellW, cellH))
                continue;

            double r = Math.Min(cellW, cellH) * 0.42;
            var col = GetTrailColor(step.Event);

            var shadowPen = new Pen(new SolidColorBrush(EventRingShadow), 2.6);
            var ringPen = new Pen(new SolidColorBrush(new Color(220, col.R, col.G, col.B)), 1.6);
            ctx.DrawEllipse(null, shadowPen, new Point(cx, cy), r + 0.05, r + 0.05);
            ctx.DrawEllipse(null, ringPen, new Point(cx, cy), r, r);

            string symbol = step.Event switch
            {
                StepEvent.MineSuccess => "⛏",
                StepEvent.BatteryLow => "⚡",
                StepEvent.BatteryCritical => "⚡",
                StepEvent.BatteryDead => "✕",
                StepEvent.ReturnHome => "⌂",
                _ => ""
            };

            if (symbol.Length == 0)
                continue;

            var ft = new FormattedText(
                symbol,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial"),
                Math.Min(cellW, cellH) * 0.55,
                new SolidColorBrush(new Color(240, col.R, col.G, col.B)));

            ctx.DrawText(ft, new Point(cx - (ft.Width / 2), cy - (ft.Height / 2)));
        }

        if (GhostIndex > 0 && GhostIndex <= trail.Count)
        {
            var cur = trail[Math.Min(GhostIndex, trail.Count) - 1];
            DrawRover(ctx, cur.X, cur.Y, viewport, cellW, cellH, 0.1, 0.8, ghostOpacity: 0.55, ghostOutlineHex: "#66D9FF");
        }

        var overlayText = new FormattedText(
            $"Ep {(trail.Count > 0 ? GhostIndex : 0)}/{trail.Count} steps",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Arial", weight: FontWeight.Bold),
            11,
            new SolidColorBrush(Color.FromArgb(220, 212, 238, 255)));

        ctx.DrawText(overlayText, new Point(6, 6));
    }

    private void DrawRover(
        DrawingContext ctx,
        int roverTileX,
        int roverTileY,
        CameraViewport viewport,
        double cellW,
        double cellH,
        double insetFactor,
        double sizeFactor,
        double? ghostOpacity = null,
        string? ghostOutlineHex = null)
    {
        double rx = (roverTileX - viewport.Left) * cellW + (cellW * insetFactor);
        double ry = (roverTileY - viewport.Top) * cellH + (cellH * insetFactor);
        var roverRect = new Rect(rx, ry, cellW * sizeFactor, cellH * sizeFactor);

        if (!RectIntersectsBounds(roverRect))
            return;

        if (ghostOpacity.HasValue)
        {
            using (ctx.PushOpacity(ghostOpacity.Value))
                ctx.DrawImage(_rover, roverRect);

            var outlineColor = ghostOutlineHex is null
                ? Color.FromArgb(220, 102, 217, 255)
                : Color.Parse(ghostOutlineHex);
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(outlineColor), 1.2), roverRect);
            return;
        }

        ctx.DrawImage(_rover, roverRect);
    }

    // Helpers

    private CameraViewport CalculateViewport()
    {
        double zoom = ClampZoom(CameraZoom);
        double viewportWidth = GameMap_.Width / zoom;
        double viewportHeight = GameMap_.Height / zoom;

        double centerX = ClampUnit(CameraCenterX) * GameMap_.Width;
        double centerY = ClampUnit(CameraCenterY) * GameMap_.Height;

        double left = Math.Clamp(centerX - (viewportWidth / 2), 0, GameMap_.Width - viewportWidth);
        double top = Math.Clamp(centerY - (viewportHeight / 2), 0, GameMap_.Height - viewportHeight);

        return new CameraViewport(left, top, viewportWidth, viewportHeight);
    }

    private Rect TileToScreenRect(int tileX, int tileY, CameraViewport viewport, double cellW, double cellH)
    {
        double sx = (tileX - viewport.Left) * cellW;
        double sy = (tileY - viewport.Top) * cellH;
        return new Rect(sx, sy, cellW, cellH);
    }

    private bool IsWithinViewportBounds(double x, double y, double cellW, double cellH)
        => x >= -cellW && x <= Bounds.Width + cellW && y >= -cellH && y <= Bounds.Height + cellH;

    private bool RectIntersectsBounds(Rect rect)
        => rect.Right >= 0 && rect.Bottom >= 0 && rect.X <= Bounds.Width && rect.Y <= Bounds.Height;

    private static Color GetTrailColor(StepEvent evt) => evt switch
    {
        StepEvent.MineSuccess => TrailMine,
        StepEvent.BatteryLow => TrailBatteryLow,
        StepEvent.BatteryCritical => TrailBatteryCrit,
        StepEvent.BatteryDead => TrailDead,
        StepEvent.ReturnHome => TrailHome,
        StepEvent.Standby => TrailStandby,
        _ => TrailMove
    };

    private static double ClampZoom(double zoom)
    {
        if (double.IsNaN(zoom) || double.IsInfinity(zoom))
            return 1.0;
        return Math.Clamp(zoom, 1.0, 2.6);
    }

    private static double ClampUnit(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0.5;
        return Math.Clamp(value, 0.0, 1.0);
    }

    private bool IsTileVisibleByReveal(int x, int y)
    {
        double reveal = Math.Clamp(TileRevealProgress, 0.0, 1.0);
        if (reveal >= 1.0)
            return true;

        int totalCells = GameMap_.Width * GameMap_.Height;
        int visibleCells = (int)Math.Floor(reveal * totalCells);
        int tileIndex = (y * GameMap_.Width) + x;
        return tileIndex <= visibleCells;
    }

    private void StartZoomFx()
    {
        _zoomFxStartedUtc = DateTime.UtcNow;
        _isZoomFxActive = true;
        if (!_zoomFxTimer.IsEnabled)
            _zoomFxTimer.Start();
        InvalidateVisual();
    }

    private void DrawZoomSpyglassFx(DrawingContext ctx)
    {
        if (!_isZoomFxActive)
            return;

        double t = Math.Clamp((DateTime.UtcNow - _zoomFxStartedUtc).TotalMilliseconds / ZoomFxDuration.TotalMilliseconds, 0.0, 1.0);
        double strength = 1.0 - t;
        if (strength <= 0.0)
        {
            _isZoomFxActive = false;
            return;
        }

        double fx = ClampUnit(ZoomFxFocusX);
        double fy = ClampUnit(ZoomFxFocusY);
        double cx = fx * Bounds.Width;
        double cy = fy * Bounds.Height;
        double minDim = Math.Min(Bounds.Width, Bounds.Height);
        double pulse = 0.5 + (0.5 * Math.Sin(t * Math.PI));
        double radius = minDim * (0.17 + (0.06 * pulse));

        byte veilAlpha = (byte)Math.Clamp(72 * strength, 0, 255);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(veilAlpha, 6, 10, 18)), new Rect(0, 0, Bounds.Width, Bounds.Height));

        var ringOuter = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((210 * strength) + 20, 0, 255), 160, 222, 255)), 2.0);
        var ringInner = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((160 * strength) + 20, 0, 255), 60, 170, 220)), 1.2);
        var crossPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((190 * strength) + 20, 0, 255), 185, 235, 255)), 1.0);
        var sweepPen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((160 * strength) + 20, 0, 255), 135, 215, 255)), 1.0);

        ctx.DrawEllipse(null, ringOuter, new Point(cx, cy), radius, radius);
        ctx.DrawEllipse(null, ringInner, new Point(cx, cy), radius * 0.68, radius * 0.68);

        double crossShort = radius * 0.28;
        double crossLong = radius * 1.15;
        ctx.DrawLine(crossPen, new Point(cx - crossLong, cy), new Point(cx - crossShort, cy));
        ctx.DrawLine(crossPen, new Point(cx + crossShort, cy), new Point(cx + crossLong, cy));
        ctx.DrawLine(crossPen, new Point(cx, cy - crossLong), new Point(cx, cy - crossShort));
        ctx.DrawLine(crossPen, new Point(cx, cy + crossShort), new Point(cx, cy + crossLong));

        double sweepAngle = (-0.8 + (t * 2.6)) * Math.PI;
        double sx = cx + (Math.Cos(sweepAngle) * radius * 0.9);
        double sy = cy + (Math.Sin(sweepAngle) * radius * 0.9);
        ctx.DrawLine(sweepPen, new Point(cx, cy), new Point(sx, sy));

        var label = new FormattedText(
            $"SPYGLASS {CameraZoom:0.00}x",
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface("Bahnschrift", FontStyle.Normal, FontWeight.SemiBold),
            10,
            new SolidColorBrush(Color.FromArgb((byte)Math.Clamp((220 * strength) + 20, 0, 255), 210, 238, 255)));

        ctx.DrawText(label, new Point(cx - (label.Width * 0.5), cy - radius - 18));
    }

    private readonly record struct CameraViewport(double Left, double Top, double Width, double Height)
    {
        public double Right => Left + Width;
        public double Bottom => Top + Height;
    }

    private static class GameMap_
    {
        public const int Width = MarsRover.Core.Simulation.GameMap.Width;
        public const int Height = MarsRover.Core.Simulation.GameMap.Height;
    }
}
