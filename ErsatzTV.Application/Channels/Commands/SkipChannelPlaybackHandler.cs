using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Channels;

public class SkipChannelPlaybackHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService)
    : IRequestHandler<SkipChannelPlayback, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        SkipChannelPlayback request,
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
            int playoutChannelId = channel.MirrorSourceChannelId ?? channel.Id;

            Option<PlayoutItem> maybeCurrent =
                await ChannelPlaybackCommandHelper.GetCurrentItem(dbContext, channel, effectiveNow, cancellationToken);

            foreach (PlayoutItem current in maybeCurrent)
            {
                Option<PlayoutItem> maybeTarget = request.Direction switch
                {
                    ChannelPlaybackSkipDirection.Previous => await dbContext.PlayoutItems
                        .AsNoTracking()
                        .Where(pi => pi.Playout.ChannelId == playoutChannelId)
                        .Where(pi => pi.Finish <= current.Start)
                        .OrderByDescending(pi => pi.Start)
                        .FirstOrDefaultAsync(cancellationToken)
                        .Map(Optional),
                    _ => await dbContext.PlayoutItems
                        .AsNoTracking()
                        .Where(pi => pi.Playout.ChannelId == playoutChannelId)
                        .Where(pi => pi.Start >= current.Finish)
                        .OrderBy(pi => pi.Start)
                        .FirstOrDefaultAsync(cancellationToken)
                        .Map(Optional)
                };

                foreach (PlayoutItem target in maybeTarget)
                {
                    ChannelPlaybackClock.SeekTo(channel, target.StartOffset);
                    await dbContext.SaveChangesAsync(cancellationToken);
                    ChannelPlaybackCommandHelper.NotifySegmenterAfterSeek(segmenterService, channel, wasPaused);
                    return Unit.Default;
                }

                return BaseError.New(
                    request.Direction is ChannelPlaybackSkipDirection.Previous
                        ? $"No previous playout item for channel {channel.Number}"
                        : $"No next playout item for channel {channel.Number}");
            }

            return BaseError.New($"No current playout item for channel {channel.Number}");
        }

        return Unit.Default;
    }
}
