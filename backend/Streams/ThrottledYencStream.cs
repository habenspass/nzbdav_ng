using NzbWebDAV.Clients.Usenet.Bandwidth;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

/// <summary>
/// Paces reads of a decoded yEnc body against <see cref="UsenetBandwidthLimiter"/>. The inner
/// stream still performs the real decode work; this class only delays returning control to the
/// caller once bytes have actually been decoded, so the limiter meters real bytes read from the
/// wire rather than bytes requested.
/// </summary>
public sealed class ThrottledYencStream(
    YencStream inner,
    UsenetBandwidthLimiter limiter,
    BandwidthLane lane) : YencStream(Null)
{
    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
        => inner.GetYencHeadersAsync(cancellationToken);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (n > 0)
            await limiter.ConsumeAsync(n, lane, cancellationToken).ConfigureAwait(false);
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
