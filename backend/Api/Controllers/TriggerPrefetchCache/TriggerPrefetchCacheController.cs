using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Services.PrefetchCache;

namespace NzbWebDAV.Api.Controllers.TriggerPrefetchCache;

[ApiController]
[Route("api/trigger-prefetch-cache")]
public class TriggerPrefetchCacheController(
    DavDatabaseClient dbClient,
    PrefetchCacheService prefetchCacheService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var davItemIdParam = HttpContext.Request.Query["davItemId"].ToString();
        if (!Guid.TryParse(davItemIdParam, out var davItemId))
            throw new BadHttpRequestException("A valid `davItemId` query parameter is required.");

        var davItem = await dbClient.GetFileById(davItemId.ToString()).ConfigureAwait(false);
        if (davItem is null)
            return NotFound(new BaseApiResponse { Status = false, Error = "Dav item not found." });

        // A manual trigger has no webhook-resolved next-episode context, so season/episode
        // are unknown here (0/0) — the management page renders just the item's name in
        // that case instead of an SxxEyy label.
        prefetchCacheService.Enqueue(davItem, davItem.Name, 0, 0);
        return Ok(new BaseApiResponse { Status = true });
    }
}
