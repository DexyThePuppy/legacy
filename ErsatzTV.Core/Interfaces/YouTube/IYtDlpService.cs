using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Core.Interfaces.YouTube;

public interface IYtDlpService
{
    Task<YtDlpSettings> GetSettings(CancellationToken cancellationToken);

    Task<Option<string>> LocateYtDlp(CancellationToken cancellationToken);

    Task<Option<string>> LocateDeno(CancellationToken cancellationToken);

    // input may be a video/playlist/channel url or a plain text search query
    Task<Either<BaseError, YtDlpQueryResult>> Query(string input, CancellationToken cancellationToken);

    // downloads a video to the youtube cache folder; returns the path of the completed file
    Task<Either<BaseError, string>> DownloadVideo(
        string videoId,
        string webpageUrl,
        CancellationToken cancellationToken);

    // returns the cached file for a video id, if one exists
    Option<string> GetCachedFile(string videoId);

    // deletes least recently used cached files until the cache fits the configured size
    Task EnforceCacheSize(CancellationToken cancellationToken);
}
