using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Channels;

public class SeekChannelPlaybackHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService)
    : IRequestHandler<SeekChannelPlayback, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        SeekChannelPlayback request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Either<BaseError, Channel> maybeChannel =
            await ChannelPlaybackCommandHelper.LoadChannel(dbContext, request.ChannelId, cancellationToken);

        foreach (BaseError error in maybeChannel.LeftToSeq())
        {
            return error;
        }

        foreach (Channel channel in maybeChannel.RightToSeq())
        {
            bool wasPaused = channel.PlaybackPausedAt.HasValue;
            DateTimeOffset effectiveNow = ChannelPlaybackClock.GetEffectiveNow(channel);
            Option<PlayoutItem> maybeItem =
                await ChannelPlaybackCommandHelper.GetCurrentItem(dbContext, channel, effectiveNow, cancellationToken);

            foreach (PlayoutItem item in maybeItem)
            {
                TimeSpan duration = item.FinishOffset - item.StartOffset;
                if (duration < TimeSpan.Zero)
                {
                    duration = TimeSpan.Zero;
                }

                TimeSpan position = request.PositionWithinItem;
                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                if (position > duration)
                {
                    position = duration;
                }

                DateTimeOffset target = item.StartOffset + position;
                ChannelPlaybackClock.SeekTo(channel, target);
                await dbContext.SaveChangesAsync(cancellationToken);
                ChannelPlaybackCommandHelper.NotifySegmenterAfterSeek(segmenterService, channel, wasPaused);
                return Unit.Default;
            }

            return BaseError.New($"No current playout item for channel {channel.Number}");
        }

        return Unit.Default;
    }
}
