using OutsideRecorderCore;

namespace OutsideRecorder;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // --check: print what it finds and exit (for testing without the window).
        if (args.Contains("--check")) return Check.Run();
        if (args.Contains("--stream-test")) return StreamTest.Run(args);
        if (args.Contains("--stream-only")) return StreamTest.StreamOnly(args);
        if (args.Contains("--stream-verify")) return StreamTest.Verify(args);
        if (args.Contains("--site-check")) return SiteCheck.Run(args);
        // --replay-tape <session folder>: re-read a recorded tape with this version, write events.replay.jsonl next to it.
        int rt = Array.IndexOf(args, "--replay-tape");
        if (rt >= 0 && rt + 1 < args.Length)
        {
            try
            {
                var (samples, lines, output) = TapeReplay.Run(args[rt + 1]);
                Console.WriteLine($"replayed {samples} samples -> {lines} lines in {output}");
                return 0;
            }
            catch (Exception e) { Console.WriteLine("replay failed: " + e.Message); return 2; }
        }
        using var single = new Mutex(true, "FFACivOutsideRecorder.Single", out bool first);
        if (!first) { MessageBox.Show("The Outside Recorder is already running (see the taskbar).", "FFACiv Outside Recorder"); return 0; }
        ApplicationConfiguration.Initialize();
        var settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.txt");
        if (!File.Exists(settingsPath)) { try { File.WriteAllText(settingsPath, RecorderSettings.DefaultFileText()); } catch { } }
        var settings = RecorderSettings.Load(settingsPath);
        // "--stream local" (the Desktop launcher for the local test): overrides settings.txt for this run only
        int so = Array.IndexOf(args, "--stream");
        if (so >= 0 && so + 1 < args.Length && args[so + 1] is "off" or "local" or "site") settings = settings with { Stream = args[so + 1] };
        var form = new MainForm(settings, settingsPath);
        // --ui-shot <prefix>: after 3 s save the window and the newest pop-up as PNGs, then close (for checking the look).
        int shot = Array.IndexOf(args, "--ui-shot");
        if (shot >= 0 && shot + 1 < args.Length)
        {
            var prefix = args[shot + 1];
            int wait = shot + 2 < args.Length && int.TryParse(args[shot + 2], out var ms) ? ms : 3000;
            var t = new System.Windows.Forms.Timer { Interval = wait };
            t.Tick += (_, _) =>
            {
                t.Stop();
                Save(form, prefix + "-window.png");
                if (Application.OpenForms.OfType<Toast>().LastOrDefault() is { } toast) Save(toast, prefix + "-toast.png");
                form.Close();
            };
            form.Shown += (_, _) => t.Start();
        }
        Application.Run(form);
        return 0;
    }

    static void Save(Form f, string path)
    {
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }
}
