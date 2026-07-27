using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services.PrefetchCache;

/// <summary>
/// Enforces the prefetch cache's retention policy on a short fixed interval while the
/// feature is enabled: stale/failed in-progress attempts first, then max age, then max
/// episode count (LRU), then a free-disk-space floor that can evict past the count cap.
/// <see cref="EvictItemAsync"/> is the same code path used for manual eviction from the
/// management page, so there is only ever one way an entry actually gets removed.
/// </summary>
public class PrefetchCacheEvictionService(
    ConfigManager configManager,
    Func<DavDatabaseContext> contextFactory) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StaleInProgressCutoff = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configManager.IsCachePrefetchEnabled())
                    await RunSweepAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Prefetch cache eviction sweep failed");
                await Task.Delay(SweepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task RunSweepAsync(CancellationToken ct)
    {
        await using var ctx = contextFactory();

        // 1. stale in-progress / failed attempts don't count as successfully cached
        // and shouldn't block the policies below.
        var staleCutoff = DateTimeOffset.UtcNow - StaleInProgressCutoff;
        var staleOrFailed = await ctx.PrefetchCacheItems
            .Where(x => x.StartedAt < staleCutoff && x.Status != PrefetchCacheItem.PrefetchCacheStatus.Complete)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in staleOrFailed)
            await EvictAsync(ctx, item, ct).ConfigureAwait(false);

        // 2. max cache age
        var maxAgeCutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(configManager.GetCacheMaxCacheTimeHours());
        var expired = await ctx.PrefetchCacheItems
            .Where(x => x.Status == PrefetchCacheItem.PrefetchCacheStatus.Complete && x.CompletedAt < maxAgeCutoff)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in expired.Where(x => !PrefetchCacheReadPath.IsOpen(x.DavItemId)))
            await EvictAsync(ctx, item, ct).ConfigureAwait(false);

        // 3. max cache episode count, oldest-by-last-access first
        var maxEpisodes = configManager.GetCacheMaxCacheEpisodes();
        var complete = await ctx.PrefetchCacheItems
            .Where(x => x.Status == PrefetchCacheItem.PrefetchCacheStatus.Complete)
            .OrderBy(x => x.LastAccessedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var overflowCount = complete.Count - maxEpisodes;
        for (var i = 0; i < overflowCount && i < complete.Count; i++)
        {
            if (PrefetchCacheReadPath.IsOpen(complete[i].DavItemId)) continue;
            await EvictAsync(ctx, complete[i], ct).ConfigureAwait(false);
        }

        // 4. minimum free disk space, takes priority over the count limit above
        var minFreeBytes = (long)configManager.GetCacheMinFreeSpaceGb() * 1024 * 1024 * 1024;
        var cacheDir = configManager.GetCacheDir();
        Directory.CreateDirectory(cacheDir);
        var remaining = await ctx.PrefetchCacheItems
            .Where(x => x.Status == PrefetchCacheItem.PrefetchCacheStatus.Complete)
            .OrderBy(x => x.LastAccessedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in remaining)
        {
            if (GetFreeBytes(cacheDir) >= minFreeBytes) break;
            if (PrefetchCacheReadPath.IsOpen(item.DavItemId)) continue;
            await EvictAsync(ctx, item, ct).ConfigureAwait(false);
        }
    }

    public async Task<bool> EvictItemAsync(Guid id, CancellationToken ct)
    {
        await using var ctx = contextFactory();
        var item = await ctx.PrefetchCacheItems.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (item is null) return false;
        await EvictAsync(ctx, item, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task EvictAsync(DavDatabaseContext ctx, PrefetchCacheItem item, CancellationToken ct)
    {
        ctx.PrefetchCacheItems.Remove(item);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
        TryDeleteFile(item.CacheFilePath);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e)
        {
            Log.Debug(e, "Failed to delete evicted prefetch cache file {Path}", path);
        }
    }

    private static long GetFreeBytes(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path)) ?? path).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Free space is unknowable (e.g. path not yet mounted); don't evict on this basis.
            return long.MaxValue;
        }
    }
}
