namespace OutsideRecorderTests;

/// <summary>
/// Real recordings, saves and PDBs that some tests measure against. They are the developer's own games, never in the
/// repository: on any other PC (or GitHub's build machine) the path does not exist and those tests skip themselves.
/// </summary>
static class TestData
{
    /// <summary>`RECORDER_TEST_DATA`, then every folder above the test binaries, is searched for <paramref name="parts"/>;
    /// a path that does not exist comes back when none holds it.</summary>
    public static string Find(params string[] parts)
    {
        var relative = Path.Combine(parts);
        var root = Environment.GetEnvironmentVariable("RECORDER_TEST_DATA");
        if (!string.IsNullOrEmpty(root) && Path.Exists(Path.Combine(root, relative))) return Path.Combine(root, relative);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Path.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return Path.Combine(AppContext.BaseDirectory, "missing-test-data", relative);
    }

    /// <summary>A file or folder under the developer's `test saves` folder.</summary>
    public static string Saves(string relative) => Find("test saves", relative);

    /// <summary>The Lekmod 35.4 PDB the layout tests check against, as the developer keeps it beside the project.</summary>
    public static string LekmodPdb => Find("out", "lek-monitor", "symbols", "CvGameCore_Expansion2.pdb");
}
