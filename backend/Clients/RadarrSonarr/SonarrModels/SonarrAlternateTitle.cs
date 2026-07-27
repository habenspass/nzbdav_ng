using System.Text.Json.Serialization;

namespace NzbWebDAV.Clients.RadarrSonarr.SonarrModels;

public class SonarrAlternateTitle
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }
}
