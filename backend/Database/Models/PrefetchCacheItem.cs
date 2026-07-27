namespace NzbWebDAV.Database.Models;

/// <summary>
/// Tracks a predictively-prefetched episode's local cache lifecycle. A dedicated
/// table (rather than filesystem metadata) because eviction needs LRU-by-last-
/// playback-access — not last-write-time — and a distinguishable failed state,
/// neither of which file mtimes/atimes can reliably provide.
/// </summary>
public class PrefetchCacheItem
{
    public Guid Id { get; set; }
    public Guid DavItemId { get; set; }
    public string SeriesName { get; set; } = null!;
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    public string CacheFilePath { get; set; } = null!;
    public long? FileSize { get; set; }
    public PrefetchCacheStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset LastAccessedAt { get; set; }
    public string? FailureReason { get; set; }

    public enum PrefetchCacheStatus
    {
        InProgress,
        Complete,
        Failed,
    }
}
