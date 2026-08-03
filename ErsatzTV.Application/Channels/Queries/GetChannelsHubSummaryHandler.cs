using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Channels;

public class GetChannelsHubSummaryHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFFmpegSegmenterService segmenterService)
    : IRequestHandler<GetChannelsHubSummary, List<ChannelHubSummaryViewModel>>
{
    public async Task<List<ChannelHubSummaryViewModel>> Handle(
        GetChannelsHubSummary request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<Channel> channels = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.Playouts)
            .ThenInclude(p => p.BuildStatus)
            .Include(c => c.MirrorSourceChannel)
            .ThenInclude(mc => mc.Playouts)
            .ThenInclude(p => p.BuildStatus)
            .OrderBy(c => c.SortNumber)
            .ToListAsync(cancellationToken);

        if (!request.IncludeDisabled)
        {
            channels = channels.Where(c => c.IsEnabled).ToList();
        }

        return channels.Map(Project).ToList();
    }

    private ChannelHubSummaryViewModel Project(Channel channel)
    {
        bool hasOwnPlayout = channel.Playouts is { Count: > 0 };
        bool hasMirrorPlayout = channel.PlayoutSource is ChannelPlayoutSource.Mirror &&
                                channel.MirrorSourceChannel?.Playouts is { Count: > 0 };
        bool hasPlayout = hasOwnPlayout || hasMirrorPlayout;

        Playout playout = null;
        if (hasOwnPlayout)
        {
            playout = channel.Playouts[0];
        }
        else if (hasMirrorPlayout)
        {
            playout = channel.MirrorSourceChannel.Playouts[0];
        }

        PlayoutBuildStatus buildStatus = playout?.BuildStatus;

        return new ChannelHubSummaryViewModel(
            channel.Id,
            channel.Number,
            channel.Name,
            channel.Group,
            channel.IsEnabled,
            channel.ShowInEpg,
            hasPlayout,
            IsLive: hasPlayout && channel.IsEnabled,
            playout?.Id,
            buildStatus?.Success,
            buildStatus?.Message,
            segmenterService.IsActive(channel.Number));
    }
}
