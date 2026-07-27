using System.Collections.Concurrent;
using NzbWebDAV.Api.Controllers.JellyfinWebhook;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.PrefetchCache;

/// <summary>
/// Implements the Jellyfin webhook's filtering pipeline: bail out silently on the first
/// check that fails, resolve the next episode only once watch-progress crosses the
/// configured threshold, and never let a resolution failure become anything other than
/// a debug-level log line — a stalled/misconfigured Sonarr must never look like a
/// delivery failure to Jellyfin's webhook plugin (which can auto-disable a destination
/// after repeated failures).
/// </summary>
public class JellyfinWebhookHandler(
    ConfigManager configManager,
    SonarrNextEpisodeResolver resolver,
    PrefetchCacheService prefetchCacheService,
    Func<DavDatabaseContext> contextFactory)
{
    private const int MaxHandledItemIds = 500;
    private readonly ConcurrentDictionary<string, byte> _handledItemIds = new();

    public async Task HandleAsync(JellyfinWebhookRequest request, CancellationToken ct)
    {
        if (!PassesFilter(request, configManager.GetCachePrefetchThresholdPercent())) return;
        if (request.ItemId != null && !TryMarkHandled(request.ItemId)) return;

        try
        {
            var next = await resolver
                .ResolveNextEpisode(request.SeriesName!, request.SeasonNumber!.Value, request.EpisodeNumber!.Value)
                .ConfigureAwait(false);
            if (next is null) return;

            await using var ctx = contextFactory();
            var dbClient = new DavDatabaseClient(ctx);
            var davItem = await OrganizedLinksUtil
                .TryResolveDavItem(next.Value.Path, configManager, dbClient)
                .ConfigureAwait(false);
            if (davItem is null)
            {
                Log.Debug("JellyfinWebhookHandler: resolved next-episode path {Path} did not map to a known dav-item",
                    next.Value.Path);
                return;
            }

            prefetchCacheService.Enqueue(davItem, request.SeriesName!, next.Value.SeasonNumber, next.Value.EpisodeNumber);
        }
        catch (Exception e)
        {
            // Any failure here (Sonarr unreachable, malformed response, etc.) is an
            // expected/no-op outcome for this feature, not a delivery failure.
            Log.Debug(e, "JellyfinWebhookHandler: failed to resolve/prefetch next episode for {SeriesName}",
                request.SeriesName);
        }
    }

    private bool TryMarkHandled(string itemId) => TryMarkHandled(_handledItemIds, itemId);

    /// <summary>
    /// Returns false (already handled) if this ItemId crossed the threshold before,
    /// so a webhook plugin resending PlaybackProgress every ~10s doesn't re-run
    /// resolution for the rest of the episode. Not durable and not precise by
    /// design — cleared wholesale once it grows past the cap.
    /// </summary>
    internal static bool TryMarkHandled(ConcurrentDictionary<string, byte> handledItemIds, string itemId)
    {
        if (handledItemIds.Count > MaxHandledItemIds) handledItemIds.Clear();
        return handledItemIds.TryAdd(itemId, 0);
    }

    /// <summary>
    /// The bail-in-order filtering sequence, independent of the resolver/prefetch/DB
    /// dependencies so it can be exercised directly: notification/item type, required
    /// fields present, tick presence with a non-zero runtime, then the watch-percent
    /// threshold.
    /// </summary>
    internal static bool PassesFilter(JellyfinWebhookRequest request, int thresholdPercent)
    {
        if (!string.Equals(request.NotificationType, "PlaybackProgress", StringComparison.Ordinal)) return false;
        if (!string.Equals(request.ItemType, "Episode", StringComparison.Ordinal)) return false;
        if (request.SeriesName is null || request.SeasonNumber is null || request.EpisodeNumber is null) return false;
        if (request.PlaybackPositionTicks is null || request.RunTimeTicks is not > 0) return false;

        var percentWatched = (double)request.PlaybackPositionTicks.Value / request.RunTimeTicks.Value * 100;
        return percentWatched >= thresholdPercent;
    }
}
