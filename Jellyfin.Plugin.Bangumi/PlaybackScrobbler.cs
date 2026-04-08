using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Bangumi.Configuration;
using Jellyfin.Plugin.Bangumi.Model;
using Jellyfin.Plugin.Bangumi.OAuth;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using CollectionType = Jellyfin.Plugin.Bangumi.Model.CollectionType;

namespace Jellyfin.Plugin.Bangumi;

public class PlaybackScrobbler(IUserDataManager userDataManager, OAuthStore store, BangumiApi api, Logger<PlaybackScrobbler> log) : IHostedService
{
    private static PluginConfiguration Configuration => Plugin.Instance!.Configuration;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        switch (e.SaveReason)
        {
            case UserDataSaveReason.TogglePlayed when e.UserData.Played:
                if (Configuration.ReportManualStatusChangeToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, true).ConfigureAwait(false);
                break;

            case UserDataSaveReason.TogglePlayed when !e.UserData.Played:
                if (Configuration.ReportManualStatusChangeToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, false).ConfigureAwait(false);
                break;

            case UserDataSaveReason.PlaybackFinished when e.UserData.Played:
                if (Configuration.ReportPlaybackStatusToBangumi)
                    ReportPlaybackStatus(e.Item, e.UserId, true).ConfigureAwait(false);
                break;
        }
    }

    private async Task ReportPlaybackStatus(BaseItem item, Guid userId, bool played)
    {
        Episode? episode = null;
        var localConfiguration = await LocalConfiguration.ForPath(item.Path);
        if (!int.TryParse(item.GetProviderId(Constants.ProviderName), out var episodeId))
        {
            log.Info("item {Name} (#{Id}) doesn't have bangumi id, ignored", item.Name, item.Id);
            return;
        }

        if (!int.TryParse(item.GetParent()?.GetProviderId(Constants.ProviderName), out var subjectId))
            log.Warn("parent of item {Name} (#{Id}) doesn't have bangumi subject id", item.Name, item.Id);

        if (!localConfiguration.Report)
        {
            log.Info("playback report is disabled via local configuration");
            return;
        }

        if (item is Movie)
        {
            subjectId = subjectId == 0 ? episodeId : subjectId;
            // jellyfin only have subject id for movie, so we need to get episode id from bangumi api
            var episodeList = await api.GetSubjectEpisodeListWithOffset(subjectId, EpisodeType.Normal, 0, CancellationToken.None);
            if (episodeList?.Data.Any() ?? false)
                episodeId = episodeList.Data.First().Id;
        }

        store.Load();
        var user = store.Get(userId);
        if (user == null)
        {
            log.Info("access token for user #{User} not found, ignored", userId);
            return;
        }

        if (user.Expired)
        {
            log.Info("access token for user #{User} expired, ignored", userId);
            return;
        }

        try
        {
            if (item is Book)
            {
                log.Info("report subject #{Subject} status {Status} to bangumi", episodeId, CollectionType.Watched);
                await api.UpdateCollectionStatus(user.AccessToken,
                    episodeId,
                    played ? CollectionType.Watched : CollectionType.Watching,
                    CancellationToken.None);
            }
            else
            {
                if (subjectId == 0)
                {
                    episode ??= await api.GetEpisode(episodeId, CancellationToken.None);
                    if (episode != null)
                        subjectId = episode.ParentId;
                }

                var subject = await api.GetSubject(subjectId, CancellationToken.None);
                if (subject?.IsNSFW == true && Configuration.SkipNSFWPlaybackReport)
                {
                    log.Info("item #{Name} marked as NSFW, skipped", item.Name);
                    return;
                }

                var episodeStatus = await api.GetEpisodeStatus(user.AccessToken, episodeId, CancellationToken.None);
                if (episodeStatus?.Type == EpisodeCollectionType.Watched)
                {
                    log.Info("item {Name} (#{Id}) has been marked as watched before, ignored",
                        item.Name,
                        item.Id);
                    return;
                }

                if (played)
                    await EnsureSubjectWatchingStatus(user.AccessToken, user.UserName, subjectId, CancellationToken.None);

                log.Info("report episode #{Episode} status {Status} to bangumi",
                    episodeId,
                    played ? EpisodeCollectionType.Watched : EpisodeCollectionType.Default);
                await api.UpdateEpisodeStatus(user.AccessToken,
                    episodeId,
                    played ? EpisodeCollectionType.Watched : EpisodeCollectionType.Default,
                    CancellationToken.None);
            }

            log.Info("report completed");
        }
        catch (Exception e)
        {
            if (played && IsErrorFromUncollectedSubject(e))
            {
                await EnsureSubjectWatchingStatus(user.AccessToken, user.UserName, subjectId, CancellationToken.None);

                log.Info("report episode #{Episode} status {Status} to bangumi", episodeId, EpisodeCollectionType.Watched);
                await api.UpdateEpisodeStatus(user.AccessToken,
                    episodeId,
                    played ? EpisodeCollectionType.Watched : EpisodeCollectionType.Default,
                    CancellationToken.None);
            }
            else
            {
                log.Error("report playback status failed: {Error}", e);
                return;
            }
        }

        // report subject status watched
        if (!played || item is Book) return;

        // skip if episode type not normal
        episode ??= await api.GetEpisode(episodeId, CancellationToken.None);
        if (episode is not { Type: EpisodeType.Normal }) return;

        // check each episode status
        var epList = await api.GetEpisodeCollectionInfo(user.AccessToken, subjectId, (int)EpisodeType.Normal, CancellationToken.None);
        if (epList is { Total: > 0 } && epList.Data.All(ep => ep.Type == EpisodeCollectionType.Watched))
        {
            log.Info("report subject #{Subject} status {Status} to bangumi", subjectId, CollectionType.Watched);
            await api.UpdateCollectionStatus(user.AccessToken, subjectId, CollectionType.Watched, CancellationToken.None);
        }
    }

    private async Task EnsureSubjectWatchingStatus(string accessToken, string? userName, int subjectId, CancellationToken token)
    {
        if (subjectId <= 0)
        {
            log.Warn("bangumi subject id is missing, skipped subject status update");
            return;
        }

        var subjectCollection = await api.GetSubjectCollectionStatus(accessToken, subjectId, token, userName);
        var currentStatus = subjectCollection?.Status ?? CollectionType.None;
        if (!ShouldUpdateSubjectCollectionToWatching(
                currentStatus,
                Configuration.UpdateExistingCollectionToWatching,
                Configuration.UpdateWatchedCollectionToWatching))
        {
            switch (currentStatus)
            {
                case CollectionType.Watching:
                    log.Info("subject #{Subject} is already watching, ignored", subjectId);
                    break;
                case CollectionType.Watched:
                    log.Info("subject #{Subject} is watched and watched-to-watching sync is disabled, ignored", subjectId);
                    break;
                default:
                    log.Info("subject #{Subject} already has collection status {Status}, ignored", subjectId, currentStatus);
                    break;
            }

            return;
        }

        log.Info("report subject #{Subject} status {Status} to bangumi", subjectId, CollectionType.Watching);
        await api.UpdateCollectionStatus(accessToken, subjectId, CollectionType.Watching, token);
    }

    internal static bool ShouldUpdateSubjectCollectionToWatching(
        CollectionType currentStatus,
        bool updateExistingCollectionToWatching,
        bool updateWatchedCollectionToWatching)
    {
        if (currentStatus == CollectionType.None)
            return true;

        if (currentStatus == CollectionType.Watching)
            return false;

        if (!updateExistingCollectionToWatching)
            return false;

        return currentStatus != CollectionType.Watched || updateWatchedCollectionToWatching;
    }

    internal static bool IsErrorFromUncollectedSubject(Exception e) =>
        e.Message.Contains("need to add subject to your collection", StringComparison.OrdinalIgnoreCase);
}
