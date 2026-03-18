namespace MarsRover.Core.Algorithm;

/// <summary>Live training progress payload used by UI/Console callbacks.</summary>
public record TrainingProgress(
    int              Episode,
    int              TotalEps,
    double           Epsilon,
    double           LastReward,
    int              BestMinerals,
    int              StatesKnown,
    int              BufferSize    = 0,
    double           LastBattery   = 100.0,
    int              LastMinerals  = 0,
    EpisodeSnapshot? Snapshot      = null)
{
    public double PercentDone => (double)Episode / TotalEps * 100.0;
}

/// <summary>Final aggregate result returned by training.</summary>
public record TrainingResult(
    int          Episodes,
    int          BestMinerals,
    List<double> RewardHistory,
    int          StatesLearned);

public enum MissionEndMode
{
    StopOnFirstReturn,
    ContinueUntilDeadline
}

public enum TrainingProfile
{
    Baseline,
    Balanced,
    Curriculum,
    RobustSweep
}

public record CurriculumOptions(
    bool Enabled = false,
    int RandomMapCount = 0,
    int RandomMapSeedStart = 1_000,
    int PretrainEpisodes = 0,
    int FineTuneEpisodes = 0);

public record SeedSweepOptions(
    bool Enabled = false,
    int SeedCount = 1,
    int EvalEpisodesPerSeed = 1,
    bool IncludeOfficialMapInValidation = true,
    int[]? ValidationMapSeeds = null);

public record ReplayDiversityOptions(
    bool Enabled = false,
    double StratifiedFraction = 0.4);

public record TrainingOptions(
    bool UseAdaptiveEpsilon = false,
    double AdaptiveEpsilonMax = 1.0,
    double Lambda = 0.7,
    double TraceThreshold = 1e-4,
    ReplayDiversityOptions? ReplayDiversity = null,
    CurriculumOptions? Curriculum = null,
    SeedSweepOptions? SeedSweep = null,
    MissionEndMode MissionEndMode = MissionEndMode.ContinueUntilDeadline,
    string ProfileName = "default",
    int EpisodeSeedOffset = 0);

public record TrainingOptionOverrides(
    bool? UseAdaptiveEpsilon = null,
    double? AdaptiveEpsilonMax = null,
    double? Lambda = null,
    double? TraceThreshold = null,
    bool? ReplayDiversityEnabled = null,
    double? ReplayStratifiedFraction = null,
    MissionEndMode? MissionEndMode = null,
    bool? CurriculumEnabled = null,
    int? CurriculumRandomMapCount = null,
    int? CurriculumRandomMapSeedStart = null,
    int? CurriculumPretrainEpisodes = null,
    int? CurriculumFineTuneEpisodes = null,
    bool? SeedSweepEnabled = null,
    int? SeedSweepCount = null,
    int? SeedSweepEvalEpisodes = null);

public static class TrainingProfileFactory
{
    public static readonly TrainingProfile DefaultProfile = TrainingProfile.Balanced;

    public static TrainingOptions CreateOptions(TrainingProfile profile)
        => profile switch
        {
            TrainingProfile.Baseline => new TrainingOptions(
                UseAdaptiveEpsilon: false,
                AdaptiveEpsilonMax: 1.0,
                Lambda: 0.7,
                TraceThreshold: 1e-4,
                ReplayDiversity: new ReplayDiversityOptions(false, 0.4),
                Curriculum: new CurriculumOptions(false),
                SeedSweep: new SeedSweepOptions(false),
                MissionEndMode: MissionEndMode.StopOnFirstReturn,
                ProfileName: TrainingProfile.Baseline.ToString()),
            TrainingProfile.Balanced => new TrainingOptions(
                UseAdaptiveEpsilon: true,
                AdaptiveEpsilonMax: 1.0,
                Lambda: 0.7,
                TraceThreshold: 1e-4,
                ReplayDiversity: new ReplayDiversityOptions(true, 0.4),
                Curriculum: new CurriculumOptions(false),
                SeedSweep: new SeedSweepOptions(false),
                MissionEndMode: MissionEndMode.ContinueUntilDeadline,
                ProfileName: TrainingProfile.Balanced.ToString()),
            TrainingProfile.Curriculum => new TrainingOptions(
                UseAdaptiveEpsilon: true,
                AdaptiveEpsilonMax: 1.0,
                Lambda: 0.7,
                TraceThreshold: 1e-4,
                ReplayDiversity: new ReplayDiversityOptions(true, 0.4),
                Curriculum: new CurriculumOptions(
                    Enabled: true,
                    RandomMapCount: 8,
                    RandomMapSeedStart: 10_000,
                    PretrainEpisodes: 120,
                    FineTuneEpisodes: 400),
                SeedSweep: new SeedSweepOptions(false),
                MissionEndMode: MissionEndMode.ContinueUntilDeadline,
                ProfileName: TrainingProfile.Curriculum.ToString()),
            TrainingProfile.RobustSweep => new TrainingOptions(
                UseAdaptiveEpsilon: true,
                AdaptiveEpsilonMax: 1.0,
                Lambda: 0.7,
                TraceThreshold: 1e-4,
                ReplayDiversity: new ReplayDiversityOptions(true, 0.4),
                Curriculum: new CurriculumOptions(false),
                SeedSweep: new SeedSweepOptions(
                    Enabled: true,
                    SeedCount: 5,
                    EvalEpisodesPerSeed: 3,
                    IncludeOfficialMapInValidation: true,
                    ValidationMapSeeds: [20_001, 20_101, 20_201]),
                MissionEndMode: MissionEndMode.ContinueUntilDeadline,
                ProfileName: TrainingProfile.RobustSweep.ToString()),
            _ => CreateOptions(DefaultProfile)
        };

    public static TrainingOptions ApplyOverrides(
        TrainingOptions baseOptions,
        TrainingOptionOverrides overrides)
    {
        var replay = baseOptions.ReplayDiversity ?? new ReplayDiversityOptions();
        replay = replay with
        {
            Enabled = overrides.ReplayDiversityEnabled ?? replay.Enabled,
            StratifiedFraction = overrides.ReplayStratifiedFraction ?? replay.StratifiedFraction
        };

        var curriculum = baseOptions.Curriculum ?? new CurriculumOptions();
        curriculum = curriculum with
        {
            Enabled = overrides.CurriculumEnabled ?? curriculum.Enabled,
            RandomMapCount = overrides.CurriculumRandomMapCount ?? curriculum.RandomMapCount,
            RandomMapSeedStart = overrides.CurriculumRandomMapSeedStart ?? curriculum.RandomMapSeedStart,
            PretrainEpisodes = overrides.CurriculumPretrainEpisodes ?? curriculum.PretrainEpisodes,
            FineTuneEpisodes = overrides.CurriculumFineTuneEpisodes ?? curriculum.FineTuneEpisodes
        };

        var seedSweep = baseOptions.SeedSweep ?? new SeedSweepOptions();
        seedSweep = seedSweep with
        {
            Enabled = overrides.SeedSweepEnabled ?? seedSweep.Enabled,
            SeedCount = overrides.SeedSweepCount ?? seedSweep.SeedCount,
            EvalEpisodesPerSeed = overrides.SeedSweepEvalEpisodes ?? seedSweep.EvalEpisodesPerSeed
        };

        return baseOptions with
        {
            UseAdaptiveEpsilon = overrides.UseAdaptiveEpsilon ?? baseOptions.UseAdaptiveEpsilon,
            AdaptiveEpsilonMax = overrides.AdaptiveEpsilonMax ?? baseOptions.AdaptiveEpsilonMax,
            Lambda = overrides.Lambda ?? baseOptions.Lambda,
            TraceThreshold = overrides.TraceThreshold ?? baseOptions.TraceThreshold,
            ReplayDiversity = replay,
            Curriculum = curriculum,
            SeedSweep = seedSweep,
            MissionEndMode = overrides.MissionEndMode ?? baseOptions.MissionEndMode
        };
    }

    public static bool TryParseProfile(string? value, out TrainingProfile profile)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            profile = DefaultProfile;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out profile);
    }
}
