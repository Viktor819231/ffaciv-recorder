using System.Text.Json.Nodes;

namespace OutsideRecorderShell;

/// <summary>Versioned setup completion preserves existing choices while introducing a new walkthrough once.</summary>
static class Onboarding
{
    public const int Version = 2;
    public static void Migrate(JsonObject preferences)
    {
        if (preferences["onboardingVersion"] is not JsonValue value || !value.TryGetValue<int>(out var version) || version < Version)
            preferences["firstRun"] = true;
    }

    public static void Complete(JsonObject preferences)
    {
        preferences["onboardingVersion"] = Version;
        preferences["firstRun"] = false;
    }
}
