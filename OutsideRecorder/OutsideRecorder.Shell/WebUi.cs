using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace OutsideRecorderShell;

/// <summary>
/// Where the HTML comes from and the one WebView2 setup every window shares. In developer builds the UI can be updated:
/// a newer folder under %LOCALAPPDATA%\FFACiv\OutsideRecorder\app\ui-updates\&lt;n&gt;\ (with a higher ui-version.txt) wins over the
/// one next to the exe, so a look update never replaces the program.
/// </summary>
static class WebUi
{
    /// <summary>The page host name: pages are served from the UI folder under https://recorder.ui/ (never from the internet).</summary>
    public const string Host = "recorder.ui";
    public static readonly Color Background = Color.FromArgb(9, 20, 23);
    [ThreadStatic] static Task<CoreWebView2Environment>? environment;
    /// <summary>Preview windows use their own browser profile, separate from the running recorder.</summary>
    public static bool Preview { get; set; }

    static Task<CoreWebView2Environment> EnvironmentAsync()
    {
        // WebView2 environments are COM apartment-bound. Share only within the owning UI thread.
        // Notification windows also use a separate browser profile so their renderer cannot stall the main page.
        var notifications = Thread.CurrentThread.Name == "Recorder notifications";
        if (environment is null || environment.IsFaulted || environment.IsCanceled)
            environment = CoreWebView2Environment.CreateAsync(null,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app",
                    Preview ? $"webview-preview-{Environment.ProcessId}-{(notifications ? "notifications" : "main")}" : notifications ? "webview-notifications" : "webview"));
        return environment;
    }

    /// <summary>An error nobody else caught (errors.log beside display-errors.log; the older half goes past 1 MB). The
    /// recorder keeps running where it can: a WinForms error dialog every few seconds helped nobody.</summary>
    public static void LogError(string where, object? error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app");
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, "errors.log");
            if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024) File.Move(file, file + ".old", overwrite: true);
            File.AppendAllText(file, $"{DateTimeOffset.Now:o} {where}: {error}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void LogStartupError(Exception error)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "display-errors.log"), $"{DateTimeOffset.Now:o} {error}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string UpdatesFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app", "ui-updates");

    /// <summary>The UI folder to show: the newest update that has an index.html, else the one shipped next to the exe.</summary>
    public static (string Folder, int Version) Folder()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "ui");
        var best = (Folder: shipped, Version: VersionOf(shipped));
#if !RECORDER_PUBLIC_RELEASE
        try
        {
            if (Directory.Exists(UpdatesFolder))
                foreach (var d in Directory.GetDirectories(UpdatesFolder))
                    if (File.Exists(Path.Combine(d, "index.html")) && VersionOf(d) > best.Version) best = (d, VersionOf(d));
        }
        catch { }
#endif
        return best;
    }

    static int VersionOf(string dir) { try { return int.Parse(File.ReadAllText(Path.Combine(dir, "ui-version.txt")).Trim()); } catch { return 0; } }

    /// <summary>Makes <paramref name="view"/> ready: shared browser data folder, our UI folder mapped, nothing else reachable.</summary>
    public static async Task Init(WebView2 view, bool dev)
    {
        view.DefaultBackgroundColor = Background;
        await WebViewStartup.Initialize(async () =>
        {
            var env = await EnvironmentAsync();
            if (view.IsDisposed || view.Disposing) throw new OperationCanceledException();
            await view.EnsureCoreWebView2Async(env);
        }, () => view.IsDisposed || view.Disposing);
        if (view.IsDisposed || view.Disposing) throw new OperationCanceledException();
        var core = view.CoreWebView2;
        var st = core.Settings;
        st.AreDevToolsEnabled = dev; st.AreDefaultContextMenusEnabled = dev; st.AreBrowserAcceleratorKeysEnabled = dev;
        st.IsStatusBarEnabled = false; st.IsZoomControlEnabled = false; st.IsPasswordAutosaveEnabled = false; st.IsGeneralAutofillEnabled = false;
        core.SetVirtualHostNameToFolderMapping(Host, Folder().Folder, CoreWebView2HostResourceAccessKind.DenyCors);
        // links to anything else open in the player's own browser, never inside the recorder
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Host == Host) return;
            e.Cancel = true; OpenExternal(e.Uri);
        };
    }

    /// <summary>Opens a link in the player's own browser: https anywhere, plain http only on this PC (the local test site).</summary>
    public static void OpenExternal(string url)
    {
        bool local = Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && (u.IsLoopback || u.Host == "localhost");
        if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || local)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    /// <summary>The dark title bar of Windows 10/11.</summary>
    public static void DarkTitle(IntPtr hwnd) { int on = 1; _ = DwmSetWindowAttribute(hwnd, 20, ref on, 4); }
}
