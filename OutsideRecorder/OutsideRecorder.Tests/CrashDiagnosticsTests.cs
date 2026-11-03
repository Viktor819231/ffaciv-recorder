namespace OutsideRecorderTests;

public sealed class CrashDiagnosticsTests
{
    [Fact]
    public async Task LookupIsExplicitBoundedAndDoesNotRepeatFromReadingState()
    {
        var now = DateTimeOffset.Parse("2026-10-30T12:00:00Z");
        int calls = 0;
        var diagnostics = new CrashDiagnostics((since, until) => {
            calls++;
            Assert.Equal(now.AddHours(-24), since); Assert.Equal(now, until);
            return new("Matching events; cause undetermined", Enumerable.Range(0, 40)
                .Select(_ => new GameCrashEvidence(now, "CivilizationV.exe", "module.dll", "0xc0000005")).ToArray());
        }, () => now);
        Assert.Null(diagnostics.Snapshot.Result);
        Assert.Equal(0, calls);
        await diagnostics.RefreshAsync();
        Assert.Equal(32, diagnostics.Snapshot.Result!.Crashes.Count);
        Assert.Equal(now, diagnostics.Snapshot.CheckedAt);
        Assert.Equal(1, calls);
        Assert.False(diagnostics.Snapshot.Busy);
    }

    [Fact]
    public async Task RepeatedClicksDoNotStartParallelLookupsAndFailureStaysUnknown()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var diagnostics = new CrashDiagnostics((_, _) => {
            Interlocked.Increment(ref calls); started.Set(); release.Wait(); throw new IOException();
        });
        var first = diagnostics.RefreshAsync();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try {
            await diagnostics.RefreshAsync();
            Assert.True(diagnostics.Snapshot.Busy);
            Assert.Equal(1, calls);
        } finally { release.Set(); }
        await first;
        Assert.Contains("unavailable", diagnostics.Snapshot.Result!.Status);
        Assert.Empty(diagnostics.Snapshot.Result.Crashes);
        Assert.False(diagnostics.Snapshot.Busy);
    }
}
