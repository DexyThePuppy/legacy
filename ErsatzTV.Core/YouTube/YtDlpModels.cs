using System.Text;

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

public record YtDlpChannelHit(
    string Id,
    string Title,
    string WebpageUrl,
    string ThumbnailUrl);

public record YtDlpQueryResult(
    YouTubeImportKind Kind,
    string Id,
    string Title,
    string ChannelName,
    string WebpageUrl,
    string ThumbnailUrl,
    List<YtDlpVideo> Videos,
    List<YtDlpChannelHit> Channels)
{
    public YtDlpQueryResult(
        YouTubeImportKind kind,
        string id,
        string title,
        string channelName,
        string webpageUrl,
        string thumbnailUrl,
        List<YtDlpVideo> videos)
        : this(kind, id, title, channelName, webpageUrl, thumbnailUrl, videos, [])
    {
    }
}

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
        var current = new StringBuilder();
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

    /// <summary>
    /// Prepares ExtraArgs for a yt-dlp process. Copies <c>--cookies</c> files to a writable
    /// temp path (BOM-stripped) so yt-dlp can save the jar without overwriting the user file
    /// or crashing on a read-only source.
    /// </summary>
    public static YtDlpPreparedExtraArgs PrepareExtraArgs(string args) =>
        YtDlpPreparedExtraArgs.Create(args);
}

/// <summary>
/// Extra yt-dlp args with an optional staged cookie jar that must be disposed after the process exits.
/// </summary>
public sealed class YtDlpPreparedExtraArgs : IDisposable
{
    private readonly string _tempCookieFile;

    private YtDlpPreparedExtraArgs(IReadOnlyList<string> args, string tempCookieFile)
    {
        Args = args;
        _tempCookieFile = tempCookieFile;
    }

    public IReadOnlyList<string> Args { get; }

    public static YtDlpPreparedExtraArgs Create(string extraArgs)
    {
        var list = YtDlpSettings.SplitExtraArgs(extraArgs).ToList();
        string tempCookieFile = null;

        for (var i = 0; i < list.Count - 1; i++)
        {
            if (!string.Equals(list[i], "--cookies", StringComparison.Ordinal))
            {
                continue;
            }

            string source = list[i + 1];
            if (!File.Exists(source))
            {
                break;
            }

            tempCookieFile = Path.Combine(
                Path.GetTempPath(),
                $"ersatztv-ytdlp-cookies-{Guid.NewGuid():N}.txt");
            CopyCookiesWithoutBom(source, tempCookieFile);
            list[i + 1] = tempCookieFile;
            break;
        }

        return new YtDlpPreparedExtraArgs(list, tempCookieFile);
    }

    private static void CopyCookiesWithoutBom(string source, string destination)
    {
        byte[] bytes = File.ReadAllBytes(source);
        var offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            offset = 3;
        }

        if (offset == 0)
        {
            File.WriteAllBytes(destination, bytes);
        }
        else
        {
            File.WriteAllBytes(destination, bytes.AsSpan(offset).ToArray());
        }
    }

    public void Dispose()
    {
        if (string.IsNullOrEmpty(_tempCookieFile))
        {
            return;
        }

        try
        {
            File.Delete(_tempCookieFile);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
