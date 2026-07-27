using System.Collections.Concurrent;
using NzbWebDAV.Api.Controllers.JellyfinWebhook;
using NzbWebDAV.Services.PrefetchCache;

namespace NzbWebDAV.Tests.Services.PrefetchCache;

public class JellyfinWebhookHandlerFilterTests
{
    private static JellyfinWebhookRequest ValidRequest() => new()
    {
        NotificationType = "PlaybackProgress",
        ItemType = "Episode",
        ItemId = "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d",
        SeriesName = "Breaking Bad",
        SeasonNumber = 2,
        EpisodeNumber = 5,
        PlaybackPositionTicks = 25128000000,
        RunTimeTicks = 27310080000, // ~92% watched
    };

    [Fact]
    public void PassesFilter_AboveThreshold_Passes()
    {
        Assert.True(JellyfinWebhookHandler.PassesFilter(ValidRequest(), 80));
    }

    [Fact]
    public void PassesFilter_BelowThreshold_Fails()
    {
        var request = ValidRequest();
        request.PlaybackPositionTicks = 5000000000; // ~18% watched

        Assert.False(JellyfinWebhookHandler.PassesFilter(request, 80));
    }

    [Fact]
    public void PassesFilter_WrongNotificationType_Fails()
    {
        var request = ValidRequest();
        request.NotificationType = "PlaybackStart";

        Assert.False(JellyfinWebhookHandler.PassesFilter(request, 80));
    }

    [Fact]
    public void PassesFilter_WrongItemType_Fails()
    {
        var request = ValidRequest();
        request.ItemType = "Movie";

        Assert.False(JellyfinWebhookHandler.PassesFilter(request, 80));
    }

    [Theory]
    [MemberData(nameof(MissingRequiredFieldCases))]
    public void PassesFilter_MissingRequiredField_Fails(Action<JellyfinWebhookRequest> mutate)
    {
        var request = ValidRequest();
        mutate(request);

        Assert.False(JellyfinWebhookHandler.PassesFilter(request, 80));
    }

    public static IEnumerable<object[]> MissingRequiredFieldCases()
    {
        yield return [(Action<JellyfinWebhookRequest>)(r => r.SeriesName = null)];
        yield return [(Action<JellyfinWebhookRequest>)(r => r.SeasonNumber = null)];
        yield return [(Action<JellyfinWebhookRequest>)(r => r.EpisodeNumber = null)];
        yield return [(Action<JellyfinWebhookRequest>)(r => r.PlaybackPositionTicks = null)];
        yield return [(Action<JellyfinWebhookRequest>)(r => r.RunTimeTicks = null)];
        yield return [(Action<JellyfinWebhookRequest>)(r => r.RunTimeTicks = 0)];
    }

    [Fact]
    public void PassesFilter_UnrelatedExtraFields_StillPasses()
    {
        // Confirms unread fields (as Jellyfin's real payload includes many more) never
        // affect the result — nothing here reads Year/NotificationUsername/etc.
        var request = ValidRequest();

        Assert.True(JellyfinWebhookHandler.PassesFilter(request, 80));
    }

    [Fact]
    public void TryMarkHandled_SameItemIdTwice_SecondCallReturnsFalse()
    {
        var handled = new ConcurrentDictionary<string, byte>();

        Assert.True(JellyfinWebhookHandler.TryMarkHandled(handled, "item-1"));
        Assert.False(JellyfinWebhookHandler.TryMarkHandled(handled, "item-1"));
    }

    [Fact]
    public void TryMarkHandled_DifferentItemIds_BothReturnTrue()
    {
        var handled = new ConcurrentDictionary<string, byte>();

        Assert.True(JellyfinWebhookHandler.TryMarkHandled(handled, "item-1"));
        Assert.True(JellyfinWebhookHandler.TryMarkHandled(handled, "item-2"));
    }

    [Fact]
    public void TryMarkHandled_PastCap_ClearsAndReAdmitsPreviouslyHandledId()
    {
        var handled = new ConcurrentDictionary<string, byte>();
        for (var i = 0; i < 501; i++)
            handled.TryAdd($"filler-{i}", 0);

        // The set is now over the 500 cap; the next call clears it wholesale first.
        Assert.True(JellyfinWebhookHandler.TryMarkHandled(handled, "filler-0"));
    }
}
