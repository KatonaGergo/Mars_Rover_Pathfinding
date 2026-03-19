using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using MarsRover.Core.Algorithm;
using MarsRover.Core.Models;
using MarsRover.Core.Simulation;
using System.Reflection;

namespace MarsRover.UI.Controls;

/// <summary>
/// Renders the 50×50 Mars map
/// </summary>
public class MapCanvas : Control
{
    // ── Styled properties ─────────────────────────────────────────────────────

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

    public GameMap?          GameMap    { get => GetValue(GameMapProperty);    set => SetValue(GameMapProperty, value); }
    public int               RoverX     { get => GetValue(RoverXProperty);     set => SetValue(RoverXProperty, value); }
    public int               RoverY     { get => GetValue(RoverYProperty);     set => SetValue(RoverYProperty, value); }
    public bool              IsNight    { get => GetValue(IsNightProperty);    set => SetValue(IsNightProperty, value); }
    public List<StepRecord>? GhostTrail { get => GetValue(GhostTrailProperty); set => SetValue(GhostTrailProperty, value); }
    public int               GhostIndex { get => GetValue(GhostIndexProperty); set => SetValue(GhostIndexProperty, value); }
    public bool              IsGhostMode{ get => GetValue(IsGhostModeProperty);set => SetValue(IsGhostModeProperty, value); }

    // ── Tile brushes ──────────────────────────────────────────────────────────
    private static readonly IBrush ObstacleBrush = new SolidColorBrush(Color.FromArgb(190, 65, 72, 88));
    private static readonly IBrush MineralBBrush = new SolidColorBrush(Color.FromArgb(220, 43, 127, 255));
    private static readonly IBrush MineralYBrush = new SolidColorBrush(Color.FromArgb(220, 255, 201, 35));
    private static readonly IBrush MineralGBrush = new SolidColorBrush(Color.FromArgb(220, 58, 203, 104));
    private static readonly IBrush StartBrush    = new SolidColorBrush(Color.FromArgb(150, 255, 130, 70));
    private static readonly IBrush RoverBrush    = new SolidColorBrush(Color.FromArgb(235, 255, 107, 53));
    private static readonly IBrush NightOverlay  = new SolidColorBrush(Color.FromArgb(90, 0, 5, 20));
    private static readonly IBrush GridBrush     = new SolidColorBrush(Color.FromArgb(52, 255, 240, 224));
    private static readonly Pen    GridPen       = new Pen(GridBrush, 0.6);

    // Ghost trail brushes — indexed by StepEvent
    private static readonly Color TrailMove           = Color.FromArgb(120, 255, 140, 60);   // orange
    private static readonly Color TrailMine           = Color.FromArgb(200, 60,  220, 100);  // bright green
    private static readonly Color TrailBatteryLow     = Color.FromArgb(160, 255, 210, 0);    // yellow
    private static readonly Color TrailBatteryCrit    = Color.FromArgb(200, 255, 60,  30);   // red-orange
    private static readonly Color TrailDead           = Color.FromArgb(255, 220, 20,  20);   // red
    private static readonly Color TrailHome           = Color.FromArgb(220, 60,  140, 255);  // blue
    private static readonly Color TrailStandby        = Color.FromArgb(80,  180, 180, 180);  // grey

    static MapCanvas()
    {
        GameMapProperty   .Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        RoverXProperty    .Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        RoverYProperty    .Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        IsNightProperty   .Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        GhostTrailProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        GhostIndexProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
        IsGhostModeProperty.Changed.AddClassHandler<MapCanvas>((c, _) => c.InvalidateVisual());
    }



    private readonly Bitmap _rockImg = new Bitmap(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/akadaly.png")));
    private readonly Bitmap _mineralB = new Bitmap(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/kekasvany.png")));
    private readonly Bitmap _mineralY = new Bitmap(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/sargaasvany.png")));
    private readonly Bitmap _mineralG = new Bitmap(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/zoldasvany.png")));
    private readonly Bitmap _rover = new Bitmap(AssetLoader.Open(new Uri("avares://MarsRover.UI/Assets/Models/rover.png")));




    // ── Rendering ─────────────────────────────────────────────────────────────

    public override void Render(DrawingContext ctx)
    {
        var map = GameMap;
        if (map == null) return;

        double cellW = Bounds.Width  / GameMap_.Width;
        double cellH = Bounds.Height / GameMap_.Height;

        // ── 1. Tiles ──────────────────────────────────────────────────────────
        for (int y = 0; y < GameMap_.Height; y++)
            for (int x = 0; x < GameMap_.Width; x++)
            {
                var rect = new Rect(x * cellW, y * cellH, cellW, cellH);
                var type = map.GetTile(x, y);
                bool hasMineral = map.HasMineral(x, y);

                // Megkeressük, melyik képet kell rajzolni
                Bitmap? imageToDraw = type switch
                {
                    TileType.Obstacle => _rockImg,
                    TileType.MineralB when hasMineral => _mineralB,
                    TileType.MineralY when hasMineral => _mineralY,
                    TileType.MineralG when hasMineral => _mineralG,
                    _ => null
                };

                if (imageToDraw != null)
                {
                    // Itt rajzoljuk ki a képet a színes négyzet helyett!
                    ctx.DrawImage(imageToDraw, rect);
                }
                else if (type == TileType.Start)
                {
                    ctx.FillRectangle(StartBrush, rect);
                }
            }

        // ── 2. Grid lines ─────────────────────────────────────────────────────
        for (int y = 0; y <= GameMap_.Height; y++)
            ctx.DrawLine(GridPen, new Point(0, y * cellH), new Point(Bounds.Width, y * cellH));
        for (int x = 0; x <= GameMap_.Width; x++)
            ctx.DrawLine(GridPen, new Point(x * cellW, 0), new Point(x * cellW, Bounds.Height));

        // ── 3. Night overlay ──────────────────────────────────────────────────
        if (IsNight && !IsGhostMode)
            ctx.FillRectangle(NightOverlay, new Rect(0, 0, Bounds.Width, Bounds.Height));

        // ── 4. Ghost trail ────────────────────────────────────────────────────
        var trail = GhostTrail;
        if (IsGhostMode && trail != null && trail.Count > 0)
        {
            int upTo = Math.Min(GhostIndex, trail.Count);

            for (int i = 0; i < upTo; i++)
            {
                var step  = trail[i];
                double age = (double)(upTo - i) / upTo; // 1.0=oldest, 0.0=newest

                // Fade older steps out
                var  src   = GetTrailColor(step.Event);
                byte alpha = (byte)(src.A * (1.0 - age * 0.7));
                var  col   = new Color(alpha, src.R, src.G, src.B);

                double cx = step.X * cellW + cellW * 0.5;
                double cy = step.Y * cellH + cellH * 0.5;
                double r  = Math.Min(cellW, cellH) * 0.28;

                ctx.DrawEllipse(new SolidColorBrush(col), null,
                                new Point(cx, cy), r, r);
            }

            // ── Event flashes: draw larger markers for significant events ─────
            for (int i = 0; i < upTo; i++)
            {
                var step = trail[i];
                if (step.Event is StepEvent.Move or StepEvent.Standby) continue;

                double cx = step.X * cellW + cellW * 0.5;
                double cy = step.Y * cellH + cellH * 0.5;
                double r  = Math.Min(cellW, cellH) * 0.42;

                // Outer ring marker
                var col  = GetTrailColor(step.Event);
                var col2 = new Color(200, col.R, col.G, col.B);
                var pen  = new Pen(new SolidColorBrush(col2), 1.5);
                ctx.DrawEllipse(null, pen, new Point(cx, cy), r, r);

                // Event symbol in centre
                string symbol = step.Event switch
                {
                    StepEvent.MineSuccess     => "⛏",
                    StepEvent.BatteryLow      => "⚡",
                    StepEvent.BatteryCritical => "⚡",
                    StepEvent.BatteryDead     => "✕",
                    StepEvent.ReturnHome      => "⌂",
                    _                          => ""
                };

                if (symbol != "")
                {
                    var ft = new FormattedText(
                        symbol,
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight,
                        new Typeface("Arial"),
                        Math.Min(cellW, cellH) * 0.55,
                        new SolidColorBrush(col));

                    ctx.DrawText(ft, new Point(cx - ft.Width / 2, cy - ft.Height / 2));
                }
            }

            // ── Ghost rover: current position ─────────────────────────────────
            // ── Ghost rover: current position (Képpel és áttetszőséggel) ───────────────
            if (GhostIndex > 0 && GhostIndex <= trail.Count)
            {
                var cur = trail[Math.Min(GhostIndex, trail.Count) - 1];

                // Pozíció kiszámítása (ugyanaz a méretezés, mint a sima rovernél)
                double gx = cur.X * cellW + cellW * 0.1;
                double gy = cur.Y * cellH + cellH * 0.1;
                var ghostRect = new Rect(gx, gy, cellW * 0.8, cellH * 0.8);

                // PushOpacity: minden, ami a blokkon belül van, 50%-ban átlátszó lesz
                using (ctx.PushOpacity(0.5))
                {
                    ctx.DrawImage(_rover, ghostRect);
                }

                // Opcionális: Egy vékony keret, hogy jobban elkülönüljön a háttértől
                ctx.DrawRectangle(null,
                                  new Pen(new SolidColorBrush(Color.Parse("#FF6B35")), 1.0),
                                  ghostRect);
            }

            // ── Episode info overlay ──────────────────────────────────────────
            var overlayText = new FormattedText(
                $"Ep {(trail.Count > 0 ? GhostIndex : 0)}/{(trail?.Count ?? 0)} steps",
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Arial", weight: FontWeight.Bold),
                11,
                new SolidColorBrush(Color.FromArgb(200, 255, 200, 100)));

            ctx.DrawText(overlayText, new Point(6, 6));
        }
        else if (!IsGhostMode)
        {
            // ── Normal rover ──────────────────────────────────────────────────
            double rx = RoverX * cellW + cellW * 0.15;
            double ry = RoverY * cellH + cellH * 0.15;
            var roverRect = new Rect(rx, ry, cellW * 0.7, cellH * 0.7);

            ctx.DrawImage(_rover, roverRect);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Color GetTrailColor(StepEvent evt) => evt switch
    {
        StepEvent.MineSuccess     => TrailMine,
        StepEvent.BatteryLow      => TrailBatteryLow,
        StepEvent.BatteryCritical => TrailBatteryCrit,
        StepEvent.BatteryDead     => TrailDead,
        StepEvent.ReturnHome      => TrailHome,
        StepEvent.Standby         => TrailStandby,
        _                          => TrailMove
    };

    private static IBrush? TileBrush(GameMap map, int x, int y)
    {
        bool hasMineral = map.HasMineral(x, y);
        return map.GetTile(x, y) switch
        {
            TileType.Obstacle => ObstacleBrush,
            TileType.Start    => StartBrush,
            TileType.MineralB => hasMineral ? MineralBBrush : null,
            TileType.MineralY => hasMineral ? MineralYBrush : null,
            TileType.MineralG => hasMineral ? MineralGBrush : null,
            _                  => null
        };
    }

    private static class GameMap_
    {
        public const int Width  = MarsRover.Core.Simulation.GameMap.Width;
        public const int Height = MarsRover.Core.Simulation.GameMap.Height;
    }
}
