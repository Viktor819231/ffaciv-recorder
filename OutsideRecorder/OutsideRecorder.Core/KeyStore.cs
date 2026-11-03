using System.Runtime.InteropServices;
using System.Text;

namespace OutsideRecorderCore;

/// <summary>
/// The install key, kept protected for this Windows user (DPAPI, CryptProtectData with the user's own key): another
/// Windows user, or the file copied to another PC, cannot read it. No package needed: crypt32 is part of Windows.
/// </summary>
public static class KeyStore
{
    /// <summary>The text protected for the current Windows user, as base64.</summary>
    public static string Protect(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var input = Blob.Of(bytes);
        try
        {
            if (!CryptProtectData(ref input, "FFACiv Recorder install key", 0, 0, 0, 0x1 /* UI_FORBIDDEN */, out var output))
                throw new InvalidOperationException($"CryptProtectData failed ({Marshal.GetLastWin32Error()})");
            try { return Convert.ToBase64String(output.ToArray()); } finally { LocalFree(output.Data); }
        }
        finally { input.Free(); }
    }

    /// <summary>The text back (only for the same Windows user on the same PC), or null when it cannot be read.</summary>
    public static string? Unprotect(string base64)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); } catch { return null; }
        var input = Blob.Of(bytes);
        try
        {
            if (!CryptUnprotectData(ref input, 0, 0, 0, 0, 0x1, out var output)) return null;
            try { return Encoding.UTF8.GetString(output.ToArray()); } finally { LocalFree(output.Data); }
        }
        finally { input.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blob
    {
        public int Size;
        public nint Data;
        public static Blob Of(byte[] b) { var p = Marshal.AllocHGlobal(Math.Max(1, b.Length)); Marshal.Copy(b, 0, p, b.Length); return new Blob { Size = b.Length, Data = p }; }
        public readonly byte[] ToArray() { var r = new byte[Size]; Marshal.Copy(Data, r, 0, Size); return r; }
        public readonly void Free() => Marshal.FreeHGlobal(Data);
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, nint description, nint entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern nint LocalFree(nint p);
}
