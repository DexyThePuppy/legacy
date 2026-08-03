using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.YouTube;

public class DeleteYouTubeImportHandler(
    IMediator mediator,
    IYouTubeImportService importService,
    ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel,
    ILogger<DeleteYouTubeImportHandler> logger)
    : IRequestHandler<DeleteYouTubeImport, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        DeleteYouTubeImport request,
        CancellationToken cancellationToken)
    {
        Option<YouTubeImportManifest> maybeManifest = await importService.GetImport(request.Slug, cancellationToken);

        foreach (YouTubeImportManifest manifest in maybeManifest)
        {
            if (request.DeleteLinkedStation && manifest.ChannelId is not null)
            {
                Either<BaseError, Unit> stationResult = await mediator.Send(
                    new DeleteChannelStation(manifest.ChannelId.Value),
                    cancellationToken);

                foreach (BaseError error in stationResult.LeftToSeq())
                {
                    logger.LogWarning(
                        "Failed to delete linked station for YouTube import {Slug}: {Error}",
                        request.Slug,
                        error.Value);
                    return error;
                }
            }
        }

        Either<BaseError, Unit> result = await importService.DeleteImport(request.Slug, cancellationToken);

        // rescan so removed items are trashed in the library
        foreach (YouTubeImportManifest manifest in maybeManifest)
        {
            await scannerWorkerChannel.WriteAsync(new ForceScanLocalLibrary(manifest.LibraryId), cancellationToken);
        }

        return result;
    }
}
