using System.Threading.Channels;
using ErsatzTV.Core.Interfaces.Locking;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Playouts;

public class RebuildFailedPlayoutsHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    IEntityLocker locker,
    ILogger<RebuildFailedPlayoutsHandler> logger)
    : IRequestHandler<RebuildFailedPlayouts>
{
    public async Task Handle(RebuildFailedPlayouts request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<int> failedPlayoutIds = await dbContext.PlayoutBuildStatus
            .AsNoTracking()
            .Where(pbs => !pbs.Success)
            .Select(pbs => pbs.PlayoutId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (failedPlayoutIds.Count == 0)
        {
            return;
        }

        logger.LogInformation(
            "Retrying {Count} failed playout build(s) after media index update",
            failedPlayoutIds.Count);

        foreach (int playoutId in failedPlayoutIds)
        {
            if (locker.IsPlayoutLocked(playoutId))
            {
                continue;
            }

            await workerChannel.WriteAsync(
                new BuildPlayout(playoutId, PlayoutBuildMode.Reset),
                cancellationToken);
        }
    }
}
