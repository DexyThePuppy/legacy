using System.Globalization;
using System.Threading.Channels;
using ErsatzTV.Application.Artworks;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.MediaCollections;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Application.ProgramSchedules;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Channels;

public class CreateChannelStationHandler(
    IMediator mediator,
    IDbContextFactory<TvContext> dbContextFactory,
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    ILogger<CreateChannelStationHandler> logger)
    : IRequestHandler<CreateChannelStation, Either<BaseError, CreateChannelStationResult>>
{
    public async Task<Either<BaseError, CreateChannelStationResult>> Handle(
        CreateChannelStation request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BaseError.New("Channel name is required");
        }

        int? createdCollectionId = null;
        int? createdChannelId = null;
        int? createdScheduleId = null;
        int? createdPlayoutId = null;
        bool collectionWasCreated = false;

        try
        {
            Either<BaseError, (int SmartCollectionId, bool Created)> maybeBinding =
                await ResolveContentBinding(request, cancellationToken);
            foreach (BaseError error in maybeBinding.LeftToSeq())
            {
                return error;
            }

            (int smartCollectionId, bool created) = maybeBinding.RightToSeq().Head();
            createdCollectionId = smartCollectionId;
            collectionWasCreated = created;

            Either<BaseError, string> maybeNumber = await ResolveChannelNumber(request.Number, cancellationToken);
            foreach (BaseError error in maybeNumber.LeftToSeq())
            {
                await Compensate(createdPlayoutId, createdScheduleId, createdChannelId, createdCollectionId, collectionWasCreated, cancellationToken);
                return error;
            }

            string number = maybeNumber.RightToSeq().Head();

            FFmpegSettingsViewModel ffmpegSettings = await mediator.Send(new GetFFmpegSettings(), cancellationToken);
            int ffmpegProfileId = request.FFmpegProfileId ?? ffmpegSettings.DefaultFFmpegProfileId;

            string group = string.IsNullOrWhiteSpace(request.Group) ? "ErsatzTV" : request.Group;

            Either<BaseError, CreateChannelResult> maybeChannel = await mediator.Send(
                new CreateChannel(
                    request.Name.Trim(),
                    number,
                    group,
                    request.Categories,
                    ffmpegProfileId,
                    null,
                    request.Logo,
                    ChannelStreamSelectorMode.Default,
                    null,
                    null,
                    null,
                    ChannelPlayoutSource.Generated,
                    request.PlayoutMode,
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
                await Compensate(createdPlayoutId, createdScheduleId, createdChannelId, createdCollectionId, collectionWasCreated, cancellationToken);
                return error;
            }

            createdChannelId = maybeChannel.RightToSeq().Head().ChannelId;

            string scheduleName = await UniqueScheduleName(request.Name.Trim(), number, cancellationToken);

            Either<BaseError, CreateProgramScheduleResult> maybeSchedule = await mediator.Send(
                new CreateProgramSchedule(
                    scheduleName,
                    false,
                    false,
                    false,
                    false,
                    FixedStartTimeBehavior.Strict),
                cancellationToken);

            foreach (BaseError error in maybeSchedule.LeftToSeq())
            {
                await Compensate(createdPlayoutId, createdScheduleId, createdChannelId, createdCollectionId, collectionWasCreated, cancellationToken);
                return error;
            }

            createdScheduleId = maybeSchedule.RightToSeq().Head().ProgramScheduleId;

            PlaybackOrder playbackOrder = request.PlaybackOrder is PlaybackOrder.Shuffle or PlaybackOrder.Random
                ? request.PlaybackOrder
                : PlaybackOrder.Chronological;

            var scheduleItem = new ReplaceProgramScheduleItem(
                1,
                StartType.Dynamic,
                null,
                null,
                PlayoutMode.Flood,
                CollectionType.SmartCollection,
                null,
                null,
                smartCollectionId,
                null,
                null,
                null,
                null,
                null,
                playbackOrder,
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
                new ReplaceProgramScheduleItems(createdScheduleId.Value, [scheduleItem]),
                cancellationToken);

            foreach (BaseError error in maybeItems.LeftToSeq())
            {
                await Compensate(createdPlayoutId, createdScheduleId, createdChannelId, createdCollectionId, collectionWasCreated, cancellationToken);
                return error;
            }

            // YouTube slug stations resolve via search index; defer hard-fail until items are indexed
            bool deferPlayoutBuild = request.DeferPlayoutBuild ||
                                     request.ContentKind is ChannelStationContentKind.YouTubeSlug;

            Either<BaseError, CreatePlayoutResponse> maybePlayout = await mediator.Send(
                new CreateClassicPlayout(
                    createdChannelId.Value,
                    createdScheduleId.Value,
                    QueueInitialBuild: !deferPlayoutBuild),
                cancellationToken);

            foreach (BaseError error in maybePlayout.LeftToSeq())
            {
                await Compensate(createdPlayoutId, createdScheduleId, createdChannelId, createdCollectionId, collectionWasCreated, cancellationToken);
                return error;
            }

            createdPlayoutId = maybePlayout.RightToSeq().Head().PlayoutId;

            if (deferPlayoutBuild)
            {
                // try once now; RebuildFailedPlayouts retries after search index updates
                await workerChannel.WriteAsync(
                    new BuildPlayout(createdPlayoutId.Value, PlayoutBuildMode.Reset),
                    cancellationToken);
            }

            // CreateChannel and CreateClassicPlayout already refresh; ensure one final refresh for consistency
            await workerChannel.WriteAsync(new RefreshChannelList(), cancellationToken);

            logger.LogInformation(
                "Created channel station {Name} (channel {Number}, id {ChannelId})",
                request.Name,
                number,
                createdChannelId);

            return new CreateChannelStationResult(
                createdChannelId.Value,
                createdPlayoutId.Value,
                createdScheduleId.Value,
                createdCollectionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create channel station {Name}", request.Name);
            await Compensate(
                createdPlayoutId,
                createdScheduleId,
                createdChannelId,
                createdCollectionId,
                collectionWasCreated,
                cancellationToken);
            return BaseError.New(ex.Message);
        }
    }

    private async Task<Either<BaseError, (int SmartCollectionId, bool Created)>> ResolveContentBinding(
        CreateChannelStation request,
        CancellationToken cancellationToken)
    {
        switch (request.ContentKind)
        {
            case ChannelStationContentKind.ExistingSmartCollection:
            {
                if (request.SmartCollectionId is null or <= 0)
                {
                    return BaseError.New("Smart collection is required");
                }

                Option<SmartCollectionViewModel> maybe = await mediator.Send(
                    new GetSmartCollectionById(request.SmartCollectionId.Value),
                    cancellationToken);

                if (maybe.IsNone)
                {
                    return BaseError.New($"Smart collection {request.SmartCollectionId} does not exist");
                }

                return (request.SmartCollectionId.Value, false);
            }
            case ChannelStationContentKind.YouTubeSlug:
            {
                if (string.IsNullOrWhiteSpace(request.YouTubeSlug))
                {
                    return BaseError.New("YouTube slug is required");
                }

                string query = $"type:remote_stream AND tag:\"youtube-{request.YouTubeSlug.Trim()}\"";
                return await CreateSmartCollectionForStation(request.Name, query, request.Number, cancellationToken);
            }
            case ChannelStationContentKind.SmartCollectionQuery:
            default:
            {
                if (string.IsNullOrWhiteSpace(request.SearchQuery))
                {
                    return BaseError.New("Search query is required for content binding");
                }

                return await CreateSmartCollectionForStation(
                    request.Name,
                    request.SearchQuery.Trim(),
                    request.Number,
                    cancellationToken);
            }
        }
    }

    private async Task<Either<BaseError, (int SmartCollectionId, bool Created)>> CreateSmartCollectionForStation(
        string name,
        string query,
        string numberHint,
        CancellationToken cancellationToken)
    {
        string collectionName = await UniqueSmartCollectionName(name.Trim(), numberHint, cancellationToken);

        Either<BaseError, SmartCollectionViewModel> maybeCollection = await mediator.Send(
            new CreateSmartCollection(query, collectionName),
            cancellationToken);

        return maybeCollection.Map(c => (c.Id, true));
    }

    private async Task<Either<BaseError, string>> ResolveChannelNumber(
        string requestedNumber,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(requestedNumber))
        {
            return requestedNumber.Trim();
        }

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

        return (maxNumber + 1).ToString(CultureInfo.InvariantCulture);
    }

    private async Task<string> UniqueSmartCollectionName(
        string baseName,
        string numberHint,
        CancellationToken cancellationToken)
    {
        string candidate = TruncateName(baseName);
        if (!await SmartCollectionNameExists(candidate, cancellationToken))
        {
            return candidate;
        }

        string withNumber = TruncateName($"{baseName} ({numberHint})");
        if (!string.IsNullOrWhiteSpace(numberHint) && !await SmartCollectionNameExists(withNumber, cancellationToken))
        {
            return withNumber;
        }

        for (var i = 2; i < 100; i++)
        {
            string numbered = TruncateName($"{baseName} ({i})");
            if (!await SmartCollectionNameExists(numbered, cancellationToken))
            {
                return numbered;
            }
        }

        return TruncateName($"{baseName} {Guid.NewGuid().ToString("N")[..8]}");
    }

    private async Task<string> UniqueScheduleName(
        string baseName,
        string number,
        CancellationToken cancellationToken)
    {
        string candidate = TruncateName(baseName);
        if (!await ScheduleNameExists(candidate, cancellationToken))
        {
            return candidate;
        }

        string withNumber = TruncateName($"{baseName} ({number})");
        if (!await ScheduleNameExists(withNumber, cancellationToken))
        {
            return withNumber;
        }

        for (var i = 2; i < 100; i++)
        {
            string numbered = TruncateName($"{baseName} ({i})");
            if (!await ScheduleNameExists(numbered, cancellationToken))
            {
                return numbered;
            }
        }

        return TruncateName($"{baseName} {Guid.NewGuid().ToString("N")[..8]}");
    }

    private async Task<bool> SmartCollectionNameExists(string name, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.SmartCollections.AnyAsync(c => c.Name == name, cancellationToken);
    }

    private async Task<bool> ScheduleNameExists(string name, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ProgramSchedules.AnyAsync(ps => ps.Name == name, cancellationToken);
    }

    private static string TruncateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Station";
        }

        return name.Length <= 50 ? name : name[..50];
    }

    private async Task Compensate(
        int? playoutId,
        int? scheduleId,
        int? channelId,
        int? collectionId,
        bool collectionWasCreated,
        CancellationToken cancellationToken)
    {
        try
        {
            if (playoutId is not null)
            {
                await mediator.Send(new DeletePlayout(playoutId.Value), cancellationToken);
            }

            if (scheduleId is not null)
            {
                // bypass playout guard only after playout delete; schedule may still be referenced if delete failed
                await mediator.Send(new DeleteProgramSchedule(scheduleId.Value), CancellationToken.None);
            }

            if (channelId is not null)
            {
                await mediator.Send(new DeleteChannel(channelId.Value), CancellationToken.None);
            }

            if (collectionWasCreated && collectionId is not null)
            {
                await mediator.Send(new DeleteSmartCollection(collectionId.Value), CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed while compensating for CreateChannelStation failure");
        }
    }
}
