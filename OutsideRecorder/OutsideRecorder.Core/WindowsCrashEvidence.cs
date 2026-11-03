using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace OutsideRecorderCore;

/// <summary>A Windows crash event is evidence of a fault, not a diagnosis of its underlying cause.</summary>
public sealed record GameCrashEvidence(DateTimeOffset At, string Application, string? Module, string? ExceptionCode);

/// <summary>Result of a bounded, optional local Windows event-log lookup.</summary>
public sealed record CrashEvidenceResult(string Status, IReadOnlyList<GameCrashEvidence> Crashes);

/// <summary>Reads recent Civ Application Error events only when a diagnostic report is requested.</summary>
public static class WindowsCrashEvidence
{
    const int MaxEvents = 32, MaxXmlBytes = 65536;
    static readonly HashSet<string> CivNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CivilizationV.exe", "CivilizationV_DX11.exe", "CivilizationV_Tablet.exe",
    };

    /// <summary>Queries a bounded set of local crash records. Unavailable evidence remains unknown.</summary>
    public static CrashEvidenceResult Read(DateTimeOffset since, DateTimeOffset until)
    {
        if (!OperatingSystem.IsWindows()) return new("Windows crash records unavailable on this platform", []);
        nint query = 0;
        var crashes = new List<GameCrashEvidence>();
        try
        {
            var from = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            var to = until.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
            // Filter by executable before limiting results: unrelated application faults cannot hide Civ faults.
            var apps = string.Join(" or ", CivNames.Select(n => $"Data[@Name='AppName']='{n}'"));
            var xpath = $"*[System[Provider[@Name='Application Error'] and EventID=1000 and TimeCreated[@SystemTime>='{from}' and @SystemTime<='{to}']] and EventData[{apps}]]";
            query = EvtQuery(0, "Application", xpath, 1 | 0x200); // channel path, newest first
            if (query == 0) return new("Windows crash records unavailable or access denied", []);
            for (int i = 0; i < MaxEvents; i++)
            {
                var handles = new nint[1];
                if (!EvtNext(query, 1, handles, 100, 0, out var returned))
                {
                    if (Marshal.GetLastWin32Error() != 259) // ERROR_NO_MORE_ITEMS
                        return new("Windows crash lookup incomplete or unavailable", crashes);
                    break;
                }
                if (returned == 0) break;
                try
                {
                    EvtRender(0, handles[0], 1, 0, 0, out var needed, out _);
                    if (needed <= 0 || needed > MaxXmlBytes) continue;
                    var buffer = Marshal.AllocHGlobal(needed);
                    try
                    {
                        if (!EvtRender(0, handles[0], 1, needed, buffer, out _, out _)) continue;
                        if (Parse(Marshal.PtrToStringUni(buffer) ?? "", since, until) is { } crash) crashes.Add(crash);
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                finally { if (handles[0] != 0) EvtClose(handles[0]); }
            }
            return new(crashes.Count == 0 ? "No matching Civ crash event found; absence does not establish a normal exit" : "Matching Windows Application Error events found; cause undetermined", crashes);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or DllNotFoundException or EntryPointNotFoundException or System.Xml.XmlException or ArgumentException)
        {
            return new("Windows crash records unavailable", []);
        }
        finally { if (query != 0) EvtClose(query); }
    }

    /// <summary>Extracts only time, executable basename, module basename and exception code; paths are discarded.</summary>
    public static GameCrashEvidence? Parse(string xml, DateTimeOffset since, DateTimeOffset until)
    {
        if (xml.Length > MaxXmlBytes) return null;
        try
        {
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            var system = doc.Root?.Element(ns + "System");
            if (system?.Element(ns + "EventID")?.Value != "1000" || (string?)system.Element(ns + "Provider")?.Attribute("Name") != "Application Error") return null;
            if (!DateTimeOffset.TryParse((string?)system.Element(ns + "TimeCreated")?.Attribute("SystemTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) || at < since || at > until) return null;
            var data = doc.Root?.Element(ns + "EventData")?.Elements(ns + "Data")
                .Where(e => e.Attribute("Name") is not null).GroupBy(e => (string)e.Attribute("Name")!)
                .ToDictionary(g => g.Key, g => g.First().Value, StringComparer.OrdinalIgnoreCase);
            if (data is null || !data.TryGetValue("AppName", out var app)) return null;
            app = Basename(app);
            if (!CivNames.Contains(app)) return null;
            var module = data.TryGetValue("ModuleName", out var m) ? SafeToken(Basename(m)) : null;
            var code = data.TryGetValue("ExceptionCode", out var c) ? SafeToken(c) : null;
            return new(at, app, module, code);
        }
        catch (Exception e) when (e is System.Xml.XmlException or ArgumentException) { return null; }
    }

    static string Basename(string value) => value.Replace('\\', '/').Split('/').LastOrDefault() ?? "";
    static string? SafeToken(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or 'x') ? value : null;

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint EvtQuery(nint session, string path, string query, int flags);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtNext(nint query, int count, [Out] nint[] events, int timeout, int flags, out int returned);
    [DllImport("wevtapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtRender(nint context, nint evt, int flags, int size, nint buffer, out int used, out int properties);
    [DllImport("wevtapi.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool EvtClose(nint handle);
}
