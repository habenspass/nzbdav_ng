using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Ticks once per second to publish the current prefetch cache contents (in-progress,
/// complete, and recently-failed entries) over the websocket, so the management page
/// updates live without polling. Sends nothing when nothing has changed.
/// </summary>
public class PrefetchCacheBroadcaster(
    ConfigManager configManager,
    WebsocketManager websocketManager,
    Func<DavDatabaseContext> contextFactory
) : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private string? _lastPayload;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
                if (configManager.IsCachePrefetchEnabled())
                    await BroadcastTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (SigtermUtil.IsSigtermTriggered())
            {
                return;
            }
            catch (Exception e)
            {
                Log.Debug(e, "PrefetchCacheBroadcaster tick failed");
            }
        }
    }

    private async Task BroadcastTickAsync(CancellationToken ct)
    {
        await using var ctx = contextFactory();
        var items = await ctx.PrefetchCacheItems
            .OrderByDescending(x => x.StartedAt)
            .ToListAsync(ct).ConfigureAwait(false);

        var snapshot = new
        {
            items = items.Select(x => new
            {
                id = x.Id,
                davItemId = x.DavItemId,
                seriesName = x.SeriesName,
                seasonNumber = x.SeasonNumber,
                episodeNumber = x.EpisodeNumber,
                status = x.Status.ToString(),
                fileSize = x.FileSize,
                startedAt = x.StartedAt.ToUnixTimeMilliseconds(),
                completedAt = x.CompletedAt?.ToUnixTimeMilliseconds(),
                lastAccessedAt = x.LastAccessedAt.ToUnixTimeMilliseconds(),
                failureReason = x.FailureReason,
            }).ToList()
        };

        var payload = JsonSerializer.Serialize(snapshot, JsonOptions);
        if (payload == _lastPayload) return;
        _lastPayload = payload;
        await websocketManager.SendMessage(WebsocketTopic.PrefetchCacheStatus, payload).ConfigureAwait(false);
    }
}
