using System.Runtime.InteropServices;
using OutsideRecorderShell;

public sealed class WebViewStartupTests
{
    static COMException Abort() => new("Operation aborted", unchecked((int)0x80004004));

    [Fact]
    public async Task TransientControllerAbortRetriesAndRecovers()
    {
        int attempts = 0;
        await WebViewStartup.Initialize(() => ++attempts < 3 ? Task.FromException(Abort()) : Task.CompletedTask,
            () => false, () => Task.CompletedTask);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task PersistentAbortIsBoundedForTheNativeFallback()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<COMException>(() => WebViewStartup.Initialize(() => { attempts++; throw Abort(); },
            () => false, () => Task.CompletedTask));
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task PopupClosedDuringInitializationCancelsWithoutRetry()
    {
        bool closed = false;
        int attempts = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => WebViewStartup.Initialize(() =>
        { attempts++; closed = true; throw Abort(); }, () => closed, () => Task.CompletedTask));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task OtherFailuresAreNotRetriedOrMisreportedAsCancellation()
    {
        int attempts = 0;
        await Assert.ThrowsAsync<IOException>(() => WebViewStartup.Initialize(() =>
        { attempts++; throw new IOException("Profile unavailable"); }, () => false, () => Task.CompletedTask));
        Assert.Equal(1, attempts);
    }
}
