using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;
using Serilog;

namespace NzbWebDAV.Services.PrefetchCache;

/// <summary>
/// The WebDAV read-path side of the prefetch cache: given a DavItem being read,
/// check whether a completed prefetch already sits on local disk and, if so,
/// serve it instead of falling through to a fresh Usenet fetch. Kept separate
/// from <see cref="PrefetchCacheService"/> (which owns downloading/eviction) so
/// the hot read path only needs what every <c>DatabaseStore*File</c> already has:
/// a scoped <see cref="DavDatabaseClient"/>.
/// </summary>
public static class PrefetchCacheReadPath
{
    // Ref-counts of cache files currently being streamed to a player, keyed by
    // DavItemId. LastAccessedAt is only bumped when a stream opens — during a
    // long single playback it would otherwise go stale and the free-space sweep
    // could evict the very file being watched. Consulting this set lets the sweep
    // skip anything with an open reader, regardless of how stale its timestamp is.
    private static readonly ConcurrentDictionary<Guid, int> OpenReadCounts = new();

    public static bool IsOpen(Guid davItemId) => OpenReadCounts.GetValueOrDefault(davItemId) > 0;

    /// <summary>
    /// Returns a stream over the cached file if a `Complete` prefetch exists and its
    /// file is still readable, else null so the caller falls through to Usenet.
    /// A cache-file read failure here (e.g. an eviction race) is not surfaced as an
    /// error — it is always safe to fall back to the normal streaming path.
    /// </summary>
    public static async Task<Stream?> TryOpenCachedStreamAsync(
        Guid davItemId, DavDatabaseClient dbClient, CancellationToken ct)
    {
        var item = await dbClient.Ctx.PrefetchCacheItems
            .FirstOrDefaultAsync(x => x.DavItemId == davItemId
                                      && x.Status == PrefetchCacheItem.PrefetchCacheStatus.Complete, ct)
            .ConfigureAwait(false);
        if (item is null) return null;

        FileStream fileStream;
        try
        {
            fileStream = new FileStream(
                item.CacheFilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Debug(e, "Prefetch cache file for dav-item {DavItemId} is missing or unreadable; falling back to Usenet",
                davItemId);
            return null;
        }

        item.LastAccessedAt = DateTimeOffset.UtcNow;
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        OpenReadCounts.AddOrUpdate(davItemId, 1, (_, count) => count + 1);
        return new DisposableCallbackStream(fileStream, onDispose: () => Release(davItemId));
    }

    private static void Release(Guid davItemId) =>
        OpenReadCounts.AddOrUpdate(davItemId, 0, (_, count) => Math.Max(0, count - 1));
}
