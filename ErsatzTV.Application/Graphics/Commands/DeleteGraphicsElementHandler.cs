using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Graphics;

public class DeleteGraphicsElementHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFileSystem fileSystem,
    ILogger<DeleteGraphicsElementHandler> logger)
    : IRequestHandler<DeleteGraphicsElement, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteGraphicsElement request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<GraphicsElement> maybeElement = await dbContext.GraphicsElements
            .SelectOneAsync(ge => ge.Id, ge => ge.Id == request.GraphicsElementId, cancellationToken);

        if (maybeElement.IsNone)
        {
            return BaseError.New($"Graphics element {request.GraphicsElementId} does not exist");
        }

        foreach (GraphicsElement element in maybeElement)
        {
            try
            {
                if (fileSystem.File.Exists(element.Path))
                {
                    fileSystem.File.Delete(element.Path);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete overlay template file {Path}", element.Path);
                return BaseError.New($"Failed to delete overlay template file: {ex.Message}");
            }

            dbContext.GraphicsElements.Remove(element);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Unit.Default;
    }
}
