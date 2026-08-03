using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Channels;

public class DeleteChannelStationHandler(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    ILogger<DeleteChannelStationHandler> logger)
    : IRequestHandler<DeleteChannelStation, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteChannelStation request,
        CancellationToken cancellationToken)
    {
        List<int> scheduleIds;
        List<int> smartCollectionIds;

        await using (TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            Option<Channel> maybeChannel = await dbContext.Channels
                .AsNoTracking()
                .Include(c => c.Playouts)
                .SelectOneAsync(c => c.Id, c => c.Id == request.ChannelId, cancellationToken);

            if (maybeChannel.IsNone)
            {
                return BaseError.New($"Channel {request.ChannelId} does not exist.");
            }

            Channel channel = maybeChannel.Head();

            scheduleIds = channel.Playouts
                .Where(p => p.ProgramScheduleId is not null)
                .Select(p => p.ProgramScheduleId!.Value)
                .Distinct()
                .ToList();

            smartCollectionIds = [];
            if (request.DeleteSmartCollection && scheduleIds.Count > 0)
            {
                smartCollectionIds = await dbContext.ProgramScheduleItems
                    .AsNoTracking()
                    .Where(i => scheduleIds.Contains(i.ProgramScheduleId) && i.SmartCollectionId != null)
                    .Select(i => i.SmartCollectionId!.Value)
                    .Distinct()
                    .ToListAsync(cancellationToken);
            }
        }

        // channel delete cascades playouts, which clears the schedule-delete guard
        Either<BaseError, Unit> deleteChannel = await mediator.Send(
            new DeleteChannel(request.ChannelId),
            cancellationToken);

        foreach (BaseError error in deleteChannel.LeftToSeq())
        {
            return error;
        }

        if (request.DeleteSchedule)
        {
            foreach (int scheduleId in scheduleIds)
            {
                Either<BaseError, Unit> deleteSchedule = await mediator.Send(
                    new DeleteProgramSchedule(scheduleId),
                    cancellationToken);

                foreach (BaseError error in deleteSchedule.LeftToSeq())
                {
                    logger.LogWarning(
                        "Deleted channel {ChannelId} but could not delete schedule {ScheduleId}: {Error}",
                        request.ChannelId,
                        scheduleId,
                        error.Value);
                }
            }
        }

        if (request.DeleteSmartCollection)
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            foreach (int smartCollectionId in smartCollectionIds)
            {
                bool stillReferenced = await dbContext.ProgramScheduleItems
                    .AsNoTracking()
                    .AnyAsync(i => i.SmartCollectionId == smartCollectionId, cancellationToken);

                if (stillReferenced)
                {
                    continue;
                }

                Either<BaseError, Unit> deleteCollection = await mediator.Send(
                    new DeleteSmartCollection(smartCollectionId),
                    cancellationToken);

                foreach (BaseError error in deleteCollection.LeftToSeq())
                {
                    logger.LogWarning(
                        "Deleted channel {ChannelId} but could not delete smart collection {CollectionId}: {Error}",
                        request.ChannelId,
                        smartCollectionId,
                        error.Value);
                }
            }
        }

        return Unit.Default;
    }
}
