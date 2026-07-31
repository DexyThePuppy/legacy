using System.Threading.Channels;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class DeleteYouTubeImportHandler(
    IYouTubeImportService importService,
    ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel)
    : IRequestHandler<DeleteYouTubeImport, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteYouTubeImport request,
        CancellationToken cancellationToken)
    {
        Option<YouTubeImportManifest> maybeManifest = await importService.GetImport(request.Slug, cancellationToken);

        Either<BaseError, Unit> result = await importService.DeleteImport(request.Slug, cancellationToken);

        // rescan so removed items are trashed in the library
        foreach (YouTubeImportManifest manifest in maybeManifest)
        {
            await scannerWorkerChannel.WriteAsync(new ForceScanLocalLibrary(manifest.LibraryId), cancellationToken);
        }

        return result;
    }
}
