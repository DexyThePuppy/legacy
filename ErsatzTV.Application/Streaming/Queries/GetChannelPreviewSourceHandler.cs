using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Streaming;

public class GetChannelPreviewSourceHandler(IDbContextFactory<TvContext> dbContextFactory, IDynamicPlayoutItemService dynamicPlayoutItemService)
    : IRequestHandler<GetChannelPreviewSource, Either<BaseError, ChannelPreviewSource>>
{
    public async Task<Either<BaseError, ChannelPreviewSource>> Handle(
        GetChannelPreviewSource request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .SelectOneAsync(c => c.Number, c => c.Number == request.ChannelNumber, cancellationToken);

        foreach (Channel channel in maybeChannel)
        {
            DateTimeOffset now = ChannelPlaybackClock.GetEffectiveNow(channel);
            int channelId = channel.MirrorSourceChannelId ?? channel.Id;

            Option<PlayoutItem> maybeItem = await dbContext.PlayoutItems
                .AsNoTracking()
                .Include(i => i.MediaItem)
                .ThenInclude(mi => mi.LibraryPath)
                .ThenInclude(lp => lp.Library)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Episode).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Movie).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Song).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Image).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .ForChannelAndTime(channelId, now);

            foreach (PlayoutItem item in maybeItem)
            {
                Either<BaseError, PlayoutItemWithPath> maybePath =
                    await dynamicPlayoutItemService.ValidatePlayoutItemPath(dbContext, item, cancellationToken);

                foreach (PlayoutItemWithPath withPath in maybePath.RightToSeq())
                {
                    bool isLive = withPath.PlayoutItem.MediaItem is RemoteStream { IsLive: true };
                    TimeSpan seek = TimeSpan.Zero;
                    if (!isLive)
                    {
                        seek = now - withPath.PlayoutItem.StartOffset + withPath.PlayoutItem.InPoint;
                        if (seek < TimeSpan.Zero)
                        {
                            seek = TimeSpan.Zero;
                        }
                    }

                    return new ChannelPreviewSource(
                        withPath.Path,
                        seek,
                        isLive,
                        channel.PlaybackPausedAt.HasValue);
                }

                foreach (BaseError error in maybePath.LeftToSeq())
                {
                    return error;
                }
            }

            return BaseError.New($"Unable to locate current playout item for channel {request.ChannelNumber}");
        }

        return BaseError.New($"Unable to locate channel {request.ChannelNumber}");
    }
}
