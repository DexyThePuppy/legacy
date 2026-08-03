using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Channels;

internal static class ChannelPlaybackCommandHelper
{
    public static async Task<Either<BaseError, Channel>> LoadChannel(
        TvContext dbContext,
        int channelId,
        CancellationToken cancellationToken)
    {
        Option<Channel> maybeChannel = await dbContext.Channels
            .SelectOneAsync(c => c.Id, c => c.Id == channelId, cancellationToken);

        return maybeChannel.ToEither(BaseError.New($"Channel {channelId} does not exist."));
    }

    public static async Task<Option<PlayoutItem>> GetCurrentItem(
        TvContext dbContext,
        Channel channel,
        DateTimeOffset effectiveNow,
        CancellationToken cancellationToken)
    {
        int playoutChannelId = channel.MirrorSourceChannelId ?? channel.Id;
        return await dbContext.PlayoutItems
            .AsNoTracking()
            .ForChannelAndTime(playoutChannelId, effectiveNow);
    }

    public static void NotifySegmenterAfterSeek(
        IFFmpegSegmenterService segmenterService,
        Channel channel,
        bool wasPaused)
    {
        if (!segmenterService.IsActive(channel.Number))
        {
            return;
        }

        if (wasPaused || channel.PlaybackPausedAt.HasValue)
        {
            segmenterService.PauseChannel(channel.Number);
        }
        else
        {
            segmenterService.PlayoutUpdated(channel.Number);
        }
    }
}
