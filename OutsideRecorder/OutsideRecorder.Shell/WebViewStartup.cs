using System.Runtime.InteropServices;

namespace OutsideRecorderShell;

/// <summary>Retries transient browser-controller aborts, while a window closed during startup cancels quietly.</summary>
static class WebViewStartup
{
    public static async Task Initialize(Func<Task> initialize, Func<bool> closed, Func<Task>? retryDelay = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (closed()) throw new OperationCanceledException();
            try { await initialize(); return; }
            catch (Exception) when (closed()) { throw new OperationCanceledException(); }
            catch (COMException e) when (e.HResult == unchecked((int)0x80004004) && attempt < 2)
            {
                await (retryDelay?.Invoke() ?? Task.Delay(250));
            }
        }
    }
}
