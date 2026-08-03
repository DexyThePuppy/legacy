using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.ProgramSchedules;

public class DeleteProgramScheduleHandler : IRequestHandler<DeleteProgramSchedule, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;

    public DeleteProgramScheduleHandler(IDbContextFactory<TvContext> dbContextFactory) =>
        _dbContextFactory = dbContextFactory;

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteProgramSchedule request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, ProgramSchedule> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(ps => DoDeletion(dbContext, ps));
    }

    private static Task<Unit> DoDeletion(TvContext dbContext, ProgramSchedule programSchedule)
    {
        dbContext.ProgramSchedules.Remove(programSchedule);
        return dbContext.SaveChangesAsync().ToUnit();
    }

    private static async Task<Validation<BaseError, ProgramSchedule>> Validate(
        TvContext dbContext,
        DeleteProgramSchedule request,
        CancellationToken cancellationToken)
    {
        Option<ProgramSchedule> maybeSchedule = await dbContext.ProgramSchedules
            .SelectOneAsync(ps => ps.Id, ps => ps.Id == request.ProgramScheduleId, cancellationToken);

        foreach (ProgramSchedule schedule in maybeSchedule)
        {
            int playoutCount = await dbContext.Playouts
                .CountAsync(p => p.ProgramScheduleId == schedule.Id, cancellationToken);

            if (playoutCount > 0)
            {
                return BaseError.New(
                    $"Schedule '{schedule.Name}' is used by {playoutCount} playout(s). Remove or reassign those playouts before deleting the schedule.");
            }

            return schedule;
        }

        return BaseError.New($"ProgramSchedule {request.ProgramScheduleId} does not exist.");
    }
}
