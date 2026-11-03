using System.Text.Json.Nodes;
using OutsideRecorderShell;

public sealed class OnboardingTests
{
    [Fact]
    public void OldIntroductionDoesNotSkipNewSetupOrResetChoices()
    {
        var preferences = JsonNode.Parse("""{"firstRun":false,"settings":{"stream":false,"recordSinglePlayer":true}}""")!.AsObject();
        Onboarding.Migrate(preferences);
        Assert.True(preferences["firstRun"]!.GetValue<bool>());
        Assert.False(preferences["settings"]!["stream"]!.GetValue<bool>());
        Assert.True(preferences["settings"]!["recordSinglePlayer"]!.GetValue<bool>());
    }

    [Fact]
    public void CompletedSetupStaysCompleteAfterRestart()
    {
        var preferences = new JsonObject { ["firstRun"] = true };
        Onboarding.Complete(preferences);
        var reloaded = JsonNode.Parse(preferences.ToJsonString())!.AsObject();
        Onboarding.Migrate(reloaded);
        Assert.False(reloaded["firstRun"]!.GetValue<bool>());
        Assert.Equal(Onboarding.Version, reloaded["onboardingVersion"]!.GetValue<int>());
    }

    [Fact]
    public void IncompleteSetupRemainsPending()
    {
        var preferences = new JsonObject { ["firstRun"] = true, ["onboardingVersion"] = Onboarding.Version };
        Onboarding.Migrate(preferences);
        Assert.True(preferences["firstRun"]!.GetValue<bool>());
    }
}
