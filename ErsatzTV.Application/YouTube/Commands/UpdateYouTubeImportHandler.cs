using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;

namespace ErsatzTV.Application.YouTube;

public class UpdateYouTubeImportHandler(IYouTubeImportService importService)
    : IRequestHandler<UpdateYouTubeImport, Either<BaseError, Unit>>
{
    public Task<Either<BaseError, Unit>> Handle(UpdateYouTubeImport request, CancellationToken cancellationToken) =>
        importService.UpdateImport(
            request.Slug,
            request.Name,
            request.IconUrl,
            request.AutoSync,
            request.SyncIntervalHours,
            cancellationToken);
}
