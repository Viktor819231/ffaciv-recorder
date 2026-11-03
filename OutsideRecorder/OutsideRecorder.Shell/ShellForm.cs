using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.WinForms;

namespace OutsideRecorderShell;

/// <summary>
/// The recorder's window (ui/index.html in WebView2) + the tray icon and its menu. Closing the window hides it to the
/// tray; only Quit ends the program. With no backend (preview) the page runs on its made-up data and tells this window
/// which tray icon and pop-ups to show.
/// </summary>
sealed class ShellForm : Form
{
    readonly WebView2 view = new() { Dock = DockStyle.Fill };
    readonly IRecorderBackend? backend;
    readonly bool demo, dev, showSetup;
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new() { Renderer = new DarkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = true };
    readonly ToolStripMenuItem statusItem = new() { Enabled = false };
    readonly ToolStripMenuItem pauseItem = new("Pause recording for this game");
    readonly ToolStripMenuItem linkItem = new("Livestream options…");
    readonly System.Windows.Forms.Timer poll = new() { Interval = 700 };
    string lastStateJson = "", trayKind = "";
    bool quitting, toldAboutTray, pageReady;
    JsonObject? lastState;

    public ShellForm(IRecorderBackend? backend, bool dev, bool startHidden, bool showSetup = false)
    {
        this.backend = backend; demo = backend is null; this.dev = dev; this.showSetup = showSetup;
        Text = "FFACiv Recorder";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1100, 720);
        MinimumSize = SizeFromClientSize(new Size(800, 560));
        BackColor = WebUi.Background;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Get("waiting", 32);
        Controls.Add(view);
        if (startHidden) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

        // ---- tray ----
        menu.Font = new Font("Segoe UI", 9.5f);
        var openItem = new ToolStripMenuItem("Open FFACiv Recorder", null, (_, _) => ShowWindow()) { Font = new Font(menu.Font, FontStyle.Bold) };
        pauseItem.Click += (_, _) => Command("pauseGame", []);
        linkItem.Click += (_, _) => ShowRecorder();
        menu.Items.AddRange([statusItem, new ToolStripSeparator(), openItem,
            new ToolStripMenuItem("Recording methods & setup…", null, (_, _) => ShowGuide()), linkItem, pauseItem,
            new ToolStripMenuItem("Open ffaciv.com", null, (_, _) => WebUi.OpenExternal("https://ffaciv.com")),
            new ToolStripSeparator(), new ToolStripMenuItem("Quit", null, (_, _) => Quit())]);
        menu.Opening += (_, _) => UpdateMenu();
        tray.ContextMenuStrip = menu;
        tray.Text = "FFACiv Recorder";
        SetTray("waiting", "FFACiv Recorder");
        tray.Visible = true;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        if (backend is not null) backend.Toast += t => BeginInvokeSafe(() => {
            Post(J.Obj(("type", "result"), ("toast", t.DeepClone())));
            if (!Visible || WindowState == FormWindowState.Minimized || t["actions"] is not null) ShowToast(t);
        });
        poll.Tick += (_, _) => PushState();
        // hidden by the clock (how it runs during a game): the state is built less often, the tray icon still follows
        VisibleChanged += (_, _) => poll.Interval = Visible && WindowState != FormWindowState.Minimized ? 700 : 3000;
        Resize += (_, _) => poll.Interval = Visible && WindowState != FormWindowState.Minimized ? 700 : 3000;
        HandleCreated += (_, _) => WebUi.DarkTitle(Handle);
        WindowPlace.Restore(this);
        FormClosed += (_, _) => ToastWindow.CloseAll();
        ResizeEnd += (_, _) => WindowPlace.Save(this);
        FormClosing += (_, _) => WindowPlace.Save(this);
        Load += async (_, _) => await StartDisplay();
        FormClosing += (_, e) =>
        {
            if (quitting) return;
            if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) { Quit(); return; }
            e.Cancel = true; Hide();
            if (!toldAboutTray) { toldAboutTray = true; ShowToast(J.Toast("info", "Still recording, by the clock", "Click the ring icon by the clock to open it again. Right-click it to quit.")); }
        };
    }

    public bool PageReady => pageReady;
    public WebView2 View => view;
    public ContextMenuStrip TrayMenu => menu;

    async Task StartDisplay()
    {
        try
        {
            await WebUi.Init(view, dev);
            if (IsDisposed || Disposing || quitting) return;
            view.CoreWebView2.WebMessageReceived += (_, e) => OnMessage(JsonNode.Parse(e.WebMessageAsJson) as JsonObject);
            var query = demo ? "?demo=1" : "";
            if (showSetup) query += (demo ? "&" : "?") + "setup=1";
            view.CoreWebView2.Navigate($"https://{WebUi.Host}/index.html{query}");
            backend?.Start();
            poll.Start();
        }
        catch (OperationCanceledException) when (IsDisposed || Disposing || quitting) { }
        catch (Exception e)
        {
            WebUi.LogStartupError(e);
            if (IsDisposed || Disposing || quitting) return;
            view.Visible = false;
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(28), BackColor = BackColor };
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(650, 0), ForeColor = Color.White,
                Text = "The recorder window could not start.\nYour recordings and settings are kept. Try opening the window again." });
            var retry = new Button { Text = "Try again", AutoSize = true };
            retry.Click += async (_, _) => { Controls.Remove(panel); panel.Dispose(); view.Visible = true; await StartDisplay(); };
            panel.Controls.Add(retry);
            var quit = new Button { Text = "Quit recorder", AutoSize = true };
            quit.Click += (_, _) => Quit(); panel.Controls.Add(quit);
            Controls.Add(panel); panel.BringToFront();
        }
    }

    void BeginInvokeSafe(Action a)
    {
        if (!IsHandleCreated || IsDisposed || Disposing) return;
        try { BeginInvoke(() => { if (!IsDisposed && !Disposing) a(); }); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    void OnMessage(JsonObject? m)
    {
        if (m is null) return;
        var cmd = m.Str("cmd") ?? ""; var args = m["args"] as JsonObject ?? [];
        switch (cmd)
        {
            case "ready": pageReady = true; lastStateJson = ""; PushState(); return;
            case "ui.tray": pageReady = true; SetTray(args.Str("kind") ?? "waiting", args.Str("tip") ?? "FFACiv Recorder"); return;
            case "ui.toast": ShowToast(args); return;
            case "showWindow": ShowWindow(); return;
            case "hideWindow": Hide(); return;
        }
        Command(cmd, args);
    }

    /// <summary>A command from the window, a pop-up or the tray menu.</summary>
    void Command(string cmd, JsonObject args)
    {
        if (cmd == "showWindow") { ShowWindow(); return; }
        if (cmd == "showOptions") { ShowOptions(); return; }
        if (cmd == "copyLiveLinkFromTray") { ShowRecorder(); return; }
        if (cmd == "reviewDelete")
        {
            ShowWindow();
            Post(J.Obj(("type", "result"), ("dialog", J.Obj(("kind", "deleteGame"), ("game", args["game"]?.DeepClone())))));
            return;
        }
        if (demo) { Post(J.Obj(("type", "command"), ("cmd", cmd), ("args", args.DeepClone()))); return; }   // the page's made-up recorder runs it
        JsonObject? result;
        try { result = backend!.Handle(cmd, args); }
        catch (Exception e) { result = J.Obj(("toast", J.Toast("warn", "Could not complete that action", e.Message))); }
        lastStateJson = ""; PushState();
        Post(J.Obj(("type", "result"), ("cmd", cmd), ("dialog", result?["dialog"]?.DeepClone()), ("toast", result?["toast"]?.DeepClone())));
        if (result?["toast"] is JsonObject t && (!Visible || WindowState == FormWindowState.Minimized)) ShowToast(t);
        if (result?["dialog"] is not null && !Visible) ShowWindow();
    }

    void PushState()
    {
        if (backend is null || !pageReady) return;
        var state = backend.BuildState();
        var json = state.ToJsonString();
        if (json == lastStateJson) return;
        lastStateJson = json; lastState = state;
        Post(J.Obj(("type", "state"), ("state", state)));
        var kind = state["status"]?["kind"]?.GetValue<string>() ?? "waiting";
        bool problem = (state["checks"] as JsonArray)?.Any(c => c?["level"]?.GetValue<string>() is "warn" or "bad") == true;
        SetTray(kind switch { "recording" => problem ? "attention" : "recording", "sending" => "sending", "unsupported" or "attention" => "attention", "paused" => "paused", _ => "waiting" },
            "FFACiv Recorder · " + state["status"]?["title"]?.GetValue<string>());
    }

    void Post(JsonObject msg) { if (view.CoreWebView2 is not null) view.CoreWebView2.PostWebMessageAsJson(msg.ToJsonString()); }

    void SetTray(string kind, string tip)
    {
        tray.Text = tip.Length > 127 ? tip[..127] : tip;
        statusItem.Text = tip.Replace("FFACiv Recorder · ", "");
        if (kind == trayKind) return;
        trayKind = kind;
        tray.Icon = TrayIcons.Get(kind, SystemInformation.SmallIconSize.Width * DeviceDpi / 96 is var px && px > 16 ? 32 : 16);
        Icon = TrayIcons.Get(kind, 32);
    }

    void UpdateMenu()
    {
        bool hasGame = demo || lastState?["current"] is JsonObject;
        pauseItem.Enabled = hasGame;
        pauseItem.Checked = lastState?["current"]?["paused"]?.GetValue<bool>() == true;
        var stream = lastState?["livestream"];
        bool active = stream?["active"]?.GetValue<bool>() == true;
        linkItem.Enabled = active || stream?["canStart"]?.GetValue<bool>() == true;
        linkItem.ToolTipText = stream?["reason"]?.GetValue<string>() ?? "Share the game currently being recorded.";
        linkItem.Text = active ? "Your livestream…" : "Get livestream link…";
    }

    public void ShowToast(JsonObject t) => ToastWindow.Show(t, (cmd, args) => BeginInvokeSafe(() => Command(cmd, args)));

    void ShowRecorder()
    {
        ShowWindow();
        if (view.CoreWebView2 is not null) _ = view.CoreWebView2.ExecuteScriptAsync("App.go('home'); if(App.state?.livestream?.canStart) App.open('livestream')");
    }

    void ShowOptions()
    {
        ShowWindow();
        if (view.CoreWebView2 is not null) _ = view.CoreWebView2.ExecuteScriptAsync("App.go('settings')");
    }

    void ShowGuide()
    {
        ShowWindow();
        if (view.CoreWebView2 is not null) _ = view.CoreWebView2.ExecuteScriptAsync("App.setup(1)");
    }

    public void ShowWindow()
    {
        ShowInTaskbar = true;
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();   // only ever when the player asked for the window
    }

    static string Short(string why) => why.StartsWith("No game") ? "no game" : why.StartsWith("Live streaming is off") ? "streaming off" : why.StartsWith("The test server") ? "server not reachable" : why.StartsWith("Connecting") ? "connecting" : "not now";

    /// <summary>Ends the program: the window and the icon go at once, then the recorder writes and sends its last lines
    /// (a few seconds at most), then the process ends.</summary>
    void Quit()
    {
        if (quitting) return;
        quitting = true; poll.Stop(); tray.Visible = false; tray.Dispose(); Hide();
        try { backend?.Dispose(); } catch { }
        Application.Exit();
    }

    /// <summary>Shows the tray menu where the pictures can catch it (for --ui-shot).</summary>
    public void ShowMenuAt(Point p) { UpdateMenu(); menu.Show(p); }
}
