using ErsatzTV.Core;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public record YouTubeImportResult(YouTubeImportManifest Manifest, int? ChannelId);

public record ImportYouTube(
    string Name,
    YtDlpQueryResult QueryResult,
    string IconUrl,
    bool AutoSync,
    int SyncIntervalHours,
    bool CreateStation) : IRequest<Either<BaseError, YouTubeImportResult>>;
