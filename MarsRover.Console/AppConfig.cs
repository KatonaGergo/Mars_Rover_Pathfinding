using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarsRover.Console;

/// <summary>
/// All runtime parameters for the Mars Rover agent, in one place.
///
/// LOAD ORDER (later overrides earlier):
///   1. Built-in defaults  (hardcoded below)
///   2. config.json        (written next to the binary on first run)
///   3. CLI flags          (--map, --hours, --episodes, --model)
///
/// This means you can set permanent defaults in config.json and still
/// override any single value on the command line without editing the file.
/// </summary>
public class AppConfig
{
    // ── Defaults ──────────────────────────────────────────────────────────────
    [JsonPropertyName("mapPath")]
    public string MapPath    { get; set; } = "mars_map_50x50.csv";

    [JsonPropertyName("hours")]
    public int    Hours      { get; set; } = 24;

    [JsonPropertyName("episodes")]
    public int    Episodes   { get; set; } = 500;

    [JsonPropertyName("modelPath")]
    public string ModelPath  { get; set; } = "model";

    // ── Load ──────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented        = true,
        AllowTrailingCommas  = true,
        ReadCommentHandling  = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads config.json from the working directory.
    /// If the file does not exist, writes a default one and returns defaults.
    /// Any field missing from the file keeps its default value.
    /// </summary>
    public static AppConfig Load(string configPath = "config.json")
    {
        if (!File.Exists(configPath))
        {
            var defaults = new AppConfig();
            defaults.Save(configPath);
            return defaults;
        }

        try
        {
            var json   = File.ReadAllText(configPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, _opts);
            return loaded ?? new AppConfig();
        }
        catch (JsonException ex)
        {
            System.Console.ForegroundColor = ConsoleColor.Yellow;
            System.Console.WriteLine($"  WARNING: config.json is malformed ({ex.Message})");
            System.Console.WriteLine("  Using built-in defaults. Fix or delete config.json to suppress this.");
            System.Console.ResetColor();
            return new AppConfig();
        }
    }

    /// <summary>Writes current values to config.json.</summary>
    public void Save(string configPath = "config.json")
    {
        File.WriteAllText(configPath, JsonSerializer.Serialize(this, _opts));
    }

    // ── CLI override ──────────────────────────────────────────────────────────

    /// <summary>
    /// Applies CLI flags on top of whatever was loaded from config.json.
    /// Only flags that are actually present in args override the config.
    /// Validates types and ranges; prints clear error + returns false on failure.
    /// </summary>
    public bool ApplyArgs(string[] args, out string errorMessage)
    {
        errorMessage = string.Empty;

        // --map
        var mapArg = GetArg(args, "--map");
        if (mapArg != null) MapPath = mapArg;

        // --model
        var modelArg = GetArg(args, "--model");
        if (modelArg != null) ModelPath = modelArg;

        // --hours
        var hoursArg = GetArg(args, "--hours");
        if (hoursArg != null)
        {
            if (!int.TryParse(hoursArg, out int h) || h < 1 || h > 240)
            {
                errorMessage = $"--hours must be an integer between 1 and 240 (got '{hoursArg}')";
                return false;
            }
            Hours = h;
        }

        // --episodes
        var epsArg = GetArg(args, "--episodes");
        if (epsArg != null)
        {
            if (!int.TryParse(epsArg, out int e) || e < 1 || e > 100_000)
            {
                errorMessage = $"--episodes must be an integer between 1 and 100000 (got '{epsArg}')";
                return false;
            }
            Episodes = e;
        }

        return true;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks all values are within legal ranges.
    /// Returns false and sets errorMessage if anything is wrong.
    /// </summary>
    public bool Validate(out string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(MapPath))
        { errorMessage = "mapPath cannot be empty."; return false; }

        if (!File.Exists(MapPath))
        { errorMessage = $"Map file not found: {MapPath}"; return false; }

        if (Hours < 1 || Hours > 240)
        { errorMessage = $"hours must be between 1 and 240 (got {Hours})."; return false; }

        if (Episodes < 1 || Episodes > 100_000)
        { errorMessage = $"episodes must be between 1 and 100000 (got {Episodes})."; return false; }

        if (string.IsNullOrWhiteSpace(ModelPath))
        { errorMessage = "modelPath cannot be empty."; return false; }

        errorMessage = string.Empty;
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetArg(string[] args, string flag)
    {
        int i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
