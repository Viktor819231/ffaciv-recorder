using System.Diagnostics;
using OutsideRecorderCore;

namespace OutsideRecorder;

/// <summary>The small status window: game, turn, units/cities seen, recording state. Closing it stops the recorder.</summary>
sealed class MainForm : Form
{
    readonly RecorderSession session;
    readonly RecorderSettings settings;
    readonly Label state = new(), game = new(), turn = new(), counts = new(), events = new(), autosave = new(), folder = new(), warning = new(), stream = new();
    readonly Streamer streamer;
    readonly Panel dot = new() { Size = new Size(14, 14), Margin = new Padding(0, 6, 8, 0) };
    readonly System.Windows.Forms.Timer refresh = new() { Interval = 500 };
    readonly NotifyIcon tray = new();

    public MainForm(RecorderSettings settings, string settingsPath)
    {
        this.settings = settings;
        Text = "FFACiv Outside Recorder";
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 520);
        BackColor = Color.FromArgb(24, 28, 36);
        ForeColor = Color.FromArgb(230, 232, 236);
        Icon = SystemIcons.Information;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 11 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var head = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 8) };
        state.AutoSize = true; state.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
        head.Controls.Add(dot); head.Controls.Add(state);
        layout.Controls.Add(head, 0, 0); layout.SetColumnSpan(head, 2);
        void Row(int r, string name, Label value)
        {
            var n = new Label { Text = name, AutoSize = true, ForeColor = Color.FromArgb(150, 156, 168), Margin = new Padding(0, 3, 0, 3) };
            value.AutoSize = true; value.MaximumSize = new Size(320, 0); value.Margin = new Padding(0, 3, 0, 3);
            layout.Controls.Add(n, 0, r); layout.Controls.Add(value, 1, r);
        }
        Row(1, "Game", game); Row(2, "Turn", turn); Row(3, "Yours now", counts); Row(4, "Recorded", events);
        Row(5, "Last autosave", autosave); Row(6, "Folder", folder); Row(10, "Stream", stream);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 0) };
        buttons.Controls.Add(Button("Open recordings", () => OpenFolder(settings.OutputFolder)));
        buttons.Controls.Add(Button("Settings", () => Process.Start(new ProcessStartInfo(settingsPath) { UseShellExecute = true })));
        buttons.Controls.Add(Button("Snapshot", TakeSnapshot));
        layout.Controls.Add(buttons, 0, 7); layout.SetColumnSpan(buttons, 2);
        warning.AutoSize = true; warning.MaximumSize = new Size(460, 0); warning.ForeColor = Color.FromArgb(240, 180, 90); warning.Visible = false;
        layout.Controls.Add(warning, 0, 9); layout.SetColumnSpan(warning, 2);
        var note = new Label { AutoSize = true, MaximumSize = new Size(430, 0), ForeColor = Color.FromArgb(120, 126, 138), Font = new Font(Font.FontFamily, 8.5f),
            Text = "Reads the game from outside, read-only. Never changes Civ, never uploads. Close this window to stop." };
        layout.Controls.Add(note, 0, 8); layout.SetColumnSpan(note, 2);
        Controls.Add(layout);

        tray.Icon = SystemIcons.Information; tray.Text = "FFACiv Outside Recorder"; tray.Visible = true;
        tray.DoubleClick += (_, _) => { Show(); WindowState = FormWindowState.Normal; Activate(); };

        session = new RecorderSession(settings);
        streamer = new Streamer(settings);
        session.Notice += (title, text) => { if (settings.Notifications) BeginInvoke(() => Toast.Show(title, text)); };
        refresh.Tick += (_, _) => Draw();
        Load += (_, _) =>
        {
            session.Start(); streamer.Start(); refresh.Start(); Draw();
            if (settings.Notifications) Toast.Show("Outside Recorder is on", "It records Civ V (no mods, BNW 1.0.3.279), LEKMOD 35.4 and Snowve 8.1.1–8.3.0 games. Nothing is uploaded.");
        };
        FormClosing += (_, _) => { refresh.Stop(); session.Dispose(); streamer.Dispose(); tray.Visible = false; tray.Dispose(); };
    }

    Button Button(string text, Action click)
    {
        var b = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(44, 50, 62), ForeColor = ForeColor, Margin = new Padding(0, 0, 8, 0) };
        b.FlatAppearance.BorderColor = Color.FromArgb(70, 78, 92);
        b.Click += (_, _) => click();
        return b;
    }

    void Draw()
    {
        var s = session.Status;
        state.Text = s.State;
        dot.BackColor = s.Recording ? Color.FromArgb(220, 60, 60) : Color.FromArgb(110, 116, 128);
        game.Text = s.Game ?? "–";
        turn.Text = s.Turn?.ToString() ?? "–";
        // only the local player's own numbers: nothing here that the game does not already show you
        counts.Text = s.Recording ? $"{s.MyUnits} units, {s.MyCities} cities (sample {s.LastSampleMs:F0} ms)" : "–";
        events.Text = s.Recording ? $"{s.Events} lines{(s.TapeBytes is long tb ? $", tape {tb / 1024.0 / 1024:F1} MB" : "")}{(s.Mode is { } m ? $" – {m}" : "")}" : "–";
        var st = streamer.Status;
        stream.Text = settings.Stream == "off" ? "off (settings.txt: stream = local or site)"
            : $"{st.State} – {settings.StreamAddress}" + Environment.NewLine +
              $"queued {st.QueuedEventBytes / 1024} KB + {st.QueuedSaves} saves; sent {st.EventWireBytes / 1024} KB events, {st.SaveWireBytes / 1024} KB saves " +
              $"({st.FullSaves} whole, {st.DeltaSaves} delta), {st.Requests} requests" + (st.LastSent is { } ls ? $"; last {ls:HH:mm:ss}" : "");
        warning.Visible = s.InGameRecorderPresent || s.LastError is not null || s.AutosaveWarning is not null;
        warning.Text = (s.InGameRecorderPresent ? @"The in-game FFACiv_Recorder folder is in Assets\DLC. For this test, move it out of Assets\DLC while Civ is closed. " : "")
            + (s.AutosaveWarning is { } aw ? aw + " " : "")
            + (s.LastError is { } e ? "Last problem: " + e : "");
        autosave.Text = s.LastAutosave ?? "none yet";
        folder.Text = s.Folder ?? settings.OutputFolder;
        tray.Text = s.Recording ? $"Recording, turn {s.Turn}" : "Outside Recorder: " + Shorten(s.State, 40);
    }

    void TakeSnapshot()
    {
        var g = session.Attached;
        if (g is null) { MessageBox.Show(this, "Civ is not being read right now, so there is nothing to copy.", Text); return; }
        if (MessageBox.Show(this, "This copies Civ's memory to one file (about 1-2 GB) so we can look for more game data later." + Environment.NewLine + Environment.NewLine +
            "It only reads (Civ is not paused or changed), but the PC is busy for a while and the game may stutter. " +
            "Best done between turns. Continue?", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        var dir = Path.Combine(settings.OutputFolder, "memory-snapshots");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"civ-{DateTime.Now:yyyyMMddTHHmmss}-{g.ProcessId}.ordump");
        Toast.Show("Snapshot", "Copying Civ's memory… you can keep playing.");
        Task.Run(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                long n = MemoryDump.Write(g.ProcessId, path, g.GameCore, new { layout = g.Layout?.Id, g.GameCoreSha256, turn = session.Status.Turn, time = DateTimeOffset.Now });
                BeginInvoke(() => Toast.Show("Snapshot saved", $"{n / 1024 / 1024} MB in {sw.Elapsed.TotalSeconds:F0} s." + Environment.NewLine + path));
            }
            catch (Exception e)
            {
                try { File.Delete(path); } catch { }
                BeginInvoke(() => Toast.Show("Snapshot not possible", e.Message));
            }
        });
    }

    static string Shorten(string s, int n) => s.Length <= n ? s : s[..n];

    static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}

/// <summary>Our own bottom-right pop-up (no Windows toast registration needed). Fades after a few seconds; click to close.</summary>
sealed class Toast : Form
{
    static readonly List<Toast> open = new();
    readonly System.Windows.Forms.Timer life = new() { Interval = 6000 };

    Toast(string title, string text)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(32, 38, 50); ForeColor = Color.White; Padding = new Padding(14, 10, 14, 12);
        Font = new Font("Segoe UI", 10f); AutoScaleMode = AutoScaleMode.Dpi;
        var t = new Label { Text = title, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Top };
        var b = new Label { Text = text, AutoSize = true, MaximumSize = new Size(340, 0), Dock = DockStyle.Top, ForeColor = Color.FromArgb(205, 210, 220), Padding = new Padding(0, 4, 0, 0) };
        Controls.Add(b); Controls.Add(t);
        Size = new Size(370, 40 + (int)(b.GetPreferredSize(new Size(340, 0)).Height));
        foreach (Control c in new Control[] { this, t, b }) c.Click += (_, _) => Close();
        life.Tick += (_, _) => Close();
        FormClosed += (_, _) => { life.Dispose(); open.Remove(this); };
    }

    protected override bool ShowWithoutActivation => true; // never steals focus from the game

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/; return cp; }
    }

    /// <summary>Shows one pop-up above any already showing.</summary>
    public static void Show(string title, string text)
    {
        var t = new Toast(title, text);
        var area = Screen.PrimaryScreen!.WorkingArea;
        int y = area.Bottom - 12 - open.Sum(o => o.Height + 8) - t.Height;
        t.Location = new Point(area.Right - t.Width - 12, Math.Max(area.Top, y));
        open.Add(t);
        t.Show();
        t.life.Start();
    }
}
