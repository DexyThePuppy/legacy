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

    public static IEnumerable<string> SplitExtraArgs(string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            yield break;
        }

        // support quoted paths, e.g. --cookies "C:\path with spaces\cookies.txt"
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < args.Length; i++)
        {
            char c = args[i];
            if (c is '"' or '\'')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}
