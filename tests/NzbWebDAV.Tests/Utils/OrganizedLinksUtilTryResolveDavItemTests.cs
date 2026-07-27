using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.MigrationHelpers;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tests.Database;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

[Collection(nameof(ConfigPathCollection))]
public sealed class OrganizedLinksUtilTryResolveDavItemTests : IAsyncLifetime
{
    private const string MountDir = "/mnt/nzbdav";

    private readonly string _configRoot =
        Path.Combine(Path.GetTempPath(), $"nzbdav-resolve-cfg-{Guid.NewGuid():N}");
    private readonly string _libraryRoot =
        Path.Combine(Path.GetTempPath(), $"nzbdav-resolve-lib-{Guid.NewGuid():N}");
    private string? _previousConfigPath;
    private DbContextOptions<DavDatabaseContext> _options = null!;
    private DavDatabaseContext _context = null!;
    private DavDatabaseClient _dbClient = null!;
    private ConfigManager _configManager = null!;

    public async Task InitializeAsync()
    {
        _previousConfigPath = Environment.GetEnvironmentVariable("CONFIG_PATH");
        Directory.CreateDirectory(_configRoot);
        Directory.CreateDirectory(_libraryRoot);
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configRoot);

        _options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler())
            .ReplaceService<
                IMigrationsSqlGenerator,
                SqliteMigrationsSqlGenerator<SqliteMigrationsSqlGenerator>>()
            .Options;
        _context = new DavDatabaseContext(_options);
        await _context.Database.MigrateAsync();
        _dbClient = new DavDatabaseClient(_context);

        _configManager = new ConfigManager();
        _configManager.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.RcloneMountDir, ConfigValue = MountDir }]);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        Environment.SetEnvironmentVariable("CONFIG_PATH", _previousConfigPath);
        try { Directory.Delete(_configRoot, recursive: true); } catch { /* best effort */ }
        try { Directory.Delete(_libraryRoot, recursive: true); } catch { /* best effort */ }
    }

    private async Task SeedDavItemAsync(Guid id)
    {
        _context.Items.Add(new DavItem
        {
            Id = id,
            IdPrefix = id.GetFiveLengthPrefix(),
            CreatedAt = DateTime.Now,
            ParentId = null,
            Name = "S02E06.mkv",
            FileSize = 12345,
            Type = DavItem.ItemType.UsenetFile,
            SubType = DavItem.ItemSubType.NzbFile,
            Path = "/content/S02E06.mkv",
        });
        await _context.SaveChangesAsync();
    }

    [Fact]
    public async Task TryResolveDavItem_SymlinkPointingToKnownDavItem_ResolvesIt()
    {
        var id = Guid.NewGuid();
        await SeedDavItemAsync(id);

        var symlinkPath = Path.Combine(_libraryRoot, "S02E06.mkv");
        File.CreateSymbolicLink(symlinkPath, $"{MountDir}/.ids/{id}");

        var davItem = await OrganizedLinksUtil.TryResolveDavItem(symlinkPath, _configManager, _dbClient);

        Assert.NotNull(davItem);
        Assert.Equal(id, davItem!.Id);
    }

    [Fact]
    public async Task TryResolveDavItem_StrmPointingToKnownDavItem_ResolvesIt()
    {
        var id = Guid.NewGuid();
        await SeedDavItemAsync(id);

        var strmPath = Path.Combine(_libraryRoot, "S02E06.strm");
        await File.WriteAllTextAsync(strmPath, $"http://localhost:3000/view/.ids/{id}.mkv");

        var davItem = await OrganizedLinksUtil.TryResolveDavItem(strmPath, _configManager, _dbClient);

        Assert.NotNull(davItem);
        Assert.Equal(id, davItem!.Id);
    }

    [Fact]
    public async Task TryResolveDavItem_GuidExtractedButNoMatchingDavItem_ReturnsNull()
    {
        var symlinkPath = Path.Combine(_libraryRoot, "Orphaned.mkv");
        File.CreateSymbolicLink(symlinkPath, $"{MountDir}/.ids/{Guid.NewGuid()}");

        var davItem = await OrganizedLinksUtil.TryResolveDavItem(symlinkPath, _configManager, _dbClient);

        Assert.Null(davItem);
    }

    [Fact]
    public async Task TryResolveDavItem_ForeignSymlinkOutsideMountDir_ReturnsNullWithoutTouchingDb()
    {
        var symlinkPath = Path.Combine(_libraryRoot, "HandMade.mkv");
        File.CreateSymbolicLink(symlinkPath, "/some/other/place/file.mkv");

        var davItem = await OrganizedLinksUtil.TryResolveDavItem(symlinkPath, _configManager, _dbClient);

        Assert.Null(davItem);
    }

    [Fact]
    public async Task TryResolveDavItem_PathDoesNotExistOnDisk_ReturnsNull()
    {
        var missingPath = Path.Combine(_libraryRoot, "DoesNotExist.mkv");

        var davItem = await OrganizedLinksUtil.TryResolveDavItem(missingPath, _configManager, _dbClient);

        Assert.Null(davItem);
    }
}
