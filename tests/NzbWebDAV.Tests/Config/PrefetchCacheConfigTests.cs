using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Config;

public class PrefetchCacheConfigTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    public void IsCachePrefetchEnabled_DefaultsToFalse(string? value, bool expected)
    {
        var config = new ConfigManager();
        if (value is not null)
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CachePrefetchEnabled, ConfigValue = value }]);

        Assert.Equal(expected, config.IsCachePrefetchEnabled());
    }

    [Theory]
    [InlineData(null, 80)]
    [InlineData("", 80)]
    [InlineData("abc", 80)]
    [InlineData("50", 50)]
    [InlineData("150", 100)]
    [InlineData("-5", 1)]
    public void GetCachePrefetchThresholdPercent_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CachePrefetchThresholdPercent, ConfigValue = value }]);

        Assert.Equal(expected, config.GetCachePrefetchThresholdPercent());
    }

    [Theory]
    [InlineData(null, 48)]
    [InlineData("abc", 48)]
    [InlineData("100", 100)]
    public void GetCacheMaxCacheTimeHours_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheTimeHours, ConfigValue = value }]);

        Assert.Equal(expected, config.GetCacheMaxCacheTimeHours());
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData("abc", 5)]
    [InlineData("20", 20)]
    public void GetCacheMaxCacheEpisodes_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CacheMaxCacheEpisodes, ConfigValue = value }]);

        Assert.Equal(expected, config.GetCacheMaxCacheEpisodes());
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData("abc", 10)]
    [InlineData("0", 0)]
    [InlineData("50", 50)]
    public void GetCacheMinFreeSpaceGb_ClampsAndFallsBack(string? value, int expected)
    {
        var config = new ConfigManager();
        if (value is not null)
            config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CacheMinFreeSpaceGb, ConfigValue = value }]);

        Assert.Equal(expected, config.GetCacheMinFreeSpaceGb());
    }

    [Fact]
    public void GetCacheDir_DefaultsUnderConfigPath()
    {
        var config = new ConfigManager();
        var dir = config.GetCacheDir();
        Assert.EndsWith("prefetch-cache", dir);
    }

    [Fact]
    public void GetCacheDir_UsesConfiguredOverride()
    {
        var config = new ConfigManager();
        config.UpdateValues([new ConfigItem { ConfigName = ConfigKeys.CacheDir, ConfigValue = "/custom/cache" }]);

        Assert.Equal("/custom/cache", config.GetCacheDir());
    }

    [Fact]
    public void ValidateConfigItems_RejectsNonNumericPrefetchThreshold()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(
            [
                new ConfigItem { ConfigName = ConfigKeys.CachePrefetchThresholdPercent, ConfigValue = "nope" },
            ]));
        Assert.Contains("whole number", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateConfigItems_RejectsNonBooleanPrefetchEnabled()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConfigManager.ValidateConfigItems(
            [
                new ConfigItem { ConfigName = ConfigKeys.CachePrefetchEnabled, ConfigValue = "sortof" },
            ]));
        Assert.Contains("true", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
