namespace NzbWebDAV.Api.Controllers.GetPrefetchCacheStatus;

public class GetPrefetchCacheStatusResponse : BaseApiResponse
{
    public List<PrefetchCacheStatusItem> Items { get; init; } = [];

    public class PrefetchCacheStatusItem
    {
        public required Guid Id { get; init; }
        public required Guid DavItemId { get; init; }
        public required string SeriesName { get; init; }
        public required int SeasonNumber { get; init; }
        public required int EpisodeNumber { get; init; }
        public required string Status { get; init; }
        public long? FileSize { get; init; }
        public required long StartedAt { get; init; }
        public long? CompletedAt { get; init; }
        public required long LastAccessedAt { get; init; }
        public string? FailureReason { get; init; }
    }
}
