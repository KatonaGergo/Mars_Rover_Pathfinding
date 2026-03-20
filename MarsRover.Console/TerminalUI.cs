using System.Text;

namespace MarsRover.Console;

public enum MainMenuResult { StartMission, SystemStatus, Exit }

/// <summary>
/// Animated terminal UI: boot/loading screen then main menu.
///
/// Boot screen: sequential system-check messages, some with "working" states
/// that resolve, finishing with a blinking prompt.
///
/// Main menu: continuous animation on a background thread —
///   · Mars planet rotating (ASCII sphere with shifted texture each frame)
///   · OFFLINE indicator breathing (sin-wave through DarkGray → Red → DarkGray)
///   · Signal bars slowly fluctuating
///   · Status messages cycling
///   · Occasional screen glitch (corrupt chars on a planet row for 1 frame)
/// Main thread owns only keyboard input. Up/Down to navigate, Enter to confirm.
/// </summary>
public static class TerminalUI
{
    // Layout
    private const int W        = 80;
    private const int MarsW    = 23;
    private const int MarsH    = 11;
    private const int MarsCol  = (W - MarsW) / 2;   // 28

    private const int R_Header = 0;
    private const int R_Sep1   = 1;
    private const int R_Mars   = 2;
    private const int R_Title  = R_Mars  + MarsH + 1;  // 14
    private const int R_Menu   = R_Title + 4;           // 18  (top border)
    private const int R_Desc   = R_Menu  + 7;           // 25  (description line)
    private const int R_Sep2   = R_Desc  + 1;           // 26
    private const int R_Status = R_Sep2  + 1;           // 27
    private const int R_Hint   = R_Status + 1;          // 28

    // Mars sphere geometry
    // Column range [L, R] (inclusive) per planet row — defines the sphere mask
    private static readonly (int L, int R)[] Mask = new (int L, int R)[]
    {
        (8,  14),   // row 0   narrow top cap
        (5,  17),   // row 1
        (3,  19),   // row 2
        (2,  20),   // row 3
        (1,  21),   // row 4
        (0,  22),   // row 5   equator — widest
        (1,  21),   // row 6
        (2,  20),   // row 7
        (3,  19),   // row 8
        (5,  17),   // row 9
        (8,  14)  // row 10  narrow bottom cap
    };

    // Surface texture per row. Length must be ≥ MarsW (23).
    // These strings tile horizontally; offset shifts the start position each frame
    // to create the rotation illusion.
    private static readonly string[] Tex = new string[]
    {
        "░░▒▒▓▒░░▒▒░░▓▓▒░░▒▒▓░░▒▒░░▒▒▓▒░░▒▒░░▓▓▒░░▒▒▓░░",
        "▒▒░░░▒▒▓▒░░▒░░░▒▒▓░░░▒▒░▒▒░░░▒▒▓▒░░▒░░░▒▒▓░░░▒",
        "░▒▒▓▓░░░▒▒░▒▒▓▓░░░▒▒░▒░░▒▒▓░░░▒▒░▒▒▓▓░░░▒▒░▒▒▓",
        "▓░░░▒▒▒░░▒▒░░░░▒▒▓░░▒▒░░░░▒▒▒░░▒▒░░░░▒▒▓░░▒▒░░",
        "░▒▒░░░░▒▒▓▒░░▒▒░░░░▒▒░░▒▒░░░░▒▒▓▒░░▒▒░░░░▒▒░░▒",
        "▒░░▓▓▒░░░░░▒▒░░▓▓▒░░░▒▒░░▒▒▓▓▒░░░░░▒▒░░▓▓▒░░░▒",
        "░░▒▒░▒▒▒░░▒░░▒▒░▒▒▒░░▒░▒▒░▒▒░▒▒▒░░▒░░▒▒░▒▒▒░░▒",
        "▓▒░░░░░▒▒▓░░▒░░░░░▒▒▓░░░░▒▒░▒░░░░░▒▒▓░░▒░░░░░▒",
        "░░▒▒▓░░▒▒░░░▒▒▓░░▒▒░░▒▒▓░░▒▒░░░▒▒▓░░▒▒░░░▒▒▓░░",
        "▒▒░░░▒▒░░▒▒▒░░░▒▒░░▒▒▒░░░▒▒░░▒▒▒░░░▒▒░░▒▒▒░░░▒",
        "░▓▒░░▒▒▓▓░░▒▒░░▒▓▒░░▒▒▓▓░░▒▒░░▒▓▒░░▒▒▓▓░░▒▒░░▒",
    };

    // Star field (fixed, generated once in static constructor)
    private static readonly (int row, int col, char g)[] Stars;
    private static readonly Random _rng = new(Environment.TickCount);

    static TerminalUI()
    {
        var r    = new Random(17);
        var list = new List<(int, int, char)>();
        char[] gc = new char[] { '·', '·', '·', '·', '*', '·' };

        for (int i = 0; i < 55; i++)
        {
            int row = r.Next(R_Mars, R_Mars + MarsH);
            int col = r.Next(1, W - 1);
            int pr  = row - R_Mars;
            int pc  = col - MarsCol;

            // Skip positions inside or just adjacent to the sphere
            if (pr >= 0 && pr < MarsH &&
                pc >= Mask[pr].L - 2 && pc <= Mask[pr].R + 2)
                continue;

            list.Add((row, col, gc[r.Next(gc.Length)]));
        }

        Stars = list.ToArray();
    }


    // Boot and loading screen


    public static void RunLoadingScreen()
    {
        System.Console.CursorVisible = false;
        System.Console.BackgroundColor = ConsoleColor.Black;
        System.Console.Clear();

        int r = 1;

        // Title box
        WA(r++, 0, Center("╔══════════════════════════════════════════════════════════╗"), ConsoleColor.DarkRed);
        WA(r++, 0, Center("║    MARS AUTONOMOUS NAVIGATION SYSTEM  ·  SYS-7  v2.4    ║"), ConsoleColor.Red);
        WA(r++, 0, Center("╚══════════════════════════════════════════════════════════╝"), ConsoleColor.DarkRed);
        r++;

        // Each entry: working message (or null = instant), final message, delay ms, final color
        var entries = new (string? wk, string dn, int ms, ConsoleColor dc)[]
        {
            (null,
             "[OK]  POWER MANAGEMENT UNIT ONLINE",
             0, ConsoleColor.DarkGreen),
            (null,
             "[OK]  THERMAL REGULATION  ·  CORE TEMP: 18.3°C",
             0, ConsoleColor.DarkGreen),
            (null,
             "[OK]  MEMORY BANKS INITIALIZED  ·  128 MB",
             0, ConsoleColor.DarkGreen),
            ("[···] LOADING TERRAIN DATABASE",
             "[OK]  TERRAIN LOADED  ·  50×50 GRID  ·  390 MINERAL DEPOSITS",
             600, ConsoleColor.DarkGreen),
            ("[···] CALIBRATING INERTIAL MEASUREMENT UNIT",
             "[OK]  IMU READY  ·  DRIFT: 0.003°/h",
             400, ConsoleColor.DarkGreen),
            ("[···] ESTABLISHING UPLINK TO MARS ORBIT SATELLITE",
             "[WRN] SIGNAL DEGRADED  ·  REROUTING VIA BACKUP RELAY",
             750, ConsoleColor.DarkYellow),
            (null,
             "[OK]  BACKUP RELAY ONLINE  ·  LATENCY: 14m 32s",
             0, ConsoleColor.DarkGreen),
            ("[···] INITIALIZING Q-LEARNING AGENT",
             "[OK]  Q-AGENT  ·  1,200 STATE VECTORS  ·  λ=0.7",
             500, ConsoleColor.DarkGreen),
            (null,
             "[OK]  A* PATHFINDER  ·  8-DIRECTIONAL  ·  CHEBYSHEV HEURISTIC",
             0, ConsoleColor.DarkGreen),
            (null,
             "[OK]  ENERGY MANAGEMENT SYSTEM READY",
             0, ConsoleColor.DarkGreen),
            ("[···] RUNNING PREFLIGHT DIAGNOSTICS",
             "[OK]  ALL SYSTEMS NOMINAL",
             500, ConsoleColor.DarkGreen),
        };

        foreach (var (wk, dn, ms, dc) in entries)
        {
            if (wk != null)
            {
                // Show working state
                WA(r, 5, wk, ConsoleColor.DarkYellow);
                Thread.Sleep(ms);
                // Overwrite with final state, padding to erase leftover chars
                string padded = dn.PadRight(Math.Max(dn.Length, wk.Length + 5));
                WA(r, 5, padded, dc);
            }
            else
            {
                WA(r, 5, dn, dc);
                Thread.Sleep(75);
            }
            r++;
        }

        r++;
        WA(r++, 0, Center(new string('━', 60)), ConsoleColor.DarkRed);
        WA(r++, 0, Center("BOOT SEQUENCE COMPLETE  ·  UPLINK ESTABLISHED"), ConsoleColor.Red);

        // Blinking prompt — blinks until keypress
        bool vis     = true;
        var  sw      = System.Diagnostics.Stopwatch.StartNew();
        bool pressed = false;

        while (!pressed)
        {
            WA(r, 0, Center($"PRESS ANY KEY TO ACCESS MISSION TERMINAL  {(vis ? "▮" : " ")}"),
               vis ? ConsoleColor.Red : ConsoleColor.DarkRed);
            vis = !vis;
            Thread.Sleep(430);
            if (System.Console.KeyAvailable)
            {
                System.Console.ReadKey(intercept: true);
                pressed = true;
            }
        }
    }


    // Main menu


    // Menu items: label (padded for uniform width), description shown below panel
    private static readonly (string lbl, string desc)[] Items = new (string lbl, string desc)[]
    {
        ("  INITIATE MISSION SEQUENCE  ",
         "TRAIN AGENT  ·  DEPLOY BEST POLICY  ·  BEGIN EXPEDITION"),
        ("  SYSTEM DIAGNOSTICS         ",
         "VIEW SAVED MODEL  ·  Q-TABLE STATS  ·  MISSION HISTORY"),
        ("  CUT TRANSMISSION           ",
         "TERMINATE UPLINK  ·  POWER DOWN TERMINAL"),
    };

    // Status messages cycling in the bottom bar
    private static readonly string[] StatusCycle = new string[]
    {
        "LINK UNSTABLE", "SYNCHRONIZING..", "LINK UNSTABLE",
        "NOMINAL  ·  ALL SYSTEMS GO", "LINK UNSTABLE",
    };

    // Shared mutable state between input thread and animation thread.
    // Fields are volatile — animation thread reads, input thread writes.
    private sealed class MenuState
    {
        public volatile int  Sel         = 0;
        public volatile bool NeedButtons = true;
    }

    public static MainMenuResult RunMainMenu()
    {
        System.Console.CursorVisible = false;
        System.Console.BackgroundColor = ConsoleColor.Black;
        System.Console.Clear();

        var state = new MenuState();
        using var cts = new CancellationTokenSource();

        // Animation runs entirely on a background thread.
        // This thread NEVER calls ReadKey — it only writes to console.
        var anim = Task.Run(() => AnimLoop(state, cts.Token));

        // Input loop on main thread — ONLY calls ReadKey, never writes to console.
        MainMenuResult? result = null;
        while (result == null)
        {
            var key = System.Console.ReadKey(intercept: true).Key;
            switch (key)
            {
                case ConsoleKey.UpArrow:
                case ConsoleKey.W:
                    state.Sel         = (state.Sel - 1 + Items.Length) % Items.Length;
                    state.NeedButtons = true;
                    break;

                case ConsoleKey.DownArrow:
                case ConsoleKey.S:
                    state.Sel         = (state.Sel + 1) % Items.Length;
                    state.NeedButtons = true;
                    break;

                case ConsoleKey.Enter:
                case ConsoleKey.Spacebar:
                    result = (MainMenuResult)state.Sel;
                    break;

                case ConsoleKey.Escape:
                    result = MainMenuResult.Exit;
                    break;
            }
        }

        cts.Cancel();
        try { anim.Wait(700); } catch {}

        System.Console.CursorVisible = true;
        return result.Value;
    }


    private static void AnimLoop(MenuState state, CancellationToken ct)
    {
        const int FrameMs = 80;

        int   marsOff  = 0;
        float breath   = 0f;
        int   sigBars  = 6;
        int   statIdx  = 0;
        int   statTick = 0;

        DrawStatic(state);

        while (!ct.IsCancellationRequested)
        {
            var t0 = DateTime.Now;

            DrawPlanet(marsOff);
            marsOff = (marsOff + 1) % 48;

            breath += 0.09f;
            if (breath >= MathF.Tau) breath -= MathF.Tau;
            DrawOffline(breath);

            if (_rng.Next(9) == 0)
                sigBars = Math.Clamp(sigBars + _rng.Next(-1, 2), 3, 8);
            DrawStatusBar(sigBars, StatusCycle[statIdx]);

            statTick++;
            if (statTick >= 50)
            {
                statTick = 0;
                statIdx  = (statIdx + 1) % StatusCycle.Length;
            }


            if (_rng.Next(55) == 0)
                DrawGlitch();

            if (state.NeedButtons)
            {
                state.NeedButtons = false;
                DrawMenuPanel(state.Sel);
            }

            int elapsed = (int)(DateTime.Now - t0).TotalMilliseconds;
            int sleep   = Math.Max(1, FrameMs - elapsed);
            try { Task.Delay(sleep, ct).Wait(ct); } catch { break; }
        }
    }


    private static void DrawStatic(MenuState state)
    {

        WA(R_Sep1, 0, new string('━', W), ConsoleColor.DarkRed);

 
        WA(R_Header, 0,
           Center("·· MARS MISSION TERMINAL  ·  SYS-7  v2.4 ··"),
           ConsoleColor.DarkRed);

        foreach (var (row, col, g) in Stars)
            WA(row, col, g.ToString(), ConsoleColor.DarkGray);

        WA(R_Title,     0, Center("──  ·  M A R S   R O V E R  ·  ──"),  ConsoleColor.Red);
        WA(R_Title + 1, 0, Center("Q-Learning Pathfinding Agent"),        ConsoleColor.DarkRed);
        WA(R_Title + 2, 0, Center("MISSION CONTROL  ·  SYS-7  v2.4"),     ConsoleColor.DarkRed);


        WA(R_Sep2, 0, new string('━', W), ConsoleColor.DarkRed);


        WA(R_Hint, 0,
           Center("↑/↓  NAVIGATE    ·    ENTER  CONFIRM    ·    ESC  ABORT"),
           ConsoleColor.DarkGray);


        DrawMenuPanel(state.Sel);
    }



    private static void DrawPlanet(int offset)
    {
        for (int py = 0; py < MarsH; py++)
        {
            int screenRow = R_Mars + py;
            (int L, int R) = Mask[py];
            int mid  = (L + R) / 2;
            int half = Math.Max(1, (R - L) / 2);

            System.Console.SetCursorPosition(MarsCol, screenRow);

            for (int px = 0; px < MarsW; px++)
            {
                if (px < L || px > R)
                {
                    System.Console.Write(' ');
                    continue;
                }

                string tex    = Tex[py];
                char   c      = tex[(px + offset) % tex.Length];

                float  dist   = Math.Abs(px - mid) / (float)half; 
                bool   bright = dist < 0.55f;

                ConsoleColor col = c switch
                {
                    '▓' => bright ? ConsoleColor.Red     : ConsoleColor.DarkRed,
                    '▒' => bright ? ConsoleColor.DarkRed : ConsoleColor.DarkRed,
                    _   => ConsoleColor.DarkRed,
                };

                System.Console.ForegroundColor = col;
                System.Console.Write(c);
            }
        }
        System.Console.ResetColor();
    }



    private static void DrawOffline(float phase)
    {

        float b = (MathF.Sin(phase) + 1f) / 2f;

        (string dot, ConsoleColor col) = b switch
        {
            < 0.15f => ("○", ConsoleColor.DarkGray),
            < 0.40f => ("◉", ConsoleColor.DarkRed),
            < 0.75f => ("◉", ConsoleColor.Red),
            _       => ("●", ConsoleColor.Red),
        };

        WA(R_Header, 2, $"{dot} OFFLINE", col);
    }



    private static void DrawMenuPanel(int sel)
    {
        const int PW  = 54;
        int       col = (W - PW) / 2;

        string top = "╔" + new string('═', PW - 2) + "╗";
        string div = "╠" + new string('═', PW - 2) + "╣";
        string bot = "╚" + new string('═', PW - 2) + "╝";

        WA(R_Menu, col, top, ConsoleColor.DarkRed);

        for (int i = 0; i < Items.Length; i++)
        {
            int    itemRow = R_Menu + 1 + i * 2;
            bool   active  = i == sel;
            string lbl     = Items[i].lbl;

            WA(itemRow, col, "║", ConsoleColor.DarkRed);

            if (active)
            {
                WA(itemRow, col + 1, " ►", ConsoleColor.Red);
                WA(itemRow, col + 3, lbl.PadRight(PW - 4), ConsoleColor.Yellow);
            }
            else
            {
                WA(itemRow, col + 1, ("   " + lbl.TrimStart()).PadRight(PW - 2), ConsoleColor.DarkRed);
            }

            WA(itemRow, col + PW - 1, "║", ConsoleColor.DarkRed);

            if (i < Items.Length - 1)
                WA(R_Menu + 2 + i * 2, col, div, ConsoleColor.DarkRed);
        }

        WA(R_Menu + 1 + (Items.Length - 1) * 2 + 1, col, bot, ConsoleColor.DarkRed);

        string desc = Items[sel].desc;
        WA(R_Desc, 0, Center(desc).PadRight(W), ConsoleColor.DarkGray);
    }


    private static void DrawStatusBar(int bars, string statusMsg)
    {
        string filled = new string('█', bars);
        string empty  = new string('░', 8 - bars);

        ConsoleColor sigCol  = bars >= 6 ? ConsoleColor.DarkGreen
                             : bars >= 4 ? ConsoleColor.DarkYellow
                                         : ConsoleColor.DarkRed;
        ConsoleColor statCol = statusMsg.Contains("NOMINAL") ? ConsoleColor.DarkGreen
                                                              : ConsoleColor.DarkYellow;

        int latency = 32 + bars % 3;

        WA(R_Status, 2,  $"SIGNAL: {filled}{empty}",    sigCol);
        WA(R_Status, 26, $"STATUS: {statusMsg,-25}",     statCol);
        WA(R_Status, 62, $"LATENCY: 14m {latency}s",     ConsoleColor.DarkGray);
    }

    private static readonly char[] GlitchSet =
        "▓▒░█▄▀■□◆○●◉①②③0xFFDE4B8A".ToCharArray();

    private static void DrawGlitch()
    {
        int py  = _rng.Next(MarsH);
        int row = R_Mars + py;
        (int L, int R) = Mask[py];

        int startPx = _rng.Next(Math.Max(0, L - 4), Math.Min(W - 1, R + 4));
        int len     = _rng.Next(3, 9);

        try
        {
            System.Console.SetCursorPosition(MarsCol + startPx, row);
            System.Console.ForegroundColor = ConsoleColor.Red;
            for (int i = 0; i < len && MarsCol + startPx + i < W; i++)
                System.Console.Write(GlitchSet[_rng.Next(GlitchSet.Length)]);
            System.Console.ResetColor();
        }
        catch {}
    }

    // Helpers

    /// <summary>Write text at absolute (col, row) position, clamped to window.</summary>
    private static void WA(int row, int col, string text, ConsoleColor color)
    {
        try
        {
            if (row < 0 || row >= System.Console.WindowHeight) return;
            if (col < 0) col = 0;
            int maxLen = Math.Max(0, System.Console.WindowWidth - col);
            if (maxLen <= 0) return;
            string safe = text.Length > maxLen ? text[..maxLen] : text;
            System.Console.SetCursorPosition(col, row);
            System.Console.ForegroundColor = color;
            System.Console.Write(safe);
            System.Console.ResetColor();
        }
        catch {}
    }

    private static string Center(string text)
    {
        int pad = Math.Max(0, (W - text.Length) / 2);
        return new string(' ', pad) + text;
    }
}
