using System.Diagnostics;
using System.Net;

namespace OutsideRecorderCore;

/// <summary>A resource pause leaves queued files and acknowledgements untouched for a later pass.</summary>
public sealed class WorkDeferredException(string message) : HttpRequestException(message);

/// <summary>The site refused one request about one recording for good (a 400-range answer that is not about the whole
/// install or timing): that recording waits, the others go on.</summary>
public sealed class SiteRefusedException(string message, System.Net.HttpStatusCode status) : HttpRequestException(message, null, status);

/// <summary>Paces request bodies across all senders; counts body bytes handed to HTTP, including retries.</summary>
public sealed class UploadBudget
{
    readonly SemaphoreSlim gate = new(1, 1);
    long nextWrite;
    /// <summary>Combined request-body limit; zero disables pacing (counting remains enabled).</summary>
    public int BytesPerSecond { get; set; }
    public Func<int>? CurrentBytesPerSecond { get; set; }
    /// <summary>Checked before every bounded write so a newly busy game can stop an in-flight upload.</summary>
    public Func<bool> CanContinue { get; set; } = () => true;
    /// <summary>Receives body bytes successfully written to the HTTP transport, excluding protocol overhead.</summary>
    public Action<long>? Written { get; set; }

    /// <summary>Preserves the original content headers and streams through a bounded, paced writer.</summary>
    public HttpContent Wrap(HttpContent content) => new Body(content, this);

    async ValueTask WriteAsync(Stream target, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        while (!bytes.IsEmpty)
        {
            int size = Math.Min(32 * 1024, bytes.Length);
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!CanContinue()) throw new WorkDeferredException("Upload paused to leave resources for the game.");
                int rate = Math.Max(0, CurrentBytesPerSecond?.Invoke() ?? BytesPerSecond);
                if (rate > 0)
                {
                    long now = Stopwatch.GetTimestamp();
                    double seconds = (nextWrite - now) / (double)Stopwatch.Frequency;
                    // Short cancellable slices also observe Civ relaunches while waiting for bandwidth.
                    while (seconds > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(seconds, .25)), ct).ConfigureAwait(false);
                        if (!CanContinue()) throw new WorkDeferredException("Upload paused to leave resources for the game.");
                        now = Stopwatch.GetTimestamp();
                        seconds = (nextWrite - now) / (double)Stopwatch.Frequency;
                    }
                    nextWrite = Math.Max(now, nextWrite) + (long)(size * (double)Stopwatch.Frequency / rate);
                }
                else nextWrite = 0;
                await target.WriteAsync(bytes[..size], ct).ConfigureAwait(false);
                try { Written?.Invoke(size); } catch { /* Diagnostic failure must not break an upload. */ }
            }
            finally { gate.Release(); }
            bytes = bytes[size..];
        }
    }

    sealed class Body : HttpContent
    {
        readonly HttpContent inner;
        readonly UploadBudget owner;
        public Body(HttpContent inner, UploadBudget owner)
        {
            this.inner = inner; this.owner = owner;
            foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        protected override bool TryComputeLength(out long length)
        { length = inner.Headers.ContentLength ?? -1; return length >= 0; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var writer = new Writer(stream, owner);
            await inner.CopyToAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    sealed class Writer(Stream target, UploadBudget owner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => target.Flush();
        public override Task FlushAsync(CancellationToken ct) => target.FlushAsync(ct);
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => owner.WriteAsync(target, buffer, ct);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
