using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// Run with ./dn.sh run OutsideRecorder/installer/Verify-PlayerBuild.cs -- <published app>.
// Inspect metadata without loading/executing the recorder or contacting a server.
if (args.Length != 1) { Console.Error.WriteLine("Expected the published player app directory."); return 2; }
using var stream = File.OpenRead(Path.Combine(args[0], "OutsideRecorder.Core.dll"));
using var pe = new PEReader(stream);
var metadata = pe.GetMetadataReader();
var forbidden = new HashSet<string> { "MemoryDump", "StreamTest", "SiteCheck" };
var found = metadata.TypeDefinitions.Select(h => metadata.GetString(metadata.GetTypeDefinition(h).Name)).Where(forbidden.Contains).ToArray();
if (found.Length > 0) { Console.Error.WriteLine("Developer tools in player assembly: " + string.Join(", ", found)); return 1; }
using var shellStream = File.OpenRead(Path.Combine(args[0], "FFACivRecorder.dll"));
using var shellPe = new PEReader(shellStream);
var shellMetadata = shellPe.GetMetadataReader();
var shellTypes = shellMetadata.TypeDefinitions.Select(h => shellMetadata.GetString(shellMetadata.GetTypeDefinition(h).Name)).ToHashSet();
if (!shellTypes.Contains("Onboarding") || !shellTypes.Contains("CapturePreferences") || !shellTypes.Contains("WebViewStartup"))
{
    Console.Error.WriteLine("Player shell is missing the current introduction, preferences or startup recovery.");
    return 1;
}
var uiVersionFile = Path.Combine(args[0], "ui", "ui-version.txt");
if (!int.TryParse(File.ReadAllText(uiVersionFile).Trim(), out var uiVersion) || uiVersion < 14)
{
    Console.Error.WriteLine("Player UI predates the UI14 introduction/deletion updates.");
    return 1;
}
Console.WriteLine("Player assembly verified: no memory-dump or stream/site-test types.");
Console.WriteLine($"Player shell verified: versioned introduction and current UI{uiVersion} are included.");
return 0;
