using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Channels;

public class ResumeChannelPlaybackHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService)
    : IRequestHandler<ResumeChannelPlayback, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        ResumeChannelPlayback request,
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
            if (!channel.PlaybackPausedAt.HasValue)
            {
                return Unit.Default;
            }

            ChannelPlaybackClock.Resume(channel);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!segmenterService.ResumeChannel(channel.Number))
            {
                segmenterService.PlayoutUpdated(channel.Number);
            }
        }

        return Unit.Default;
    }
}
