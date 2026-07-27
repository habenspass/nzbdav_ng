using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;
using NzbWebDAV.Streams;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services.PrefetchCache;

/// <summary>
/// Downloads a resolved next-episode DavItem into the local prefetch cache. Requests
/// (from the Jellyfin webhook, the management page's manual trigger, or a "cache this
/// now" button) are queued on a channel and drained by a small bounded worker pool so
/// one runaway prefetch can't pile up unbounded background downloads.
/// </summary>
public class PrefetchCacheService(
    ConfigManager configManager,
    UsenetStreamingClient usenetClient,
    QueueManager queueManager,
    WebsocketManager websocketManager,
    LazyRarResolver lazyRarResolver,
    InFlightArticleBudget inFlightArticleBudget,
    Func<DavDatabaseContext> contextFactory)
{
    private const int WorkerCount = 2;
    private readonly Channel<PrefetchRequest> _channel = Channel.CreateUnbounded<PrefetchRequest>();

    public void Enqueue(DavItem davItem, string seriesName, int seasonNumber, int episodeNumber) =>
        _channel.Writer.TryWrite(new PrefetchRequest(davItem, seriesName, seasonNumber, episodeNumber));

    public void Start(CancellationToken ct)
    {
        for (var i = 0; i < WorkerCount; i++)
            _ = Task.Run(() => WorkerLoopAsync(ct), ct);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        await foreach (var request in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Warning(e, "Prefetch worker failed to process dav-item {DavItemId}", request.DavItem.Id);
            }
        }
    }

    private async Task ProcessAsync(PrefetchRequest request, CancellationToken ct)
    {
        await using var ctx = contextFactory();
        var dbClient = new DavDatabaseClient(ctx);

        var alreadyTracked = await ctx.PrefetchCacheItems.AnyAsync(x =>
            x.DavItemId == request.DavItem.Id &&
            (x.Status == PrefetchCacheItem.PrefetchCacheStatus.InProgress
             || x.Status == PrefetchCacheItem.PrefetchCacheStatus.Complete), ct).ConfigureAwait(false);
        if (alreadyTracked) return;

        var cacheDir = configManager.GetCacheDir();
        Directory.CreateDirectory(cacheDir);
        var cacheFilePath = Path.Combine(cacheDir, request.DavItem.Id.ToString());

        var now = DateTimeOffset.UtcNow;
        var item = new PrefetchCacheItem
        {
            Id = GuidUtil.GenerateSecureGuid(),
            DavItemId = request.DavItem.Id,
            SeriesName = request.SeriesName,
            SeasonNumber = request.SeasonNumber,
            EpisodeNumber = request.EpisodeNumber,
            CacheFilePath = cacheFilePath,
            Status = PrefetchCacheItem.PrefetchCacheStatus.InProgress,
            StartedAt = now,
            LastAccessedAt = now,
        };
        ctx.PrefetchCacheItems.Add(item);
        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            if (DatabaseStoreItemFactory.Create(
                    request.DavItem, new DefaultHttpContext(), dbClient, configManager, usenetClient,
                    queueManager, websocketManager, lazyRarResolver, inFlightArticleBudget)
                is not BaseStoreStreamFile streamFile)
            {
                throw new InvalidOperationException($"Dav-item {request.DavItem.Id} is not a streamable file.");
            }

            await using var source = await streamFile.GetStreamForBackgroundUseAsync(ct).ConfigureAwait(false);
            await using var destination = new FileStream(
                cacheFilePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 81920, useAsync: true);
            await source.CopyToAsync(destination, ct).ConfigureAwait(false);

            item.Status = PrefetchCacheItem.PrefetchCacheStatus.Complete;
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.FileSize = destination.Length;
        }
        catch (Exception e)
        {
            item.Status = PrefetchCacheItem.PrefetchCacheStatus.Failed;
            item.FailureReason = e.Message;
            TryDeletePartialFile(cacheFilePath);
        }

        await ctx.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException e)
        {
            Log.Debug(e, "Failed to clean up partial prefetch file {Path}; the eviction sweep will retry", path);
        }
    }

    private sealed record PrefetchRequest(DavItem DavItem, string SeriesName, int SeasonNumber, int EpisodeNumber);
}
