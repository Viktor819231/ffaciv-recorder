using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.WinForms;

namespace OutsideRecorderShell;

/// <summary>
/// One pop-up in the bottom-right corner: ui/toast.html in a small window that is never activated (no focus taken from
/// Civ), stays on top, and stacks above the others. It closes itself when its time is up (the page counts, hover pauses).
/// </summary>
sealed class ToastWindow : Form
{
    static readonly List<ToastWindow> open = [];
    static readonly object openGate = new();
    static readonly Lazy<Task<Control>> dispatcher = new(StartDispatcher);
    readonly System.Windows.Forms.Timer expiry = new();
    readonly Button dismiss = new() { Text = "×", AccessibleName = "Dismiss notification", TabStop = true,
        FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = WebUi.Background };
    readonly WebView2 view = new() { Dock = DockStyle.Fill };
    readonly JsonObject toast;
    readonly Action<string, JsonObject> onCommand;
    volatile int contentHeight;

    ToastWindow(JsonObject toast, Action<string, JsonObject> onCommand)
    {
        this.toast = toast; this.onCommand = onCommand;
        Text = "FFACiv Recorder notification";
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = WebUi.Background; AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(view);
        dismiss.FlatAppearance.BorderSize = 0;
        dismiss.Click += (_, _) => Dismiss();
        Controls.Add(dismiss);
        dismiss.BringToFront();
        Resize += (_, _) => { dismiss.Size = Scale(new Size(28, 28)); dismiss.Location = new Point(ClientSize.Width - dismiss.Width - Scale(new Size(8, 0)).Width, Scale(new Size(0, 8)).Height); };
        // A native watchdog is independent of the web renderer, including its hover-paused timer.
        expiry.Interval = Math.Clamp(toast["timeout"]?.GetValue<int>() ?? 12000, 1000, 60000);
        expiry.Tick += (_, _) => Dismiss();
        Shown += (_, _) => expiry.Start();
        Size = Scale(new Size(360, 96));
        Location = new Point(-32000, -32000);   // shown off screen until the page says how tall it is
        FormClosed += (_, _) => { expiry.Dispose(); lock (openGate) open.Remove(this); Restack(); };
        Shown += (_, _) => BeginInvoke(async () =>
        {
            try
            {
                if (IsDisposed || Disposing) return;
                await WebUi.Init(view, false);
                if (IsDisposed || Disposing) return;
                view.CoreWebView2.WebMessageReceived += (_, e) =>
                {
                    if (IsDisposed || Disposing) return;
                    try {
                        var m = JsonNode.Parse(e.WebMessageAsJson) as JsonObject;
                        var cmd = m?.Str("cmd"); var args = m?["args"] as JsonObject ?? [];
                        switch (cmd)
                        {
                            case "toast.ready": view.CoreWebView2.PostWebMessageAsJson(J.Obj(("type", "toast"), ("toast", toast.DeepClone())).ToJsonString()); break;
                            case "toast.size": contentHeight = args["height"]?.GetValue<int>() ?? 96; Height = Scale(new Size(0, contentHeight)).Height; Restack(); break;
                            case "toast.close": Dismiss(); break;
                            case "toast.action":
                                var action = args.Str("cmd") ?? "";
                                var actionArgs = (args["args"] as JsonObject)?.DeepClone() as JsonObject ?? [];
                                Dismiss();
                                onCommand(action, actionArgs);
                                break;
                            case "showWindow": onCommand("showWindow", []); break;
                        }
                    } catch (Exception error) { WebUi.LogStartupError(error); Dismiss(); }
                };
                view.CoreWebView2.Navigate($"https://{WebUi.Host}/toast.html");
            }
            catch (OperationCanceledException) when (IsDisposed || Disposing) { }
            catch (Exception e)
            {
                WebUi.LogStartupError(e);
                if (!IsDisposed && !Disposing) Close();
            }
        });
    }

    /// <summary>The newest pop-up and whether its page has laid out (for --ui-shot).</summary>
    public static ToastWindow? Newest { get { lock (openGate) return open.LastOrDefault(); } }
    public bool Placed => contentHeight > 0;
    public Task CapturePng(Stream to)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(async () => {
            try { await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, to); done.SetResult(); }
            catch (Exception e) { done.SetException(e); }
        });
        return done.Task;
    }

    void Dismiss()
    {
        if (IsDisposed || Disposing) return;
        expiry.Stop();
        try { Hide(); Close(); } catch (ObjectDisposedException) { }
    }

    static Task<Control> StartDispatcher()
    {
        var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try
            {
                var control = new Control();
                _ = control.Handle;
                ready.SetResult(control);
                Application.Run();
                control.Dispose();
            }
            catch (Exception e) { ready.TrySetException(e); WebUi.LogStartupError(e); }
        }) { IsBackground = true, Name = "Recorder notifications" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    /// <summary>Closes only notification windows when the owning Recorder exits.</summary>
    public static async void CloseAll()
    {
        if (!dispatcher.IsValueCreated) return;
        try {
            var ui = await dispatcher.Value.ConfigureAwait(false);
            ui.BeginInvoke(() => { foreach (var toast in Snapshot()) toast.Dismiss(); Application.ExitThread(); });
        } catch (Exception e) { WebUi.LogStartupError(e); }
    }

    static ToastWindow[] Snapshot() { lock (openGate) return open.ToArray(); }

    Size Scale(Size s) => new((int)(s.Width * DeviceDpi / 96.0), (int)(s.Height * DeviceDpi / 96.0));

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x00000080 /*WS_EX_TOOLWINDOW*/ | 0x00000008 /*WS_EX_TOPMOST*/; return cp; }
    }

    /// <summary>Shows a pop-up; at most 3 at once (the oldest goes).</summary>
    public static async void Show(JsonObject toast, Action<string, JsonObject> onCommand)
    {
        var copy = (JsonObject)toast.DeepClone();
        try
        {
            var ui = await dispatcher.Value.ConfigureAwait(false);
            ui.BeginInvoke(() => {
                ToastWindow? t = null;
                try {
                    while (Snapshot().Length >= 3) Snapshot()[0].Dismiss();
                    t = new ToastWindow(copy, onCommand);
                    lock (openGate) open.Add(t);
                    t.Show();
                } catch (Exception error) {
                    WebUi.LogStartupError(error);
                    if (t is not null) { lock (openGate) open.Remove(t); t.Dismiss(); }
                }
            });
        }
        catch (Exception e) { WebUi.LogStartupError(e); }
    }

    static void Restack()
    {
        var area = Screen.PrimaryScreen!.WorkingArea;
        int y = area.Bottom - 12;
        var windows = Snapshot();
        for (int i = windows.Length - 1; i >= 0; i--)
        {
            var t = windows[i];
            if (t.IsDisposed || t.Disposing || t.contentHeight == 0) continue;
            y -= t.Height;
            t.Location = new Point(area.Right - t.Width - 12, Math.Max(area.Top, y));
            y -= 8;
        }
    }
}
