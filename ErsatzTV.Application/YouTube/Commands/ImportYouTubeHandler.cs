using System.Globalization;
using System.Threading.Channels;
using ErsatzTV.Application.Artworks;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Libraries;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.YouTube;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.YouTube;

public class ImportYouTubeHandler(
    IMediator mediator,
    IYouTubeImportService importService,
    ChannelWriter<IYtDlpWorkerRequest> ytDlpWorkerChannel,
    ChannelWriter<IScannerBackgroundServiceRequest> scannerWorkerChannel,
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

            // index the new items, then fetch thumbnails in the background
            await scannerWorkerChannel.WriteAsync(new ForceScanLocalLibrary(libraryId), cancellationToken);
            await ytDlpWorkerChannel.WriteAsync(new FetchYouTubeThumbnails(manifest.Slug), cancellationToken);

            int? channelId = null;
            if (request.CreateStation)
            {
                Either<BaseError, int> maybeChannelId = await CreateStation(manifest, cancellationToken);
                foreach (BaseError error in maybeChannelId.LeftToSeq())
                {
                    logger.LogWarning(
                        "Imported {Name} but failed to create channel station: {Error}",
                        manifest.Name,
                        error.Value);

                    return BaseError.New(
                        $"Imported {manifest.VideoCount} videos, but station creation failed: {error.Value}");
                }

                channelId = maybeChannelId.RightToSeq().Head();
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

    private async Task<Either<BaseError, int>> CreateStation(
        YouTubeImportManifest manifest,
        CancellationToken cancellationToken)
    {
        // smart collection that matches every video of this import
        string query = $"type:remote_stream AND tag:\"youtube-{manifest.Slug}\"";
        Either<BaseError, SmartCollectionViewModel> maybeCollection = await mediator.Send(
            new CreateSmartCollection(query, manifest.Name),
            cancellationToken);

        foreach (BaseError error in maybeCollection.LeftToSeq())
        {
            return error;
        }

        SmartCollectionViewModel collection = maybeCollection.RightToSeq().Head();

        // channel with the next available number
        FFmpegSettingsViewModel ffmpegSettings = await mediator.Send(new GetFFmpegSettings(), cancellationToken);

        List<ChannelViewModel> channels = await mediator.Send(new GetAllChannels(), cancellationToken);
        int maxNumber = channels
            .Map(c => int.TryParse(
                c.Number.Split('.').Head(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int result)
                ? result
                : 0)
            .DefaultIfEmpty(0)
            .Max();

        Either<BaseError, CreateChannelResult> maybeChannel = await mediator.Send(
            new CreateChannel(
                manifest.Name,
                (maxNumber + 1).ToString(CultureInfo.InvariantCulture),
                "YouTube",
                null,
                ffmpegSettings.DefaultFFmpegProfileId,
                null,
                string.IsNullOrWhiteSpace(manifest.IconUrl)
                    ? null
                    : new ArtworkContentTypeModel(manifest.IconUrl, string.Empty),
                ChannelStreamSelectorMode.Default,
                null,
                null,
                null,
                ChannelPlayoutSource.Generated,
                ChannelPlayoutMode.Continuous,
                null,
                null,
                StreamingEngine.Legacy,
                NextEngineTextSubtitleMode.Burn,
                StreamingMode.TransportStreamHybrid,
                null,
                null,
                null,
                ChannelSubtitleMode.None,
                ChannelMusicVideoCreditsMode.None,
                null,
                ChannelSongVideoMode.Default,
                ChannelTranscodeMode.OnDemand,
                ChannelIdleBehavior.StopOnDisconnect,
                true,
                true,
                []),
            cancellationToken);

        foreach (BaseError error in maybeChannel.LeftToSeq())
        {
            return error;
        }

        int channelId = maybeChannel.RightToSeq().Head().ChannelId;

        // schedule that floods the collection chronologically (oldest to newest)
        Either<BaseError, CreateProgramScheduleResult> maybeSchedule = await mediator.Send(
            new CreateProgramSchedule(manifest.Name, false, false, false, false, FixedStartTimeBehavior.Strict),
            cancellationToken);

        foreach (BaseError error in maybeSchedule.LeftToSeq())
        {
            return error;
        }

        int scheduleId = maybeSchedule.RightToSeq().Head().ProgramScheduleId;

        var scheduleItem = new ReplaceProgramScheduleItem(
            1,
            StartType.Dynamic,
            null,
            null,
            PlayoutMode.Flood,
            CollectionType.SmartCollection,
            null,
            null,
            collection.Id,
            null,
            null,
            null,
            null,
            null,
            PlaybackOrder.Chronological,
            MarathonGroupBy.None,
            false,
            false,
            null,
            FillWithGroupMode.None,
            MultipleMode.Count,
            null,
            null,
            TailMode.None,
            null,
            null,
            GuideMode.Normal,
            null,
            null,
            null,
            null,
            null,
            [],
            [],
            null,
            null,
            null,
            null);

        Either<BaseError, IEnumerable<ProgramScheduleItemViewModel>> maybeItems = await mediator.Send(
            new ReplaceProgramScheduleItems(scheduleId, [scheduleItem]),
            cancellationToken);

        foreach (BaseError error in maybeItems.LeftToSeq())
        {
            return error;
        }

        Either<BaseError, CreatePlayoutResponse> maybePlayout = await mediator.Send(
            new CreateClassicPlayout(channelId, scheduleId),
            cancellationToken);

        foreach (BaseError error in maybePlayout.LeftToSeq())
        {
            return error;
        }

        logger.LogInformation(
            "Created channel station {Name} (channel {Number}) for YouTube import",
            manifest.Name,
            maxNumber + 1);

        return channelId;
    }
}
