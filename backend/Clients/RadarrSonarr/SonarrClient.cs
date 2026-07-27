using System.Net;
using System.Text.RegularExpressions;
using NzbWebDAV.Clients.RadarrSonarr.BaseModels;
using NzbWebDAV.Clients.RadarrSonarr.SonarrModels;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.RadarrSonarr;

public class SonarrClient(string host, string apiKey) : ArrClient(host, apiKey)
{
    private static readonly Dictionary<string, int> SeriesPathToSeriesIdCache = new();
    private static readonly Dictionary<string, int> SymlinkOrStrmToEpisodeFileIdCache = new();

    public Task<SonarrQueue> GetSonarrQueueAsync() =>
        Get<SonarrQueue>($"/queue?protocol=usenet&pageSize=5000");

    public Task<List<SonarrSeries>> GetAllSeries() =>
        Get<List<SonarrSeries>>($"/series");

    public Task<SonarrSeries> GetSeries(int seriesId) =>
        Get<SonarrSeries>($"/series/{seriesId}");

    private Task<SonarrSeries?> GetSeriesOrNull(int seriesId) =>
        GetOrNull<SonarrSeries>($"/series/{seriesId}");

    public Task<SonarrEpisodeFile> GetEpisodeFile(int episodeFileId) =>
        Get<SonarrEpisodeFile>($"/episodefile/{episodeFileId}");

    private Task<SonarrEpisodeFile?> GetEpisodeFileOrNull(int episodeFileId) =>
        GetOrNull<SonarrEpisodeFile>($"/episodefile/{episodeFileId}");

    public Task<List<SonarrEpisodeFile>> GetAllEpisodeFiles(int seriesId) =>
        Get<List<SonarrEpisodeFile>>($"/episodefile?seriesId={seriesId}");

    public Task<List<SonarrEpisode>> GetEpisodesFromEpisodeFileId(int episodeFileId) =>
        Get<List<SonarrEpisode>>($"/episode?episodeFileId={episodeFileId}");

    public Task<List<SonarrEpisode>> GetAllEpisodesForSeries(int seriesId) =>
        Get<List<SonarrEpisode>>($"/episode?seriesId={seriesId}");

    public Task<HttpStatusCode> DeleteEpisodeFile(int episodeFileId) =>
        Delete($"/episodefile/{episodeFileId}");

    public Task<ArrCommand> SearchEpisodesAsync(List<int> episodeIds) =>
        CommandAsync(new { name = "EpisodeSearch", episodeIds });

    /// <summary>
    /// Finds a series by name using progressively looser tiers: exact title,
    /// then title normalized (year-suffix/punctuation/whitespace stripped),
    /// then normalized match against Sonarr's own alternate/localized titles.
    /// Stops at the first tier with exactly one match; an ambiguous (&gt;1) or
    /// empty result at any tier is treated as "no match" rather than guessing.
    /// </summary>
    public async Task<SonarrSeries?> FindSeriesByTitle(string seriesName)
    {
        var allSeries = await GetAllSeries();

        var exactMatches = allSeries
            .Where(s => s.Title != null && s.Title.Equals(seriesName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count == 1) return exactMatches[0];
        if (exactMatches.Count > 1) return null;

        var normalizedName = NormalizeTitle(seriesName);
        var normalizedMatches = allSeries
            .Where(s => s.Title != null && NormalizeTitle(s.Title) == normalizedName)
            .ToList();
        if (normalizedMatches.Count == 1) return normalizedMatches[0];
        if (normalizedMatches.Count > 1) return null;

        var alternateTitleMatches = allSeries
            .Where(s => s.AlternateTitles?.Any(a => a.Title != null && NormalizeTitle(a.Title) == normalizedName) == true)
            .ToList();
        return alternateTitleMatches.Count == 1 ? alternateTitleMatches[0] : null;
    }

    private static string NormalizeTitle(string title)
    {
        var withoutYear = Regex.Replace(title, @"\s*\(\d{4}\)\s*$", "");
        var withoutPunctuation = Regex.Replace(withoutYear, @"[^\w\s]", "");
        var collapsedWhitespace = Regex.Replace(withoutPunctuation, @"\s+", " ").Trim();
        return collapsedWhitespace.ToLowerInvariant();
    }

    public override async Task<bool> RemoveAndSearch(string symlinkOrStrmPath)
    {
        // get episode-file-id and episode-ids
        var mediaIds = await GetMediaIds(symlinkOrStrmPath);
        if (mediaIds == null) return false;

        // delete the episode-file
        if (await DeleteEpisodeFile(mediaIds.Value.episodeFileId) != HttpStatusCode.OK)
            throw new Exception($"Failed to delete episode file `{symlinkOrStrmPath}` from sonarr instance `{Host}`.");

        // trigger a new search for each episode
        await SearchEpisodesAsync(mediaIds.Value.episodeIds);
        return true;
    }

    private async Task<(int episodeFileId, List<int> episodeIds)?> GetMediaIds(string symlinkOrStrmPath)
    {
        // get episode-file-id
        var episodeFileId = await GetEpisodeFileId(symlinkOrStrmPath);
        if (episodeFileId == null) return null;

        // get episode-ids
        var episodes = await GetEpisodesFromEpisodeFileId(episodeFileId.Value);
        var episodeIds = episodes.Select(x => x.Id).ToList();
        if (episodeIds.Count == 0) return null;

        // return
        return (episodeFileId.Value, episodeIds);
    }

    private async Task<int?> GetEpisodeFileId(string symlinkOrStrmPath)
    {
        // if episode-file-id is found in the cache, verify it and return it
        if (SymlinkOrStrmToEpisodeFileIdCache.TryGetValue(symlinkOrStrmPath, out var episodeFileId))
        {
            var episodeFile = await GetEpisodeFileOrNull(episodeFileId);
            if (episodeFile?.Path == symlinkOrStrmPath) return episodeFileId;
            SymlinkOrStrmToEpisodeFileIdCache.Remove(symlinkOrStrmPath);
        }

        // otherwise, find the series-id
        var seriesId = await GetSeriesId(symlinkOrStrmPath);
        if (seriesId == null) return null;

        // then use it to find all episode-files and repopulate the cache
        int? result = null;
        foreach (var episodeFile in await GetAllEpisodeFiles(seriesId.Value))
        {
            SymlinkOrStrmToEpisodeFileIdCache[episodeFile.Path!] = episodeFile.Id;
            if (episodeFile.Path == symlinkOrStrmPath)
                result = episodeFile.Id;
        }

        // return the found episode-file-id
        return result;
    }

    private async Task<int?> GetSeriesId(string symlinkOrStrmPath)
    {
        // get series-id from cache
        var cachedSeriesPath = PathUtil.GetAllParentDirectories(symlinkOrStrmPath)
            .Where(x => SeriesPathToSeriesIdCache.ContainsKey(x))
            .FirstOrDefault();

        // if found, verify and return it
        if (cachedSeriesPath != null)
        {
            var cachedSeriesId = SeriesPathToSeriesIdCache[cachedSeriesPath];
            var series = await GetSeriesOrNull(cachedSeriesId);
            if (series?.Path != null && symlinkOrStrmPath.StartsWith(series.Path))
                return cachedSeriesId;
            SeriesPathToSeriesIdCache.Remove(cachedSeriesPath);
        }

        // otherwise, fetch all series and repopulate the cache
        int? result = null;
        foreach (var series in await GetAllSeries())
        {
            SeriesPathToSeriesIdCache[series.Path!] = series.Id;
            if (symlinkOrStrmPath.StartsWith(series.Path!))
                result = series.Id;
        }

        // return the found series-id
        return result;
    }
}
