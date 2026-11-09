using System.Drawing.Imaging;
using OutsideRecorderCore;

namespace OutsideRecorderShell;

static class Program
{
#if !RECORDER_PUBLIC_RELEASE
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);
#endif

    /// <summary>
    /// FFACivRecorder.exe              the recorder with its window (records Civ for real, uses ffaciv.com by default)
    ///   --demo                        the window on made-up data (preview: nothing is read or written)
    ///   --tray                        start hidden by the clock (how Windows starts it)
    ///   --setup                       open the introduction without resetting saved preferences
    ///   --dev                         allow the browser tools (F12) in the window
    ///   --ui-shot &lt;folder&gt; [--live]  save pictures of the window (made-up data, or --live: the real recorder on this PC),
    ///                                 the tray icons and the tray menu, then quit
    ///   developer builds only: --check, --replay-tape &lt;session folder&gt; (<see cref="DevTools"/>), --stream-test …
    /// settings.txt next to the exe: where the stream goes (stream_url), the live site (live_site_url), folders.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
#if !RECORDER_PUBLIC_RELEASE
        // Developer builds only: these harnesses can send requests outside the normal player capture policy.
        if (args.Any(a => a is "--stream-test" or "--stream-only" or "--stream-verify" or "--site-check" or "--check" or "--replay-tape"))
        {
            AttachConsole(-1);   // a window program: write to the console it was started from
            try
            {
                return args.Contains("--stream-test") ? StreamTest.Run(args) : args.Contains("--stream-only") ? StreamTest.StreamOnly(args)
                    : args.Contains("--stream-verify") ? StreamTest.Verify(args) : args.Contains("--site-check") ? SiteCheck.Run(args) : DevTools.Run(args);
            }
            finally { Console.Out.Flush(); }
        }
#else
        if (args.Any(a => a is "--stream-test" or "--stream-only" or "--stream-verify" or "--site-check" or "--check" or "--replay-tape" or "--dev"))
        {
            MessageBox.Show("Developer tools are not included in this installer.", "FFACiv Recorder");
            return 2;
        }
#endif
        // Nothing may end the recorder unseen: errors on the window's thread are logged instead of a WinForms dialog (which
        // stacked up every few seconds under a failing refresh), and a fatal one leaves its reason in errors.log.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => WebUi.LogError("window", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WebUi.LogError(e.IsTerminating ? "fatal" : "background", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { WebUi.LogError("background task", e.Exception); e.SetObserved(); };
        bool demo = args.Contains("--demo"), dev = args.Contains("--dev"), tray = args.Contains("--tray");
        int shotAt = Array.IndexOf(args, "--ui-shot");
        string? shotDir = shotAt >= 0 && shotAt + 1 < args.Length ? args[shotAt + 1] : null;
        using var single = new Mutex(true, demo || shotDir is not null ? "FFACivRecorderUI.Preview." + Environment.ProcessId : "FFACivRecorderUI.Single", out bool first);
        if (!first) { if (!tray) MessageBox.Show("The FFACiv Recorder is already running. Click its ring icon by the clock.", "FFACiv Recorder"); return 0; }
        ApplicationConfiguration.Initialize();
        WebUi.Preview = demo || shotDir is not null;

        if (Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() is null)
        {
            MessageBox.Show("This window needs Microsoft Edge WebView2, which comes with Windows 10 and 11. Please install it from microsoft.com/edge/webview2.", "FFACiv Recorder");
            return 1;
        }

        IRecorderBackend? backend = null;
        bool live = !demo && (shotDir is null || args.Contains("--live"));
        if (live)
        {
            var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.txt");
            // the user build sends to the site (ffaciv.com or the stream_url of a test environment); "local" for our tests (OR05)
            if (!File.Exists(settingsPath)) { try { File.WriteAllText(settingsPath, RecorderSettings.DefaultFileText("site")); } catch { } }
            backend = new LiveBackend(RecorderSettings.Load(settingsPath).ForDesktop(args.Contains("--memory-test")), settingsPath, typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.7.3", preview: shotDir is not null);
        }
        var form = new ShellForm(backend, dev, tray && shotDir is null, args.Contains("--setup"));
        if (shotDir is not null) Shots(form, shotDir, live);
        Application.Run(form);
        return 0;
    }

    /// <summary>Pictures for the user (they judge from pictures): the window as WebView2 draws it, the tray icons, the tray menu.</summary>
    static void Shots(ShellForm form, string dir, bool live)
    {
        Directory.CreateDirectory(dir);
        var t = new System.Windows.Forms.Timer { Interval = 300 };
        int ticks = 0;
        t.Tick += async (_, _) =>
        {
            if (!form.PageReady && ++ticks < 60) return;
            t.Stop();
            await Task.Delay(live ? 6000 : 1200);   // live: give the recorder a few looks at Civ and the server
            var core = form.View.CoreWebView2;
            async Task Shot(string name, string? script = null)
            {
                if (script is not null) { await core.ExecuteScriptAsync(script); await Task.Delay(700); }
                using var f = File.Create(Path.Combine(dir, name));
                await core.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, f);
            }
            await Shot("40-window-in-webview2.png");
            if (live)
            {
                // the screens behind the first-start steps (only hidden for the picture; nothing is changed)
                const string hideWelcome = "App.state && (App.state.firstRun = false);";
                await Shot("50-live-home.png", hideWelcome + "App.go('home')");
                await Shot("51-live-games.png", hideWelcome + "App.go('games')");
                await Shot("52-live-settings.png", hideWelcome + "App.go('settings')");
                await Shot("53-live-checks.png", hideWelcome + "App.go('checks')");
                await Shot("55-live-old-games.png", hideWelcome + "App.go('old')");
                await core.ExecuteScriptAsync("App.go('home')");
            }
            // the five tray icons, at the size Windows shows them and 4× for reading
            using (var strip = new Bitmap(5 * 150, 130))
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.FromArgb(32, 32, 32));
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                using var font = new Font("Segoe UI", 9f);
                for (int i = 0; i < TrayIcons.Kinds.Length; i++)
                {
                    var k = TrayIcons.Kinds[i];
                    using var big = TrayIcons.Draw(k, 64); using var small = TrayIcons.Draw(k, 16);
                    g.DrawImage(big, i * 150 + 20, 14);
                    g.DrawImage(small, i * 150 + 100, 38);
                    g.DrawString(new[] { "waiting / idle", "recording", "sending", "needs attention", "paused" }[i], font, Brushes.White, i * 150 + 18, 92);
                }
                strip.Save(Path.Combine(dir, "41-tray-icons.png"), ImageFormat.Png);
            }
            // the tray menu, drawn by itself
            form.ShowMenuAt(new Point(form.Left + 40, form.Top + 80));
            await Task.Delay(400);
            var m = form.TrayMenu;
            using (var bmp = new Bitmap(m.Width, m.Height))
            {
                m.DrawToBitmap(bmp, new Rectangle(0, 0, m.Width, m.Height));
                bmp.Save(Path.Combine(dir, "42-tray-menu.png"), ImageFormat.Png);
            }
            m.Close();
            // one real pop-up window, as Windows shows it in the corner
            form.ShowToast(J.Obj(("kind", "good"), ("title", "Recording game"), ("text", "Turn 12, 6 players, single player."),
                ("actions", new System.Text.Json.Nodes.JsonArray(J.Obj(("label", "Don't record this game"), ("cmd", "dontRecord")), J.Obj(("label", "Start livestream"), ("cmd", "copyLiveLink"), ("primary", true)))), ("timeout", 60000)));
            for (int i = 0; i < 50 && ToastWindow.Newest?.Placed != true; i++) await Task.Delay(200);
            await Task.Delay(500);
            if (ToastWindow.Newest is { } toast)
                using (var f = File.Create(Path.Combine(dir, "45-toast-real-window.png"))) await toast.CapturePng(f);
            form.ShowToast(J.Obj(("kind", "info"), ("title", "Single-player game — not recorded"), ("text", "Turn 12. Single-player games are off in Settings (for testing, coming later)."),
                ("actions", new System.Text.Json.Nodes.JsonArray(J.Obj(("label", "Record it anyway"), ("cmd", "recordAnyway"), ("primary", true)))), ("timeout", 60000)));
            await Task.Delay(300);
            for (int i = 0; i < 50 && ToastWindow.Newest?.Placed != true; i++) await Task.Delay(200);
            await Task.Delay(500);
            if (ToastWindow.Newest is { } sp)
                using (var f = File.Create(Path.Combine(dir, "46-toast-single-player.png"))) await sp.CapturePng(f);
            form.Hide();
            (form as IDisposable)?.Dispose();
            Environment.Exit(0);
        };
        form.Shown += (_, _) => t.Start();
    }
}
