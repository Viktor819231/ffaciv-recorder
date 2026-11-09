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
    long? highSince, lowSince, lastMs;   // milliseconds of SampleMs()
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
    /// <summary>Independent of capture mode: a permission never enables a disabled memory reader. Only a quiet period (the
    /// turn transition) stops reading Civ. Pressure slows it instead (<see cref="MinimumSampleMs"/>: one look a second on a
    /// busy computer): the recorder runs below normal priority, so Windows already favours Civ. Low memory paused a whole
    /// game (2026-11-01); a busy computer stopped reading while Civ itself kept three cores busy, so no recording began and
    /// no save was copied (2026-11-09).</summary>
    public bool CanReadMemory { get { lock (sync) return !quiet; } }
    /// <summary>A save copy is one small file per turn at below-normal priority: only the turn-transition quiet holds it. A busy
    /// computer must not hold it for long: Civ deletes its oldest autosaves beyond the kept count, so a long game on a fully
    /// loaded PC could lose its early turns before they were kept (2026-11-09).</summary>
    public bool CanCopySaves { get { lock (sync) return !quiet; } }
    /// <summary>On suspected CPU pressure slow capture immediately, before the sustained pause threshold; a sustained busy
    /// computer reads Civ once a second.</summary>
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

    /// <summary>A sample's time for intervals: its monotonic tick when it has one, else its wall-clock time.</summary>
    static long SampleMs(ResourceSample s) => s.TickMs ?? s.At.ToUnixTimeMilliseconds();

    /// <summary>Pause after four seconds of pressure; require eight seconds of observed recovery before resuming. A sample
    /// not newer than the last one (the monitor's cached sample) is ignored.</summary>
    public void Observe(ResourceSample sample)
    {
        lock (sync)
        {
            long now = SampleMs(sample);
            if (lastMs is long at && now <= at) return;
            // Missing a monitoring interval does not count as continuously observed high or low load.
            if (lastMs is long last && now - last > 6000) { highSince = null; lowSince = null; }
            lastMs = now;
            Pressure high = Pressure.None;
            if (Valid(sample.SystemCpuPercent) is double cpu && cpu >= 85) high |= Pressure.SystemCpu;
            if (Valid(sample.RecorderCpuPercent) is double own && own >= EffectiveBudget) high |= Pressure.RecorderCpu;
            long enterMemory = Math.Max(256L << 20, (long)((sample.TotalMemoryBytes ?? 0) * .05));
            long leaveMemory = Math.Max(512L << 20, (long)((sample.TotalMemoryBytes ?? 0) * .08));
            if (sample.AvailableMemoryBytes is >= 0 and var available && available < enterMemory) high |= Pressure.Memory;
            if (high != Pressure.None)
            {
                highSince ??= now;
                lowSince = null;
                causes |= high;
                if (pressured || now - highSince >= 4000)
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
            lowSince ??= now;
            if (now - lowSince >= 8000) { pressured = false; lowSince = null; causes = Pressure.None; }
        }
    }

    static double? Valid(double? value) => value is double v && double.IsFinite(v) && v is >= 0 and <= 100 ? v : null;
    static bool Healthy(double? value, double threshold, bool required) => Valid(value) is double v ? v <= threshold : !required;
}
