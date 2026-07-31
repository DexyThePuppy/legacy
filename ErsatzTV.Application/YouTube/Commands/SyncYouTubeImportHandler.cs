using System.Threading.Channels;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class SyncYouTubeImportHandler(
    IYouTubeImportService importService,
    ChannelWriter<IYtDlpWorkerRequest> ytDlpWorkerChannel,
    ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel)
    : IRequestHandler<SyncYouTubeImport, Either<BaseError, YouTubeImportSyncResult>>
{
    public async Task<Either<BaseError, YouTubeImportSyncResult>> Handle(
        SyncYouTubeImport request,
        CancellationToken cancellationToken)
    {
        Either<BaseError, YouTubeImportSyncResult> maybeResult =
            await importService.SyncImport(request.Slug, cancellationToken);

        foreach (YouTubeImportSyncResult result in maybeResult.RightToSeq())
        {
            if (result.Added > 0 || result.Removed > 0)
            {
                Option<YouTubeImportManifest> maybeManifest =
                    await importService.GetImport(request.Slug, cancellationToken);

                foreach (YouTubeImportManifest manifest in maybeManifest)
                {
                    await scannerWorkerChannel.WriteAsync(
                        new ForceScanLocalLibrary(manifest.LibraryId),
                        cancellationToken);
                }

                await ytDlpWorkerChannel.WriteAsync(new FetchYouTubeThumbnails(request.Slug), cancellationToken);
            }
        }

        return maybeResult;
    }
}
