using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MarsRover.Core.Algorithm;

public readonly record struct BenchmarkRunResult(
    int Seed,
    int Minerals,
    bool ReturnedHome,
    int TicksUsed,
    double BatteryAtEnd);

public record BenchmarkSummary(
    string MapPath,
    int Hours,
    int Episodes,
    string ModelPath,
    int[] Seeds,
    int Runs,
    double MineralsMean,
    double MineralsMedian,
    double MineralsStd,
    double ReturnHomeRate,
    double TicksUsedMedian,
    double BatteryAtEndMedian,
    IReadOnlyList<BenchmarkRunResult> PerRun);

public static class Benchmarking
{
    public static BenchmarkSummary BuildSummary(
        string mapPath,
        int hours,
        int episodes,
        string modelPath,
        IEnumerable<int> seeds,
        IEnumerable<BenchmarkRunResult> runs)
    {
        var seedArray = seeds.ToArray();
        var sortedRuns = runs
            .OrderBy(r => r.Seed)
            .ToArray();

        var minerals = sortedRuns.Select(r => (double)r.Minerals).ToArray();
        var ticks = sortedRuns.Select(r => (double)r.TicksUsed).ToArray();
        var batteries = sortedRuns.Select(r => r.BatteryAtEnd).ToArray();
        int returnedCount = sortedRuns.Count(r => r.ReturnedHome);
        int n = sortedRuns.Length;

        return new BenchmarkSummary(
            MapPath: mapPath,
            Hours: hours,
            Episodes: episodes,
            ModelPath: modelPath,
            Seeds: seedArray,
            Runs: n,
            MineralsMean: Mean(minerals),
            MineralsMedian: Median(minerals),
            MineralsStd: StdDevPopulation(minerals),
            ReturnHomeRate: n == 0 ? 0.0 : returnedCount / (double)n,
            TicksUsedMedian: Median(ticks),
            BatteryAtEndMedian: Median(batteries),
            PerRun: sortedRuns);
    }

    public static (string jsonPath, string csvPath) SaveSummary(
        BenchmarkSummary summary,
        string outputBaseName,
        string resultsDir = "results")
    {
        Directory.CreateDirectory(resultsDir);
        string jsonPath = Path.Combine(resultsDir, outputBaseName + ".json");
        string csvPath = Path.Combine(resultsDir, outputBaseName + ".csv");

        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        File.WriteAllText(jsonPath, json);

        var sb = new StringBuilder();
        sb.AppendLine("metric,value");
        sb.AppendLine($"mapPath,{Escape(summary.MapPath)}");
        sb.AppendLine($"hours,{summary.Hours}");
        sb.AppendLine($"episodes,{summary.Episodes}");
        sb.AppendLine($"modelPath,{Escape(summary.ModelPath)}");
        sb.AppendLine($"runs,{summary.Runs}");
        sb.AppendLine($"mineralsMean,{Format(summary.MineralsMean)}");
        sb.AppendLine($"mineralsMedian,{Format(summary.MineralsMedian)}");
        sb.AppendLine($"mineralsStd,{Format(summary.MineralsStd)}");
        sb.AppendLine($"returnHomeRate,{Format(summary.ReturnHomeRate)}");
        sb.AppendLine($"ticksUsedMedian,{Format(summary.TicksUsedMedian)}");
        sb.AppendLine($"batteryAtEndMedian,{Format(summary.BatteryAtEndMedian)}");
        sb.AppendLine();
        sb.AppendLine("seed,minerals,returnedHome,ticksUsed,batteryAtEnd");
        foreach (var run in summary.PerRun)
        {
            sb.Append(run.Seed).Append(',')
              .Append(run.Minerals).Append(',')
              .Append(run.ReturnedHome ? "1" : "0").Append(',')
              .Append(run.TicksUsed).Append(',')
              .AppendLine(Format(run.BatteryAtEnd));
        }

        File.WriteAllText(csvPath, sb.ToString());
        return (jsonPath, csvPath);
    }

    private static double Mean(IReadOnlyList<double> xs)
        => xs.Count == 0 ? 0.0 : xs.Average();

    private static double Median(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0.0;
        var sorted = xs.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        if (sorted.Length % 2 == 1) return sorted[mid];
        return (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static double StdDevPopulation(IReadOnlyList<double> xs)
    {
        if (xs.Count == 0) return 0.0;
        double mean = Mean(xs);
        double sumSq = 0.0;
        foreach (double x in xs)
        {
            double d = x - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / xs.Count);
    }

    private static string Format(double value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Escape(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n'))
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
