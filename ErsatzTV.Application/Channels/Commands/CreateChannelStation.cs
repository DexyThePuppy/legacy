using ErsatzTV.Application.Artworks;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Channels;

public enum ChannelStationContentKind
{
    SmartCollectionQuery = 0,
    YouTubeSlug = 1,
    ExistingSmartCollection = 2
}

public record CreateChannelStationResult(
    int ChannelId,
    int PlayoutId,
    int ScheduleId,
    int? CollectionId);

/// <summary>
///     Creates a watchable station: content binding → channel → flood schedule → classic playout.
///     Mirror is refused; use channel editor to mirror an existing generated station.
/// </summary>
public record CreateChannelStation(
    string Name,
    string Number,
    string Group,
    string Categories,
    ArtworkContentTypeModel Logo,
    int? FFmpegProfileId,
    ChannelStationContentKind ContentKind,
    string SearchQuery,
    string YouTubeSlug,
    int? SmartCollectionId,
    PlaybackOrder PlaybackOrder,
    ChannelPlayoutMode PlayoutMode,
    bool DeferPlayoutBuild = false) : IRequest<Either<BaseError, CreateChannelStationResult>>;
