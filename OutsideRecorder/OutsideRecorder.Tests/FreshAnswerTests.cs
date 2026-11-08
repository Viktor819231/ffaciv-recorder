namespace OutsideRecorderTests;

/// <summary>"Is Civ open?" is asked many times a second; only a stale answer may cost a real look at the process list.</summary>
public sealed class FreshAnswerTests
{
    [Fact]
    public void OneAnswerServesEveryoneForTheKeepTime()
    {
        long now = 1000; int asked = 0;
        var civ = new FreshAnswer(() => { asked++; return true; }, 2000, () => now);
        for (int i = 0; i < 50; i++) Assert.True(civ.Value);
        now += 1999;
        Assert.True(civ.Value);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void AStaleAnswerIsAskedAgain()
    {
        long now = 0; bool open = true;
        var civ = new FreshAnswer(() => open, 2000, () => now);
        Assert.True(civ.Value);
        open = false;
        Assert.True(civ.Value);
        now += 2000;
        Assert.False(civ.Value);
    }

    [Fact]
    public void TheFirstQuestionIsAlwaysReallyAsked()
    {
        int asked = 0;
        Assert.True(new FreshAnswer(() => { asked++; return true; }, 2000, () => 0).Value);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void AFailedLookIsNotKept()
    {
        long now = 0; bool fail = true;
        var civ = new FreshAnswer(() => fail ? throw new InvalidOperationException() : true, 2000, () => now);
        Assert.Throws<InvalidOperationException>(() => civ.Value);
        fail = false;
        Assert.True(civ.Value);
    }
}
