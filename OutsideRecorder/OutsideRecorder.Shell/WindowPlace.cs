namespace OutsideRecorderShell;

/// <summary>Remembers where the window was and how big (window.txt next to the window's settings), so it opens the same way.</summary>
static class WindowPlace
{
    static string File => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FFACiv", "OutsideRecorder", "app", "window.txt");

    /// <summary>"x y width height maximized" in screen pixels; ignored when it would be off every screen.</summary>
    public static void Restore(Form f)
    {
        try
        {
            if (!System.IO.File.Exists(File)) return;
            var p = System.IO.File.ReadAllText(File).Split(' ').Select(int.Parse).ToArray();
            var r = new Rectangle(p[0], p[1], Math.Max(p[2], f.MinimumSize.Width), Math.Max(p[3], f.MinimumSize.Height));
            if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(r))) return;
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = r;
            if (p.Length > 4 && p[4] == 1) f.WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    public static void Save(Form f)
    {
        try
        {
            if (f.WindowState == FormWindowState.Minimized || !f.Visible) return;
            var r = f.WindowState == FormWindowState.Normal ? f.Bounds : f.RestoreBounds;
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, $"{r.X} {r.Y} {r.Width} {r.Height} {(f.WindowState == FormWindowState.Maximized ? 1 : 0)}");
        }
        catch { }
    }
}
