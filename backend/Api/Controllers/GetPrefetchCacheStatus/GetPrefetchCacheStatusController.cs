using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;

namespace NzbWebDAV.Api.Controllers.GetPrefetchCacheStatus;

[ApiController]
[Route("api/get-prefetch-cache-status")]
public class GetPrefetchCacheStatusController(DavDatabaseContext dbContext) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var items = await dbContext.PrefetchCacheItems
            .OrderByDescending(x => x.StartedAt)
            .ToListAsync(HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return Ok(new GetPrefetchCacheStatusResponse
        {
            Items = items.Select(x => new GetPrefetchCacheStatusResponse.PrefetchCacheStatusItem
            {
                Id = x.Id,
                DavItemId = x.DavItemId,
                SeriesName = x.SeriesName,
                SeasonNumber = x.SeasonNumber,
                EpisodeNumber = x.EpisodeNumber,
                Status = x.Status.ToString(),
                FileSize = x.FileSize,
                StartedAt = x.StartedAt.ToUnixTimeMilliseconds(),
                CompletedAt = x.CompletedAt?.ToUnixTimeMilliseconds(),
                LastAccessedAt = x.LastAccessedAt.ToUnixTimeMilliseconds(),
                FailureReason = x.FailureReason,
            }).ToList(),
        });
    }
}
