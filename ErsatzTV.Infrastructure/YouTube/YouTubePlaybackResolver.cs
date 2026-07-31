using System.Collections.Concurrent;
using System.Threading.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Infrastructure.YouTube;

public interface IYouTubePlaybackResolver
{
    // returns a playback path for downloader-managed remote streams:
    // the cached local file when available, otherwise a live yt-dlp proxy url;
    // also enqueues downloads for the next videos in the same playout
    Task<Option<string>> ResolvePathAndPrefetch(
        TvContext dbContext,
        PlayoutItem playoutItem,
        RemoteStream remoteStream,
        CancellationToken cancellationToken);

    Option<string> VideoIdForRemoteStream(RemoteStream remoteStream);

    bool IsDownloaderManaged(RemoteStream remoteStream);
}

public class YouTubePlaybackResolver(
    IYtDlpService ytDlpService,
    ChannelWriter<IYtDlpWorkerRequest> workerChannel,
    ILogger<YouTubePlaybackResolver> logger) : IYouTubePlaybackResolver
{
    private const int PrefetchCount = 2;

    private static readonly ConcurrentDictionary<string, (DateTime LastWrite, string Downloader)> DefinitionCache =
        new();

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public async Task<Option<string>> ResolvePathAndPrefetch(
        TvContext dbContext,
        PlayoutItem playoutItem,
        RemoteStream remoteStream,
        CancellationToken cancellationToken)
    {
        if (!IsDownloaderManaged(remoteStream))
        {
            return None;
        }

        // as soon as this video starts playing, download the next videos in the playout
        try
        {
            if (playoutItem.PlayoutId > 0)
            {
                List<int> nextIds = await dbContext.PlayoutItems
                    .AsNoTracking()
                    .Where(pi =>
                        pi.PlayoutId == playoutItem.PlayoutId &&
                        pi.Start > playoutItem.Start &&
                        pi.MediaItem is RemoteStream)
                    .OrderBy(pi => pi.Start)
                    .Select(pi => pi.MediaItemId)
                    .Take(12)
                    .ToListAsync(cancellationToken);

                foreach (int mediaItemId in nextIds
                             .Where(id => id != remoteStream.Id)
                             .Distinct()
                             .Take(PrefetchCount))
                {
                    await workerChannel.WriteAsync(new DownloadYouTubeVideo(mediaItemId), cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to enqueue YouTube prefetch downloads");
        }

        foreach (string videoId in VideoIdForRemoteStream(remoteStream))
        {
            foreach (string cached in ytDlpService.GetCachedFile(videoId))
            {
                try
                {
                    File.SetLastAccessTimeUtc(cached, DateTime.UtcNow);
                }
                catch (Exception)
                {
                    // ignored - last access time is only used for cache eviction ordering
                }

                logger.LogDebug("Playing YouTube video {VideoId} from cache", videoId);
                return cached;
            }
        }

        // not downloaded yet; stream live through yt-dlp
        return $"http://localhost:{ErsatzTV.Core.Settings.StreamingPort}/ffmpeg/ytdlp/{remoteStream.Id}";
    }

    public Option<string> VideoIdForRemoteStream(RemoteStream remoteStream)
    {
        foreach (string videoId in VideoIdFromUrl(remoteStream.Url))
        {
            return videoId;
        }

        string path = remoteStream.GetHeadVersion().MediaFiles.Head().Path;
        string fileName = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(fileName) ? None : Some(fileName);
    }

    public bool IsDownloaderManaged(RemoteStream remoteStream)
    {
        try
        {
            string path = remoteStream.GetHeadVersion().MediaFiles.Head().Path;
            if (!File.Exists(path))
            {
                return false;
            }

            DateTime lastWrite = File.GetLastWriteTimeUtc(path);
            if (DefinitionCache.TryGetValue(path, out (DateTime LastWrite, string Downloader) cached) &&
                cached.LastWrite == lastWrite)
            {
                return !string.IsNullOrWhiteSpace(cached.Downloader);
            }

            YamlRemoteStreamDefinition definition =
                Deserializer.Deserialize<YamlRemoteStreamDefinition>(File.ReadAllText(path));

            DefinitionCache[path] = (lastWrite, definition?.Downloader);

            return !string.IsNullOrWhiteSpace(definition?.Downloader);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to check remote stream definition for downloader");
            return false;
        }
    }

    private static Option<string> VideoIdFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            return None;
        }

        if (uri.Host.Contains("youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            string id = uri.AbsolutePath.Trim('/');
            return string.IsNullOrWhiteSpace(id) ? None : Some(id);
        }

        if (uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
        {
            System.Collections.Specialized.NameValueCollection query =
                System.Web.HttpUtility.ParseQueryString(uri.Query);
            string v = query["v"];
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v;
            }

            string path = uri.AbsolutePath;
            foreach (string prefix in new[] { "/shorts/", "/live/", "/embed/" })
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string id = path[prefix.Length..].Trim('/');
                    return string.IsNullOrWhiteSpace(id) ? None : Some(id);
                }
            }
        }

        return None;
    }
}
