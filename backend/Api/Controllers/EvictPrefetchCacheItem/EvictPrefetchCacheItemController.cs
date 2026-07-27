using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Services.PrefetchCache;

namespace NzbWebDAV.Api.Controllers.EvictPrefetchCacheItem;

[ApiController]
[Route("api/evict-prefetch-cache-item")]
public class EvictPrefetchCacheItemController(PrefetchCacheEvictionService evictionService) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var idParam = HttpContext.Request.Query["id"].ToString();
        if (!Guid.TryParse(idParam, out var id))
            throw new BadHttpRequestException("A valid `id` query parameter is required.");

        var evicted = await evictionService.EvictItemAsync(id, HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new BaseApiResponse
        {
            Status = evicted,
            Error = evicted ? null : "Prefetch cache item not found.",
        });
    }
}
