using System.Diagnostics;
using NzbWebDAV.Config;

namespace NzbWebDAV.Clients.Usenet.Bandwidth;

/// <summary>
/// Global token-bucket cap on real Usenet download bandwidth. Hooked in at
/// <see cref="MultiProviderNntpClient.WrapProviderStream"/> — the single point every
/// article-fetching path (single, batch, and pipelined BODY/ARTICLE) funnels a decoded
/// stream through before it's handed to a segment cache or a reader — so a segment-cache
/// hit never gets throttled (it never reaches this class) and every genuine wire read does.
///
/// Two independent buckets: a global one sized by <c>usenet.bandwidth-limit-mbps</c>, and
/// a Queue-lane-only one sized by the SAB-compatible <c>queue.speed-limit-kbps</c>. Streaming
/// only ever draws from the global bucket; Queue draws from both, so an Arr-set speed limit
/// can restrict background queue downloads without ever throttling interactive playback.
///
/// Both limits are re-read from <see cref="ConfigManager"/> on every poll slice (not just once
/// per call), so a limit changed while a caller is already waiting takes effect within one
/// slice rather than being computed once against a rate that may no longer apply.
/// </summary>
public class UsenetBandwidthLimiter(ConfigManager configManager)
{
    // Settable for tests so a mid-wait limit change doesn't need a full real second to prove.
    internal static TimeSpan PollSlice { get; set; } = TimeSpan.FromMilliseconds(50);

    private readonly Lock _lock = new();
    private double _globalBucketBytes;
    private double _queueBucketBytes;
    private long _lastRefillTimestamp = Stopwatch.GetTimestamp();
    private int _streamingWaiters;

    /// <summary>
    /// Blocks until <paramref name="bytes"/> worth of bandwidth has been "spent" for
    /// <paramref name="lane"/>, pacing the caller to the currently configured rate.
    /// Returns immediately (no lock, no allocation) when no limit is configured for this lane.
    /// </summary>
    public async Task ConsumeAsync(long bytes, BandwidthLane lane, CancellationToken cancellationToken)
    {
        if (bytes <= 0) return;

        if (lane == BandwidthLane.Streaming) Interlocked.Increment(ref _streamingWaiters);
        try
        {
            double remaining = bytes;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var globalRate = GetGlobalRateBytesPerSecond();
                var queueRate = lane == BandwidthLane.Queue ? GetQueueRateBytesPerSecond() : null;
                if (globalRate is null && queueRate is null) return; // fully unlimited for this lane

                double granted;
                lock (_lock)
                {
                    Refill(globalRate, queueRate);
                    granted = ComputeGrant(remaining, lane, globalRate, queueRate);
                    _globalBucketBytes -= granted;
                    if (lane == BandwidthLane.Queue && queueRate is not null)
                        _queueBucketBytes -= granted;
                }

                remaining -= granted;
                if (remaining <= 0) return;
                await Task.Delay(PollSlice, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (lane == BandwidthLane.Streaming) Interlocked.Decrement(ref _streamingWaiters);
        }
    }

    private double ComputeGrant(double requested, BandwidthLane lane, double? globalRate, double? queueRate)
    {
        var globalAllowance = globalRate is null
            ? requested
            : lane == BandwidthLane.Streaming || Volatile.Read(ref _streamingWaiters) == 0
                ? _globalBucketBytes
                : _globalBucketBytes * (1 - GetStreamingReserveFraction());

        var allowance = Math.Max(0, Math.Min(requested, globalAllowance));
        if (lane == BandwidthLane.Queue && queueRate is not null)
            allowance = Math.Max(0, Math.Min(allowance, _queueBucketBytes));

        return allowance;
    }

    private void Refill(double? globalRate, double? queueRate)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;
        if (elapsedSeconds <= 0) return;

        if (globalRate is { } gRate)
            _globalBucketBytes = Math.Min(gRate, _globalBucketBytes + elapsedSeconds * gRate);
        if (queueRate is { } qRate)
            _queueBucketBytes = Math.Min(qRate, _queueBucketBytes + elapsedSeconds * qRate);
    }

    private double GetStreamingReserveFraction() =>
        Math.Clamp(configManager.GetUsenetBandwidthStreamingReservePercent() / 100.0, 0, 1);

    private double? GetGlobalRateBytesPerSecond()
    {
        var mbps = configManager.GetUsenetBandwidthLimitMbps();
        return mbps is { } value ? value * 1_000_000 / 8 : null;
    }

    private double? GetQueueRateBytesPerSecond()
    {
        var kbps = configManager.GetSabSpeedLimitKbps();
        return kbps > 0 ? kbps * 1000.0 / 8 : null;
    }
}
