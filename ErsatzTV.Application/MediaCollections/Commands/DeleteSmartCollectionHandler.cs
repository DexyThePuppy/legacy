using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Core.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.MediaCollections;

public class DeleteSmartCollectionHandler : IRequestHandler<DeleteSmartCollection, Either<BaseError, Unit>>
{
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly ISearchTargets _searchTargets;
    private readonly ISmartCollectionCache _smartCollectionCache;

    public DeleteSmartCollectionHandler(
        IDbContextFactory<TvContext> dbContextFactory,
        ISearchTargets searchTargets,
        ISmartCollectionCache smartCollectionCache)
    {
        _dbContextFactory = dbContextFactory;
        _searchTargets = searchTargets;
        _smartCollectionCache = smartCollectionCache;
    }

    public async Task<Either<BaseError, Unit>> Handle(
        DeleteSmartCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, SmartCollection> validation = await Validate(
            dbContext,
            request,
            cancellationToken);
        return await validation.Apply(c => DoDeletion(dbContext, c, cancellationToken));
    }

    private async Task<Unit> DoDeletion(
        TvContext dbContext,
        SmartCollection smartCollection,
        CancellationToken cancellationToken)
    {
        dbContext.SmartCollections.Remove(smartCollection);
        await dbContext.SaveChangesAsync(cancellationToken);
        _searchTargets.SearchTargetsChanged();
        await _smartCollectionCache.Refresh(cancellationToken);
        return Unit.Default;
    }

    private static async Task<Validation<BaseError, SmartCollection>> Validate(
        TvContext dbContext,
        DeleteSmartCollection request,
        CancellationToken cancellationToken)
    {
        Option<SmartCollection> maybeCollection = await dbContext.SmartCollections
            .SelectOneAsync(c => c.Id, c => c.Id == request.SmartCollectionId, cancellationToken);

        foreach (SmartCollection collection in maybeCollection)
        {
            int scheduleItemCount = await dbContext.ProgramScheduleItems
                .CountAsync(i => i.SmartCollectionId == collection.Id, cancellationToken);

            if (scheduleItemCount > 0)
            {
                return BaseError.New(
                    $"Smart collection '{collection.Name}' is used by {scheduleItemCount} schedule item(s). Remove those schedule items before deleting the collection.");
            }

            return collection;
        }

        return BaseError.New($"SmartCollection {request.SmartCollectionId} does not exist.");
    }
}
