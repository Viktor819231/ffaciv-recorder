namespace OutsideRecorderTests;

public sealed class ResourceMonitorTests
{
    [Fact]
    public void FirstCpuSampleIsUnknownAndPollingIsThrottled()
    {
        long now = 0; int reads = 0;
        var monitor = new ResourceMonitor(() => { reads++; return new() { WorkingSetBytes = 123 }; }, () => now);
        var first = monitor.Sample();
        Assert.Null(first.SystemCpuPercent); Assert.Null(first.RecorderCpuPercent);
        Assert.Equal(123, first.WorkingSetBytes);
        now = 1999; Assert.Same(first, monitor.Sample()); Assert.Equal(1, reads);
        now = 2000; Assert.NotSame(first, monitor.Sample()); Assert.Equal(2, reads);
    }

    [Fact]
    public void CpuUsesDeltasAndRecorderIsNormalizedToWholeMachine()
    {
        long now = 0;
        ResourceCounters counters = new() { SystemIdleTicks = 100, SystemKernelTicks = 200, SystemUserTicks = 100, RecorderCpuTicks = 0 };
        var monitor = new ResourceMonitor(() => counters, () => now, processorCount: 4);
        monitor.Sample(); now = 2000;
        counters = counters with { SystemIdleTicks = 200, SystemKernelTicks = 400, SystemUserTicks = 300, RecorderCpuTicks = TimeSpan.TicksPerSecond };
        var sample = monitor.Sample();
        Assert.Equal(75, sample.SystemCpuPercent); Assert.Equal(12.5, sample.RecorderCpuPercent);
    }

    [Fact]
    public void MissingFailedAndRegressedCountersRemainUnknown()
    {
        long now = 0; bool fail = false;
        ResourceCounters counters = new() { SystemIdleTicks = 100, SystemKernelTicks = 200, SystemUserTicks = 100, RecorderCpuTicks = 100 };
        var monitor = new ResourceMonitor(() => fail ? throw new InvalidOperationException() : counters, () => now);
        monitor.Sample(); now = 2000;
        counters = counters with { SystemIdleTicks = 10, RecorderCpuTicks = 10 };
        Assert.Null(monitor.Sample().SystemCpuPercent); Assert.Null(monitor.Sample().RecorderCpuPercent);
        now = 4000; fail = true;
        var sample = monitor.Sample(); Assert.Null(sample.SystemCpuPercent); Assert.Null(sample.WorkingSetBytes);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void InvalidHardCeilingNeverChangesProcess(int percent)
    {
        Assert.Null(CpuLimit.TryApply(percent, out var error)); Assert.NotNull(error);
    }
}
