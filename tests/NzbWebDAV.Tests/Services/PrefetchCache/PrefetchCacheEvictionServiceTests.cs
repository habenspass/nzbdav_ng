using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services.PrefetchCache;
using NzbWebDAV.Tests.Database;

namespace NzbWebDAV.Tests.Services.PrefetchCache;

[Collection(nameof(ConfigPathCollection))]
public sealed class PrefetchCacheEvictionServiceTests : IAsyncLifetime
{
    private readonly string _configRoot =
        Path.Combine(Path.GetTempPath(), $"nzbdav-evict-cfg-{Guid.NewGuid():N}");
    private readonly string _cacheDir =
        Path.Combine(Path.GetTempPath(), $"nzbdav-evict-cache-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private ConfigManager _configManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Directory.CreateDirectory(_cacheDir);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        await using (var context = new DavDatabaseContext(_options))
            await context.Database.MigrateAsync();

        _configManager = new ConfigManager();
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.CacheDir, ConfigValue = _cacheDir },
            new ConfigItem { ConfigName = ConfigKeys.CacheMinFreeSpaceGb, ConfigValue = "0" },
            new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheTimeHours, ConfigValue = "48" },
            new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheEpisodes, ConfigValue = "1000" },
        ]);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_cacheDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<PrefetchCacheItem> SeedAsync(
        PrefetchCacheItem.PrefetchCacheStatus status, DateTimeOffset startedAt, DateTimeOffset? completedAt,
        DateTimeOffset lastAccessedAt, bool withFile = true)
    {
        var filePath = Path.Combine(_cacheDir, $"{Guid.NewGuid():N}.mkv");
        if (withFile) await File.WriteAllTextAsync(filePath, "fake cached content");

        var item = new PrefetchCacheItem
        {
            Id = Guid.NewGuid(),
            DavItemId = Guid.NewGuid(),
            SeriesName = "Breaking Bad",
            SeasonNumber = 2,
            EpisodeNumber = 6,
            CacheFilePath = filePath,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = completedAt,
            LastAccessedAt = lastAccessedAt,
        };

        await using var context = new DavDatabaseContext(_options);
        context.PrefetchCacheItems.Add(item);
        await context.SaveChangesAsync();
        return item;
    }

    private async Task<List<PrefetchCacheItem>> GetAllAsync()
    {
        await using var context = new DavDatabaseContext(_options);
        return await context.PrefetchCacheItems.ToListAsync();
    }

    private PrefetchCacheEvictionService CreateService() =>
        new(_configManager, () => new DavDatabaseContext(_options));

    [Fact]
    public async Task RunSweepAsync_StaleInProgressPastCutoff_IsEvicted()
    {
        var now = DateTimeOffset.UtcNow;
        var stale = await SeedAsync(
            PrefetchCacheItem.PrefetchCacheStatus.InProgress, now - TimeSpan.FromHours(2), null, now);

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remaining = await GetAllAsync();
        Assert.DoesNotContain(remaining, x => x.Id == stale.Id);
        Assert.False(File.Exists(stale.CacheFilePath));
    }

    [Fact]
    public async Task RunSweepAsync_RecentInProgress_IsNotEvicted()
    {
        var now = DateTimeOffset.UtcNow;
        var recent = await SeedAsync(
            PrefetchCacheItem.PrefetchCacheStatus.InProgress, now - TimeSpan.FromMinutes(5), null, now);

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remaining = await GetAllAsync();
        Assert.Contains(remaining, x => x.Id == recent.Id);
    }

    [Fact]
    public async Task RunSweepAsync_CompleteItemOlderThanMaxAge_IsEvicted()
    {
        var now = DateTimeOffset.UtcNow;
        var expired = await SeedAsync(
            PrefetchCacheItem.PrefetchCacheStatus.Complete, now - TimeSpan.FromHours(100),
            now - TimeSpan.FromHours(100), now - TimeSpan.FromHours(100));

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remaining = await GetAllAsync();
        Assert.DoesNotContain(remaining, x => x.Id == expired.Id);
    }

    [Fact]
    public async Task RunSweepAsync_OverMaxEpisodeCount_EvictsLeastRecentlyAccessedFirst()
    {
        _configManager.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheEpisodes, ConfigValue = "1" }]);

        var now = DateTimeOffset.UtcNow;
        var oldest = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now - TimeSpan.FromHours(3));
        var middle = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now - TimeSpan.FromHours(2));
        var newest = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now - TimeSpan.FromHours(1));

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remaining = await GetAllAsync();
        Assert.DoesNotContain(remaining, x => x.Id == oldest.Id);
        Assert.DoesNotContain(remaining, x => x.Id == middle.Id);
        Assert.Contains(remaining, x => x.Id == newest.Id);
    }

    [Fact]
    public async Task RunSweepAsync_BelowFreeSpaceFloor_EvictsEvenBelowEpisodeCap()
    {
        // An unreasonably high floor guarantees "below the floor" deterministically,
        // without depending on the test machine's actual free disk space.
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.CacheMinFreeSpaceGb, ConfigValue = "999999999" },
            new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheEpisodes, ConfigValue = "1000" },
        ]);

        var now = DateTimeOffset.UtcNow;
        var item = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now);

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remaining = await GetAllAsync();
        Assert.DoesNotContain(remaining, x => x.Id == item.Id);
    }

    [Fact]
    public async Task RunSweepAsync_ItemCurrentlyBeingRead_IsNeverEvicted()
    {
        _configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.CacheMinFreeSpaceGb, ConfigValue = "999999999" },
        ]);

        var now = DateTimeOffset.UtcNow;
        var item = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now);

        await using var context = new DavDatabaseContext(_options);
        var dbClient = new DavDatabaseClient(context);
        // Opens the cache file, which marks the dav-item as "currently being read".
        await using var openStream = await PrefetchCacheReadPath
            .TryOpenCachedStreamAsync(item.DavItemId, dbClient, CancellationToken.None);
        Assert.NotNull(openStream);
        Assert.True(PrefetchCacheReadPath.IsOpen(item.DavItemId));

        var service = CreateService();
        await service.RunSweepAsync(CancellationToken.None);

        var remainingWhileOpen = await GetAllAsync();
        Assert.Contains(remainingWhileOpen, x => x.Id == item.Id);

        await openStream!.DisposeAsync();
        Assert.False(PrefetchCacheReadPath.IsOpen(item.DavItemId));

        await service.RunSweepAsync(CancellationToken.None);
        var remainingAfterClose = await GetAllAsync();
        Assert.DoesNotContain(remainingAfterClose, x => x.Id == item.Id);
    }

    [Fact]
    public async Task EvictItemAsync_SameCodePathAsSweep_RemovesRowAndFile()
    {
        var now = DateTimeOffset.UtcNow;
        var item = await SeedAsync(PrefetchCacheItem.PrefetchCacheStatus.Complete, now, now, now);
        Assert.True(File.Exists(item.CacheFilePath));

        var service = CreateService();
        var evicted = await service.EvictItemAsync(item.Id, CancellationToken.None);

        Assert.True(evicted);
        Assert.False(File.Exists(item.CacheFilePath));
        var remaining = await GetAllAsync();
        Assert.DoesNotContain(remaining, x => x.Id == item.Id);
    }

    [Fact]
    public async Task EvictItemAsync_UnknownId_ReturnsFalse()
    {
        var service = CreateService();
        var evicted = await service.EvictItemAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.False(evicted);
    }
}
