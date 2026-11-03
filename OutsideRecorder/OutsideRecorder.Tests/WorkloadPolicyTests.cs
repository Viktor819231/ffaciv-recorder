namespace OutsideRecorderTests;

public sealed class WorkloadPolicyTests
{
    [Fact]
    public void IdleModeAllowsMoreOwnCpuButRetainsPressureAndGameplayLimits()
    {
        var idle=new WorkloadPolicy {GameIdle=true};
        foreach(int t in new[]{0,2,4})idle.Observe(Sample(t,own:6));
        Assert.True(idle.CanStartHeavyWork); Assert.Equal(50, idle.MinimumSampleMs);
        idle.GameIdle=false;
        foreach(int t in new[]{6,8,10})idle.Observe(Sample(t,own:6));
        Assert.True(idle.CanStartHeavyWork); Assert.Equal(500, idle.MinimumSampleMs);
        var busy=new WorkloadPolicy {GameIdle=true};
        foreach(int t in new[]{0,2,4})busy.Observe(Sample(t,system:95,own:6));
        Assert.False(busy.CanStartHeavyWork);
        var lowMemory=new WorkloadPolicy {GameIdle=true};
        foreach(int t in new[]{0,2,4})lowMemory.Observe(Sample(t,own:6,available:64L<<20));
        Assert.False(lowMemory.CanStartHeavyWork);
    }
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-29T12:00:00Z");
    static ResourceSample Sample(int seconds, double? system = 20, double? own = .1, long? available = 4L << 30) => new()
    {
        At = Start.AddSeconds(seconds), SystemCpuPercent = system, RecorderCpuPercent = own,
        AvailableMemoryBytes = available, TotalMemoryBytes = 16L << 30,
    };

    [Fact]
    public void ShortSpikeSlowsButDoesNotPauseAndSustainedLoadDoes()
    {
        var policy = new WorkloadPolicy();
        policy.Observe(Sample(0, 90)); Assert.True(policy.CanStartHeavyWork); Assert.Equal(500, policy.MinimumSampleMs);
        policy.Observe(Sample(2, 20)); Assert.True(policy.CanReadMemory); Assert.Equal(50, policy.MinimumSampleMs);
        policy.Observe(Sample(4, 90)); policy.Observe(Sample(6, 90)); policy.Observe(Sample(8, 90));
        Assert.False(policy.CanReadMemory); Assert.False(policy.CanStartHeavyWork); Assert.Equal(1000, policy.MinimumSampleMs);
    }

    [Fact]
    public void RecoveryRequiresEightSecondsAndMidrangeResetsRecovery()
    {
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(Sample(t, 90));
        foreach (int t in new[] { 6, 8, 10 }) policy.Observe(Sample(t, 20));
        policy.Observe(Sample(12, 70));
        foreach (int t in new[] { 14, 16, 18, 20 }) policy.Observe(Sample(t, 20));
        Assert.False(policy.CanStartHeavyWork);
        policy.Observe(Sample(22, 20)); Assert.True(policy.CanStartHeavyWork);
    }

    [Fact]
    public void UnknownCannotRecoverTheCounterThatCausedPause()
    {
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(Sample(t, 90));
        for (int t = 6; t <= 20; t += 2) policy.Observe(Sample(t, null));
        Assert.False(policy.CanReadMemory);
        var unsupported = new WorkloadPolicy(); unsupported.Observe(new() { At = Start });
        Assert.True(unsupported.CanStartHeavyWork);
    }

    [Fact]
    public void QuietAndResourcePausesAreIndependent()
    {
        var policy = new WorkloadPolicy(); policy.SetQuiet(true, "Save is still changing");
        Assert.False(policy.CanReadMemory); Assert.Equal("Save is still changing", policy.Reason);
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(Sample(t, 90));
        policy.SetQuiet(false); Assert.False(policy.CanStartHeavyWork);
        for (int t = 6; t <= 14; t += 2) policy.Observe(Sample(t));
        Assert.True(policy.CanReadMemory);
    }

    [Fact]
    public void MemoryAndRecorderCpuPressureUseTheirOwnRecoveryThresholds()
    {
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(Sample(t, own: 3, available: 100L << 20));
        Assert.False(policy.CanStartHeavyWork); Assert.Contains("memory", policy.Reason);
        for (int t = 6; t <= 16; t += 2) policy.Observe(Sample(t, own: .1, available: null));
        Assert.False(policy.CanStartHeavyWork);
        for (int t = 18; t <= 26; t += 2) policy.Observe(Sample(t));
        Assert.True(policy.CanStartHeavyWork);
    }

    [Fact]
    public void RecorderOverItsOwnBudgetReadsSlowerButNeverHoldsUploadsOrReading()
    {
        // 2026-11-02: each turn's save packing (6-12 % of a 12-thread PC for a few seconds) tripped the 2 % budget,
        // the brake cut the upload halfway and paused reading; the livestream fell 9 turns behind.
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4, 6 }) policy.Observe(Sample(t, own: 10));
        Assert.True(policy.CanStartHeavyWork); Assert.True(policy.CanReadMemory); Assert.Equal(500, policy.MinimumSampleMs);
        Assert.Contains("more slowly", policy.Reason);
        foreach (int t in new[] { 8, 10, 12 }) policy.Observe(Sample(t, 90, own: 10));
        Assert.False(policy.CanStartHeavyWork); Assert.False(policy.CanReadMemory); Assert.Equal(1000, policy.MinimumSampleMs);
        for (int t = 14; t <= 24; t += 2) policy.Observe(Sample(t));
        Assert.True(policy.CanStartHeavyWork); Assert.Equal(50, policy.MinimumSampleMs);
    }

    [Fact]
    public void LowMemoryAloneHoldsHeavyWorkButKeepsReadingCiv()
    {
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4, 6 }) policy.Observe(Sample(t, available: 100L << 20));
        Assert.False(policy.CanStartHeavyWork); Assert.True(policy.CanReadMemory); Assert.Equal(50, policy.MinimumSampleMs);
        foreach (int t in new[] { 8, 10, 12 }) policy.Observe(Sample(t, 90, available: 100L << 20));
        Assert.False(policy.CanReadMemory); Assert.Equal(1000, policy.MinimumSampleMs);
    }

    [Fact]
    public void MissedSamplesAndReorderedSamplesCannotCountAsContinuousRecovery()
    {
        var policy = new WorkloadPolicy();
        foreach (int t in new[] { 0, 2, 4 }) policy.Observe(Sample(t, 90));
        policy.Observe(Sample(6)); policy.Observe(Sample(30)); Assert.False(policy.CanReadMemory);
        policy.Observe(Sample(29)); Assert.False(policy.CanReadMemory);
        foreach (int t in new[] { 32, 34, 36, 38 }) policy.Observe(Sample(t));
        Assert.True(policy.CanReadMemory);
    }
}
