using System.Text.Json.Serialization;

namespace NzbWebDAV.Api.Controllers.JellyfinWebhook;

/// <summary>
/// Jellyfin's Webhook plugin ("Send All Properties") sends many more fields than
/// this app reads, and its payload shape has changed across plugin versions before.
/// Every field here is nullable and unknown fields are ignored — never require a
/// field that isn't strictly needed, and never fail to deserialize on an unfamiliar
/// payload shape.
/// </summary>
public class JellyfinWebhookRequest
{
    [JsonPropertyName("NotificationType")]
    public string? NotificationType { get; set; }

    [JsonPropertyName("ItemType")]
    public string? ItemType { get; set; }

    [JsonPropertyName("ItemId")]
    public string? ItemId { get; set; }

    [JsonPropertyName("SeriesName")]
    public string? SeriesName { get; set; }

    [JsonPropertyName("SeasonNumber")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("EpisodeNumber")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("PlaybackPositionTicks")]
    public long? PlaybackPositionTicks { get; set; }

    [JsonPropertyName("RunTimeTicks")]
    public long? RunTimeTicks { get; set; }
}
