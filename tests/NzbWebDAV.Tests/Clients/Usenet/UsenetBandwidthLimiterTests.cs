using System.Diagnostics;
using NzbWebDAV.Clients.Usenet.Bandwidth;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public class UsenetBandwidthLimiterTests
{
    public UsenetBandwidthLimiterTests()
    {
        // Real time, but a short slice so "mid-wait" tests finish in well under a second.
        UsenetBandwidthLimiter.PollSlice = TimeSpan.FromMilliseconds(20);
    }

    [Fact]
    public async Task ConsumeAsync_LimitRaisedMidWait_ProceedsWithoutWaitingForStaleRate()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            // ~8 KB/s: a 100KB read takes ~12s at this rate if the limiter ever
            // computed a wait duration against it up front instead of re-checking live.
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "0.0625" },
        ]);
        var limiter = new UsenetBandwidthLimiter(configManager);

        var consumeTask = limiter.ConsumeAsync(100_000, BandwidthLane.Streaming, CancellationToken.None);
        await Task.Delay(100); // let it block on the low rate for a few poll slices

        Assert.False(consumeTask.IsCompleted);

        // Raise the limit so the remaining bytes clear almost instantly.
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "1000" },
        ]);

        var completed = await Task.WhenAny(consumeTask, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(consumeTask, completed);
    }

    [Fact]
    public async Task ConsumeAsync_NoLimitConfigured_ReturnsImmediatelyRegardlessOfSize()
    {
        var configManager = new ConfigManager();
        var limiter = new UsenetBandwidthLimiter(configManager);

        var stopwatch = Stopwatch.StartNew();
        await limiter.ConsumeAsync(1_000_000_000, BandwidthLane.Queue, CancellationToken.None);
        Assert.True(stopwatch.ElapsedMilliseconds < 100);
    }

    [Fact]
    public async Task ConsumeAsync_QueueSubLimit_NeverThrottlesStreamingLane()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            // Global cap generous; SAB queue-only cap tiny. Streaming must be unaffected.
            new ConfigItem { ConfigName = ConfigKeys.UsenetBandwidthLimitMbps, ConfigValue = "1000" },
            new ConfigItem { ConfigName = ConfigKeys.QueueSpeedLimitKbps, ConfigValue = "1" },
        ]);
        var limiter = new UsenetBandwidthLimiter(configManager);

        var stopwatch = Stopwatch.StartNew();
        await limiter.ConsumeAsync(10_000_000, BandwidthLane.Streaming, CancellationToken.None);
        Assert.True(stopwatch.ElapsedMilliseconds < 200,
            $"Streaming should be unaffected by the Queue-only SAB sub-limit, took {stopwatch.ElapsedMilliseconds}ms");
    }
}
