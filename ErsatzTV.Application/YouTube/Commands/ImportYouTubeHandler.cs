using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.Libraries;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.YouTube;

public class ImportYouTubeHandler(
    IMediator mediator,
    IYouTubeImportService importService,
    ChannelWriter<IYtDlpWorkerRequest> ytDlpWorkerChannel,
    ILogger<ImportYouTubeHandler> logger)
    : IRequestHandler<ImportYouTube, Either<BaseError, YouTubeImportResult>>
{
    public async Task<Either<BaseError, YouTubeImportResult>> Handle(
        ImportYouTube request,
        CancellationToken cancellationToken)
    {
        try
        {
            // ensure the youtube library exists
            Either<BaseError, int> maybeLibraryId = await EnsureYouTubeLibrary(cancellationToken);
            foreach (BaseError error in maybeLibraryId.LeftToSeq())
            {
                return error;
            }

            int libraryId = maybeLibraryId.RightToSeq().Head();

            // write the import folder (yaml definitions + manifest)
            Either<BaseError, YouTubeImportManifest> maybeManifest = await importService.CreateImport(
                request.QueryResult,
                request.Name,
                request.IconUrl,
                request.AutoSync,
                request.SyncIntervalHours,
                libraryId,
                cancellationToken);

            foreach (BaseError error in maybeManifest.LeftToSeq())
            {
                return error;
            }

            YouTubeImportManifest manifest = maybeManifest.RightToSeq().Head();

            // scan synchronously so media items exist before station/playout build
            Either<BaseError, string> scanResult =
                await mediator.Send(new ForceScanLocalLibrary(libraryId), cancellationToken);
            foreach (BaseError error in scanResult.LeftToSeq())
            {
                if (error is not ScanIsNotRequired)
                {
                    logger.LogWarning(
                        "YouTube import {Name} library scan failed: {Error}",
                        manifest.Name,
                        error.Value);
                }
            }

            // thumbnails (and a follow-up scan) in the background
            await ytDlpWorkerChannel.WriteAsync(new FetchYouTubeThumbnails(manifest.Slug), cancellationToken);

            int? channelId = null;
            if (request.CreateStation)
            {
                // DeferPlayoutBuild: search index may still be catching up after the scan.
                // CreateChannelStation queues one build attempt; RebuildFailedPlayouts retries after index.
                Either<BaseError, CreateChannelStationResult> maybeStation = await mediator.Send(
                    new CreateChannelStation(
                        manifest.Name,
                        null,
                        "YouTube",
                        null,
                        string.IsNullOrWhiteSpace(manifest.IconUrl)
                            ? null
                            : new Artworks.ArtworkContentTypeModel(manifest.IconUrl, string.Empty),
                        null,
                        ChannelStationContentKind.YouTubeSlug,
                        null,
                        manifest.Slug,
                        null,
                        PlaybackOrder.Chronological,
                        ChannelPlayoutMode.Continuous,
                        DeferPlayoutBuild: true),
                    cancellationToken);

                foreach (BaseError error in maybeStation.LeftToSeq())
                {
                    logger.LogWarning(
                        "Imported {Name} but failed to create channel station: {Error}",
                        manifest.Name,
                        error.Value);

                    return BaseError.New(
                        $"Imported {manifest.VideoCount} videos, but station creation failed: {error.Value}");
                }

                CreateChannelStationResult station = maybeStation.RightToSeq().Head();
                channelId = station.ChannelId;

                await importService.UpdateStationLinkage(
                    manifest.Slug,
                    station.ChannelId,
                    station.CollectionId,
                    station.ScheduleId,
                    station.PlayoutId,
                    cancellationToken);

                manifest.ChannelId = station.ChannelId;
                manifest.SmartCollectionId = station.CollectionId;
                manifest.ProgramScheduleId = station.ScheduleId;
                manifest.PlayoutId = station.PlayoutId;
            }

            return new YouTubeImportResult(manifest, channelId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to import from YouTube");
            return BaseError.New(ex.Message);
        }
    }

    private async Task<Either<BaseError, int>> EnsureYouTubeLibrary(CancellationToken cancellationToken)
    {
        List<LocalLibraryViewModel> libraries = await mediator.Send(new GetAllLocalLibraries(), cancellationToken);

        Option<LocalLibraryViewModel> maybeExisting = libraries.Find(l =>
            l.MediaKind == LibraryMediaKind.RemoteStreams &&
            string.Equals(l.Name, "YouTube", StringComparison.OrdinalIgnoreCase));

        foreach (LocalLibraryViewModel existing in maybeExisting)
        {
            return existing.Id;
        }

        Directory.CreateDirectory(FileSystemLayout.YouTubeLibraryFolder);

        Either<BaseError, LocalLibraryViewModel> maybeLibrary = await mediator.Send(
            new CreateLocalLibrary(
                "YouTube",
                LibraryMediaKind.RemoteStreams,
                [FileSystemLayout.YouTubeLibraryFolder]),
            cancellationToken);

        return maybeLibrary.Map(l => l.Id);
    }
}
