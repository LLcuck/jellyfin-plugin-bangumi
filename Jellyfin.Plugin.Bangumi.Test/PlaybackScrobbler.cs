using Jellyfin.Plugin.Bangumi.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Jellyfin.Plugin.Bangumi.Test;

[TestClass]
public class PlaybackScrobbler
{
    [TestMethod]
    public void ShouldUpdateSubjectCollectionToWatching_MatchesConfiguration()
    {
        Assert.IsTrue(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.None,
            updateExistingCollectionToWatching: false,
            updateWatchedCollectionToWatching: false));

        Assert.IsFalse(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Watching,
            updateExistingCollectionToWatching: true,
            updateWatchedCollectionToWatching: true));

        Assert.IsFalse(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Pending,
            updateExistingCollectionToWatching: false,
            updateWatchedCollectionToWatching: false));

        Assert.IsTrue(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Pending,
            updateExistingCollectionToWatching: true,
            updateWatchedCollectionToWatching: false));

        Assert.IsFalse(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Watched,
            updateExistingCollectionToWatching: true,
            updateWatchedCollectionToWatching: false));

        Assert.IsFalse(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Watched,
            updateExistingCollectionToWatching: false,
            updateWatchedCollectionToWatching: true));

        Assert.IsTrue(Jellyfin.Plugin.Bangumi.PlaybackScrobbler.ShouldUpdateSubjectCollectionToWatching(
            CollectionType.Watched,
            updateExistingCollectionToWatching: true,
            updateWatchedCollectionToWatching: true));
    }

    [TestMethod]
    public void GetSubjectCollectionStatusUrl_UsesActualBangumiUsername()
    {
        var url = BangumiApi.GetSubjectCollectionStatusUrl("https://api.bgm.tv", "alice", 42);

        Assert.AreEqual("https://api.bgm.tv/v0/users/alice/collections/42", url);
    }
}
