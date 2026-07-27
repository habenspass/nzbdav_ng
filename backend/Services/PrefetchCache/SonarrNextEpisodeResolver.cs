using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Services.PrefetchCache;

/// <summary>
/// Resolves the episode that follows a given (series, season, episode) across every
/// configured Sonarr instance, using Sonarr's own episode ordering rather than
/// season/episode-number arithmetic (which breaks on specials and season-count
/// mismatches between metadata providers).
/// </summary>
public class SonarrNextEpisodeResolver(ConfigManager configManager)
{
    public Task<NextEpisode?> ResolveNextEpisode(string seriesName, int currentSeasonNumber, int currentEpisodeNumber) =>
        ResolveNextEpisode(
            configManager.GetArrConfig().GetArrClients().OfType<SonarrClient>(),
            seriesName, currentSeasonNumber, currentEpisodeNumber);

    internal static async Task<NextEpisode?> ResolveNextEpisode(
        IEnumerable<SonarrClient> sonarrClients, string seriesName, int currentSeasonNumber, int currentEpisodeNumber)
    {
        foreach (var sonarrClient in sonarrClients)
        {
            var nextEpisode = await TryResolveFromInstance(sonarrClient, seriesName, currentSeasonNumber, currentEpisodeNumber)
                .ConfigureAwait(false);
            if (nextEpisode != null) return nextEpisode;
        }

        return null;
    }

    private static async Task<NextEpisode?> TryResolveFromInstance(
        SonarrClient sonarrClient, string seriesName, int currentSeasonNumber, int currentEpisodeNumber)
    {
        var series = await sonarrClient.FindSeriesByTitle(seriesName).ConfigureAwait(false);
        if (series == null)
        {
            Log.Debug("SonarrNextEpisodeResolver: no series match for {SeriesName} on {Host}",
                seriesName, sonarrClient.Host);
            return null;
        }

        var episodes = await sonarrClient.GetAllEpisodesForSeries(series.Id).ConfigureAwait(false);
        var nextEpisode = episodes
            .Where(e => IsAfter(e.SeasonNumber, e.EpisodeNumber, currentSeasonNumber, currentEpisodeNumber))
            .OrderBy(e => e.SeasonNumber)
            .ThenBy(e => e.EpisodeNumber)
            .FirstOrDefault();

        if (nextEpisode is not { HasFile: true })
        {
            Log.Debug("SonarrNextEpisodeResolver: no downloaded next episode after S{Season}E{Episode} for {SeriesName}",
                currentSeasonNumber, currentEpisodeNumber, seriesName);
            return null;
        }

        var episodeFile = await sonarrClient.GetEpisodeFile(nextEpisode.EpisodeFileId).ConfigureAwait(false);
        if (episodeFile.Path is null) return null;
        return new NextEpisode(episodeFile.Path, nextEpisode.SeasonNumber, nextEpisode.EpisodeNumber);
    }

    private static bool IsAfter(int season, int episode, int currentSeason, int currentEpisode) =>
        season > currentSeason || (season == currentSeason && episode > currentEpisode);

    public readonly record struct NextEpisode(string Path, int SeasonNumber, int EpisodeNumber);
}
