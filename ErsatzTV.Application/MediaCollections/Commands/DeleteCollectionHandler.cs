using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.MediaCollections;

public class DeleteCollectionHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    ISearchTargets searchTargets,
    IMediaCollectionRepository mediaCollectionRepository,
    ISearchRepository searchRepository,
    IFallbackMetadataProvider fallbackMetadataProvider,
    ILanguageCodeService languageCodeService,
    ISearchIndex searchIndex)
    : IRequestHandler<DeleteCollection, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteCollection request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        Validation<BaseError, Collection> validation = await Validate(dbContext, request, cancellationToken);
        return await validation.Apply(c => DoDeletion(dbContext, c, cancellationToken));
    }

    private async Task<Unit> DoDeletion(TvContext dbContext, Collection collection, CancellationToken cancellationToken)
    {
        var itemIds = (await mediaCollectionRepository.GetItems(collection.Id)).Map(i => i.Id).ToList();
        dbContext.Collections.Remove(collection);
        await dbContext.SaveChangesAsync(cancellationToken);
        await searchIndex.RebuildItems(
            searchRepository,
            fallbackMetadataProvider,
            languageCodeService,
            itemIds,
            cancellationToken);
        searchIndex.Commit();
        searchTargets.SearchTargetsChanged();
        return Unit.Default;
    }

    private static async Task<Validation<BaseError, Collection>> Validate(
        TvContext dbContext,
        DeleteCollection request,
        CancellationToken cancellationToken)
    {
        Option<Collection> maybeCollection = await dbContext.Collections
            .SelectOneAsync(c => c.Id, c => c.Id == request.CollectionId, cancellationToken);

        foreach (Collection collection in maybeCollection)
        {
            int scheduleItemCount = await dbContext.ProgramScheduleItems
                .CountAsync(i => i.CollectionId == collection.Id, cancellationToken);

            if (scheduleItemCount > 0)
            {
                return BaseError.New(
                    $"Collection '{collection.Name}' is used by {scheduleItemCount} schedule item(s). Remove those schedule items before deleting the collection.");
            }

            return collection;
        }

        return BaseError.New($"Collection {request.CollectionId} does not exist.");
    }
}
