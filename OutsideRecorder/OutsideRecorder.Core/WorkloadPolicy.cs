namespace OutsideRecorderCore;

/// <summary>Shared, conservative permission for optional work; CPU percentages refer to the entire machine.</summary>
public sealed class WorkloadPolicy
{
    readonly object sync = new();
    readonly double budget;
    /// <summary>Menu/closed-game mode permits more recorder CPU, retaining memory/system-pressure and quiet gates.</summary>
    public volatile bool GameIdle;
    double EffectiveBudget => GameIdle ? Math.Max(10, budget) : budget;
    public double RecorderCpuBudgetPercent => EffectiveBudget;
    bool quiet, pressured;
    string quietReason = "Waiting for game to settle", pressureReason = "Computer is busy";
    DateTimeOffset? highSince, lowSince, lastAt;
    Pressure causes;
    [Flags] enum Pressure { None = 0, SystemCpu = 1, RecorderCpu = 2, Memory = 4 }

    /// <summary>The default two-percent budget slows optional work after sustained recorder CPU use.</summary>
    public WorkloadPolicy(double recorderCpuBudgetPercent = 2)
    {
        if (!double.IsFinite(recorderCpuBudgetPercent) || recorderCpuBudgetPercent <= 0 || recorderCpuBudgetPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(recorderCpuBudgetPercent));
        budget = recorderCpuBudgetPercent;
    }

    /// <summary>New parsing, compression and uploads wait while a quiet period, a busy computer or low memory holds them.
    /// The recorder's own CPU never holds them: it runs below normal priority, so Civ always comes first, and holding cut
    /// each turn's save upload halfway, starting it over and over (a livestream fell 9 turns behind, 2026-11-02).</summary>
    public bool CanStartHeavyWork { get { lock (sync) return !quiet && !(pressured && ComputerCause); } }
    /// <summary>Independent of capture mode: a permission never enables a disabled memory reader. Low memory alone does not
    /// stop reading Civ (the reads hold ~75 MB; on a full PC the brake paused a whole game, 2026-11-01): only a busy
    /// computer does. The recorder over its own budget reads more slowly instead (<see cref="MinimumSampleMs"/>).</summary>
    public bool CanReadMemory { get { lock (sync) return !quiet && !(pressured && causes.HasFlag(Pressure.SystemCpu)); } }
    /// <summary>On suspected CPU pressure slow capture immediately, before the sustained pause threshold.</summary>
    public int MinimumSampleMs { get { lock (sync) return quiet || pressured && causes.HasFlag(Pressure.SystemCpu) ? 1000 : (highSince is not null || pressured) && CpuCause ? 500 : 50; } }
    bool CpuCause => (causes & ~Pressure.Memory) != 0;
    bool ComputerCause => (causes & ~Pressure.RecorderCpu) != 0;
    /// <summary>Plain-language explanation for the current restriction.</summary>
    public string Reason { get { lock (sync) return quiet ? quietReason : pressured ? pressureReason : highSince is not null ? "Reducing work while the computer is busy" : "Ready"; } }

    /// <summary>Explicit quiet periods take precedence; clearing one cannot clear an independent pressure pause.</summary>
    public void SetQuiet(bool quiet, string reason = "Waiting for game to settle")
    {
        lock (sync) { this.quiet = quiet; quietReason = reason; }
    }

    /// <summary>Pause after four seconds of pressure; require eight seconds of observed recovery before resuming.</summary>
    public void Observe(ResourceSample sample)
    {
        lock (sync)
        {
            if (lastAt is DateTimeOffset at && sample.At <= at) return;
            // Missing a monitoring interval does not count as continuously observed high or low load.
            if (lastAt is DateTimeOffset last && sample.At - last > TimeSpan.FromSeconds(6)) { highSince = null; lowSince = null; }
            lastAt = sample.At;
            Pressure high = Pressure.None;
            if (Valid(sample.SystemCpuPercent) is double cpu && cpu >= 85) high |= Pressure.SystemCpu;
            if (Valid(sample.RecorderCpuPercent) is double own && own >= EffectiveBudget) high |= Pressure.RecorderCpu;
            long enterMemory = Math.Max(256L << 20, (long)((sample.TotalMemoryBytes ?? 0) * .05));
            long leaveMemory = Math.Max(512L << 20, (long)((sample.TotalMemoryBytes ?? 0) * .08));
            if (sample.AvailableMemoryBytes is >= 0 and var available && available < enterMemory) high |= Pressure.Memory;
            if (high != Pressure.None)
            {
                highSince ??= sample.At;
                lowSince = null;
                causes |= high;
                if (pressured || sample.At - highSince >= TimeSpan.FromSeconds(4))
                {
                    pressured = true;
                    pressureReason = causes.HasFlag(Pressure.Memory) ? "Waiting for more available memory" : causes.HasFlag(Pressure.SystemCpu) ? "Waiting for the computer to be less busy" : "Reading Civ more slowly to keep recorder CPU use within its budget";
                }
                return;
            }
            highSince = null;
            if (!pressured) { causes = Pressure.None; return; }
            bool recovered = Healthy(sample.SystemCpuPercent, 65, causes.HasFlag(Pressure.SystemCpu)) &&
                Healthy(sample.RecorderCpuPercent, EffectiveBudget * .75, causes.HasFlag(Pressure.RecorderCpu)) &&
                (sample.AvailableMemoryBytes is long memory ? memory >= leaveMemory : !causes.HasFlag(Pressure.Memory));
            if (!recovered) { lowSince = null; return; }
            lowSince ??= sample.At;
            if (sample.At - lowSince >= TimeSpan.FromSeconds(8)) { pressured = false; lowSince = null; causes = Pressure.None; }
        }
    }

    static double? Valid(double? value) => value is double v && double.IsFinite(v) && v is >= 0 and <= 100 ? v : null;
    static bool Healthy(double? value, double threshold, bool required) => Valid(value) is double v ? v <= threshold : !required;
}
