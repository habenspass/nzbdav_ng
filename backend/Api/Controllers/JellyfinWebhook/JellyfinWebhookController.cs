using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services.PrefetchCache;
using Serilog;

namespace NzbWebDAV.Api.Controllers.JellyfinWebhook;

/// <summary>
/// Receives Jellyfin's Webhook-plugin "Send All Properties" payloads. Auth is a
/// dedicated `?apikey=` query token — Jellyfin's plugin cannot send custom headers,
/// so this cannot reuse the app's normal API-key scheme (<see cref="ApiKeyValidator"/>),
/// which lives on <c>BaseApiController</c>; this controller intentionally does not
/// derive from it, and intentionally does not use <c>[ApiController]</c> either, since
/// that attribute auto-returns 400 on a model-binding failure — and a malformed body
/// must still yield 200 here. Only a bad/missing token ever returns a non-200 status:
/// every other failure (malformed payload, Sonarr unreachable, resolution miss) is
/// caught and swallowed so Jellyfin's webhook plugin — which can auto-disable a
/// destination after repeated delivery failures — never sees this as broken.
/// </summary>
[Route("api/jellyfin-webhook")]
public class JellyfinWebhookController(ConfigManager configManager, JellyfinWebhookHandler handler) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Handle(CancellationToken ct)
    {
        if (!Request.Query.TryGetValue("apikey", out var apikey)
            || !apikey.ToString().FixedTimeEquals(configManager.GetJellyfinWebhookToken()))
        {
            return Unauthorized(new BaseApiResponse { Status = false, Error = "API Key Incorrect" });
        }

        try
        {
            var request = await ParseRequestAsync(ct).ConfigureAwait(false);
            if (request != null)
                await handler.HandleAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Log.Debug(e, "JellyfinWebhookController: unhandled error processing webhook payload");
        }

        return Ok();
    }

    private async Task<JellyfinWebhookRequest?> ParseRequestAsync(CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<JellyfinWebhookRequest>(Request.Body, cancellationToken: ct)
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
