namespace ErsatzTV.Core.YouTube;

public enum YouTubeImportKind
{
    Video = 0,
    Playlist = 1,
    Channel = 2,
    Search = 3
}

public record YtDlpVideo(
    string Id,
    string Title,
    string WebpageUrl,
    string ChannelName,
    double? DurationSeconds,
    string ThumbnailUrl,
    DateTime? UploadDate,
    string Description);

public record YtDlpQueryResult(
    YouTubeImportKind Kind,
    string Id,
    string Title,
    string ChannelName,
    string WebpageUrl,
    string ThumbnailUrl,
    List<YtDlpVideo> Videos);

public record YtDlpSettings(
    string YtDlpPath,
    string DenoPath,
    string Format,
    string ExtraArgs,
    int CacheMaxGb)
{
    public const string DefaultFormat = "bv*[height<=1080]+ba/b[height<=1080]/b";
    public const int DefaultCacheMaxGb = 30;
}
