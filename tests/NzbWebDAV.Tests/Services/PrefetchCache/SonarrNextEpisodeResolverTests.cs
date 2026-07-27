using System.Net;
using System.Text;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Services.PrefetchCache;

namespace NzbWebDAV.Tests.Services.PrefetchCache;

public class SonarrNextEpisodeResolverTests
{
    [Fact]
    public async Task ResolveNextEpisode_NextEpisodeInSameSeason_ReturnsItsFilePath()
    {
        var client = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=1", JsonResponse(
                """
                [
                  {"id":10,"seriesId":1,"seasonNumber":2,"episodeNumber":5,"hasFile":true,"episodeFileId":100},
                  {"id":11,"seriesId":1,"seasonNumber":2,"episodeNumber":6,"hasFile":true,"episodeFileId":101}
                ]
                """)),
            ("GET /api/v3/episodefile/101", JsonResponse("""{"id":101,"seriesId":1,"path":"/tv/Breaking Bad/S02E06.mkv"}""")));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode([client], "Breaking Bad", 2, 5);

        Assert.NotNull(next);
        Assert.Equal("/tv/Breaking Bad/S02E06.mkv", next.Value.Path);
        Assert.Equal(2, next.Value.SeasonNumber);
        Assert.Equal(6, next.Value.EpisodeNumber);
    }

    [Fact]
    public async Task ResolveNextEpisode_CurrentIsLastEpisodeOfSeason_CrossesSeasonBoundary()
    {
        var client = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=1", JsonResponse(
                """
                [
                  {"id":10,"seriesId":1,"seasonNumber":1,"episodeNumber":7,"hasFile":true,"episodeFileId":100},
                  {"id":11,"seriesId":1,"seasonNumber":2,"episodeNumber":1,"hasFile":true,"episodeFileId":101}
                ]
                """)),
            ("GET /api/v3/episodefile/101", JsonResponse("""{"id":101,"seriesId":1,"path":"/tv/Breaking Bad/S02E01.mkv"}""")));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode([client], "Breaking Bad", 1, 7);

        Assert.NotNull(next);
        Assert.Equal("/tv/Breaking Bad/S02E01.mkv", next.Value.Path);
        Assert.Equal(2, next.Value.SeasonNumber);
        Assert.Equal(1, next.Value.EpisodeNumber);
    }

    [Fact]
    public async Task ResolveNextEpisode_SpecialsSeasonZero_NeverChosenOverLaterRegularSeason()
    {
        var client = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=1", JsonResponse(
                """
                [
                  {"id":9,"seriesId":1,"seasonNumber":0,"episodeNumber":1,"hasFile":true,"episodeFileId":99},
                  {"id":10,"seriesId":1,"seasonNumber":2,"episodeNumber":5,"hasFile":true,"episodeFileId":100},
                  {"id":11,"seriesId":1,"seasonNumber":2,"episodeNumber":6,"hasFile":true,"episodeFileId":101}
                ]
                """)),
            ("GET /api/v3/episodefile/101", JsonResponse("""{"id":101,"seriesId":1,"path":"/tv/Breaking Bad/S02E06.mkv"}""")));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode([client], "Breaking Bad", 2, 5);

        Assert.NotNull(next);
        Assert.Equal("/tv/Breaking Bad/S02E06.mkv", next.Value.Path);
    }

    [Fact]
    public async Task ResolveNextEpisode_NextEpisodeNotYetDownloaded_ReturnsNullWithoutSkippingAhead()
    {
        var client = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=1", JsonResponse(
                """
                [
                  {"id":10,"seriesId":1,"seasonNumber":2,"episodeNumber":5,"hasFile":true,"episodeFileId":100},
                  {"id":11,"seriesId":1,"seasonNumber":2,"episodeNumber":6,"hasFile":false,"episodeFileId":0},
                  {"id":12,"seriesId":1,"seasonNumber":2,"episodeNumber":7,"hasFile":true,"episodeFileId":102}
                ]
                """)));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode([client], "Breaking Bad", 2, 5);

        Assert.Null(next);
    }

    [Fact]
    public async Task ResolveNextEpisode_EndOfSeries_ReturnsNull()
    {
        var client = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=1", JsonResponse(
                """[{"id":10,"seriesId":1,"seasonNumber":5,"episodeNumber":16,"hasFile":true,"episodeFileId":100}]""")));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode([client], "Breaking Bad", 5, 16);

        Assert.Null(next);
    }

    [Fact]
    public async Task ResolveNextEpisode_NoSeriesMatchOnFirstInstance_FallsThroughToSecondInstance()
    {
        var noMatchClient = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":1,"title":"Some Other Show"}]""")));
        var matchClient = CreateClient(
            ("GET /api/v3/series", JsonResponse("""[{"id":7,"title":"Breaking Bad"}]""")),
            ("GET /api/v3/episode?seriesId=7", JsonResponse(
                """[{"id":10,"seriesId":7,"seasonNumber":1,"episodeNumber":2,"hasFile":true,"episodeFileId":200}]""")),
            ("GET /api/v3/episodefile/200", JsonResponse("""{"id":200,"seriesId":7,"path":"/tv/Breaking Bad/S01E02.mkv"}""")));

        var next = await SonarrNextEpisodeResolver.ResolveNextEpisode(
            [noMatchClient, matchClient], "Breaking Bad", 1, 1);

        Assert.NotNull(next);
        Assert.Equal("/tv/Breaking Bad/S01E02.mkv", next.Value.Path);
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private static SonarrClient CreateClient(params (string request, HttpResponseMessage response)[] responses)
    {
        var handler = new ResponseQueueHandler(responses
            .GroupBy(x => x.request)
            .ToDictionary(x => x.Key, x => new Queue<HttpResponseMessage>(x.Select(y => y.response))));
        return new TestSonarrClient(new HttpClient(handler));
    }

    private sealed class TestSonarrClient(HttpClient client) : SonarrClient("http://arr.test", "test-key")
    {
        protected override HttpClient Client => client;
    }

    private sealed class ResponseQueueHandler(
        Dictionary<string, Queue<HttpResponseMessage>> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            if (!responses.TryGetValue(key, out var queuedResponses) || !queuedResponses.TryDequeue(out var response))
                throw new InvalidOperationException($"Unexpected request: {key}");

            return Task.FromResult(response);
        }
    }
}
