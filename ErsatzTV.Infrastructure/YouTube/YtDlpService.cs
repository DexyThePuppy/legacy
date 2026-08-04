using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Infrastructure.YouTube;

public class YtDlpService(
    IConfigElementRepository configElementRepository,
    IFFmpegLocator ffmpegLocator,
    ILogger<YtDlpService> logger) : IYtDlpService
{
    private static readonly string[] CachedExtensions = [".mp4", ".mkv", ".webm"];

    public async Task<YtDlpSettings> GetSettings(CancellationToken cancellationToken)
    {
        Option<string> ytDlpPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.YtDlpPath,
            cancellationToken);

        Option<string> denoPath = await configElementRepository.GetValue<string>(
            ConfigElementKey.YtDlpDenoPath,
            cancellationToken);

        Option<string> format = await configElementRepository.GetValue<string>(
            ConfigElementKey.YtDlpFormat,
            cancellationToken);

        Option<string> extraArgs = await configElementRepository.GetValue<string>(
            ConfigElementKey.YtDlpExtraArgs,
            cancellationToken);

        Option<int> cacheMaxGb = await configElementRepository.GetValue<int>(
            ConfigElementKey.YtDlpCacheMaxGb,
            cancellationToken);

        return new YtDlpSettings(
            ytDlpPath.IfNone(string.Empty),
            denoPath.IfNone(string.Empty),
            format.Where(f => !string.IsNullOrWhiteSpace(f)).IfNone(YtDlpSettings.DefaultFormat),
            extraArgs.IfNone(string.Empty),
            cacheMaxGb.Where(gb => gb > 0).IfNone(YtDlpSettings.DefaultCacheMaxGb));
    }

    public Task<Option<string>> LocateYtDlp(CancellationToken cancellationToken) =>
        ffmpegLocator.ValidatePath("yt-dlp", ConfigElementKey.YtDlpPath, cancellationToken);

    public Task<Option<string>> LocateDeno(CancellationToken cancellationToken) =>
        ffmpegLocator.ValidatePath("deno", ConfigElementKey.YtDlpDenoPath, cancellationToken);

    public async Task<Either<BaseError, YtDlpQueryResult>> Query(string input, CancellationToken cancellationToken)
    {
        Option<string> maybeYtDlp = await LocateYtDlp(cancellationToken);
        if (maybeYtDlp.IsNone)
        {
            return BaseError.New("Unable to locate yt-dlp; install it or configure its path in YouTube settings");
        }

        string ytDlpPath = maybeYtDlp.ValueUnsafe();
        YtDlpSettings settings = await GetSettings(cancellationToken);
        bool isUrl = IsUrl(input);

        try
        {
            if (isUrl)
            {
                Either<BaseError, string> jsonResult = await RunYtDlpJson(
                    ytDlpPath,
                    NormalizeUrl(input),
                    settings.ExtraArgs,
                    cancellationToken);
                foreach (BaseError error in jsonResult.LeftToSeq())
                {
                    return error;
                }

                return ParseQueryResult(jsonResult.RightToSeq().Head(), isUrl: true);
            }

            // Text search: fetch matching channels and videos in parallel.
            string videoTarget = $"ytsearch25:{input}";
            string channelTarget =
                $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(input)}&sp=EgIQAg%253D%253D";

            Task<Either<BaseError, string>> videoJsonTask =
                RunYtDlpJson(ytDlpPath, videoTarget, settings.ExtraArgs, cancellationToken);
            Task<Either<BaseError, string>> channelJsonTask =
                RunYtDlpJson(ytDlpPath, channelTarget, settings.ExtraArgs, cancellationToken);
            await Task.WhenAll(videoJsonTask, channelJsonTask);

            Either<BaseError, string> videoJson = await videoJsonTask;
            Either<BaseError, string> channelJson = await channelJsonTask;

            List<YtDlpChannelHit> channels = [];
            foreach (string json in channelJson.RightToSeq())
            {
                channels = ParseChannelHits(json);
            }

            if (channelJson.IsLeft)
            {
                logger.LogDebug(
                    "YouTube channel search failed for {Query}: {Error}",
                    input,
                    channelJson.LeftToSeq().Head().Value);
            }

            foreach (BaseError error in videoJson.LeftToSeq())
            {
                // channels alone are still useful
                if (channels.Count > 0)
                {
                    return new YtDlpQueryResult(
                        YouTubeImportKind.Search,
                        input,
                        input,
                        string.Empty,
                        string.Empty,
                        channels[0].ThumbnailUrl,
                        [],
                        channels);
                }

                return error;
            }

            Either<BaseError, YtDlpQueryResult> parsedVideos = ParseQueryResult(videoJson.RightToSeq().Head(), isUrl: false);
            foreach (BaseError error in parsedVideos.LeftToSeq())
            {
                if (channels.Count > 0)
                {
                    return new YtDlpQueryResult(
                        YouTubeImportKind.Search,
                        input,
                        input,
                        string.Empty,
                        string.Empty,
                        channels[0].ThumbnailUrl,
                        [],
                        channels);
                }

                return error;
            }

            YtDlpQueryResult videos = parsedVideos.RightToSeq().Head();
            return videos with { Channels = channels };
        }
        catch (OperationCanceledException)
        {
            return BaseError.New("yt-dlp query was canceled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error querying yt-dlp");
            return BaseError.New(ex.Message);
        }
    }

    private async Task<Either<BaseError, string>> RunYtDlpJson(
        string ytDlpPath,
        string target,
        string extraArgs,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-J");
        startInfo.ArgumentList.Add("--flat-playlist");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--extractor-args");
        startInfo.ArgumentList.Add("youtubetab:approximate_date");

        using YtDlpPreparedExtraArgs prepared = YtDlpSettings.PrepareExtraArgs(extraArgs);
        foreach (string arg in prepared.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add(target);

        await AddPathEnvironment(startInfo, cancellationToken);

        using var process = new Process();
        process.StartInfo = startInfo;
        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string json = await stdOutTask;
        string err = await stdErrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(json))
        {
            logger.LogWarning("yt-dlp query failed for {Target}: {Error}", target, err);
            return BaseError.New(string.IsNullOrWhiteSpace(err) ? "yt-dlp query failed" : err.Trim());
        }

        return json;
    }

    public async Task<Either<BaseError, string>> DownloadVideo(
        string videoId,
        string webpageUrl,
        CancellationToken cancellationToken)
    {
        foreach (string existing in GetCachedFile(videoId))
        {
            return existing;
        }

        Option<string> maybeYtDlp = await LocateYtDlp(cancellationToken);
        if (maybeYtDlp.IsNone)
        {
            return BaseError.New("Unable to locate yt-dlp");
        }

        YtDlpSettings settings = await GetSettings(cancellationToken);

        Directory.CreateDirectory(FileSystemLayout.YouTubeCacheFolder);

        var startInfo = new ProcessStartInfo
        {
            FileName = maybeYtDlp.ValueUnsafe(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(settings.Format);
        startInfo.ArgumentList.Add("--merge-output-format");
        startInfo.ArgumentList.Add("mp4");
        startInfo.ArgumentList.Add("--no-warnings");
        startInfo.ArgumentList.Add("--no-progress");
        startInfo.ArgumentList.Add("--no-playlist");
        startInfo.ArgumentList.Add("--no-simulate");
        startInfo.ArgumentList.Add("--print");
        startInfo.ArgumentList.Add("after_move:filepath");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(Path.Combine(FileSystemLayout.YouTubeCacheFolder, "%(id)s.%(ext)s"));

        using YtDlpPreparedExtraArgs prepared = YtDlpSettings.PrepareExtraArgs(settings.ExtraArgs);
        foreach (string arg in prepared.Args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        startInfo.ArgumentList.Add(webpageUrl);

        await AddPathEnvironment(startInfo, cancellationToken);

        try
        {
            logger.LogInformation("Downloading YouTube video {VideoId}", videoId);

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            string stdOut = await stdOutTask;
            string err = await stdErrTask;

            if (process.ExitCode != 0)
            {
                logger.LogWarning("yt-dlp download of {VideoId} failed: {Error}", videoId, err);
                return BaseError.New(string.IsNullOrWhiteSpace(err) ? "yt-dlp download failed" : err.Trim());
            }

            string printedPath = stdOut
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(File.Exists);

            if (printedPath is not null)
            {
                logger.LogInformation("Downloaded YouTube video {VideoId} to {Path}", videoId, printedPath);
                return printedPath;
            }

            foreach (string cached in GetCachedFile(videoId))
            {
                return cached;
            }

            return BaseError.New($"yt-dlp download of {videoId} completed but no file was found");
        }
        catch (OperationCanceledException)
        {
            return BaseError.New("yt-dlp download was canceled");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error downloading YouTube video {VideoId}", videoId);
            return BaseError.New(ex.Message);
        }
    }

    public Option<string> GetCachedFile(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return None;
        }

        foreach (string extension in CachedExtensions)
        {
            string path = Path.Combine(FileSystemLayout.YouTubeCacheFolder, $"{videoId}{extension}");
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return path;
            }
        }

        return None;
    }

    public async Task EnforceCacheSize(CancellationToken cancellationToken)
    {
        try
        {
            YtDlpSettings settings = await GetSettings(cancellationToken);
            long maxBytes = (long)settings.CacheMaxGb * 1024 * 1024 * 1024;

            if (!Directory.Exists(FileSystemLayout.YouTubeCacheFolder))
            {
                return;
            }

            var files = new DirectoryInfo(FileSystemLayout.YouTubeCacheFolder)
                .GetFiles()
                .Where(f => CachedExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastAccessTimeUtc)
                .ToList();

            long total = 0;
            foreach (FileInfo file in files)
            {
                total += file.Length;
                if (total > maxBytes)
                {
                    logger.LogInformation("Removing {File} from YouTube cache (over size limit)", file.Name);
                    file.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error enforcing YouTube cache size");
        }
    }

    private async Task AddPathEnvironment(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        // ensure deno (required by yt-dlp for youtube signature solving) is on PATH
        Option<string> maybeDeno = await LocateDeno(cancellationToken);
        foreach (string denoPath in maybeDeno)
        {
            string denoDir = Path.GetDirectoryName(denoPath) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(denoDir))
            {
                string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                startInfo.Environment["PATH"] = $"{denoDir}{Path.PathSeparator}{path}";
            }
        }
    }

    private static Either<BaseError, YtDlpQueryResult> ParseQueryResult(string json, bool isUrl)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        string rootType = GetString(root, "_type") ?? "video";

        if (rootType == "playlist")
        {
            var videos = new List<YtDlpVideo>();
            if (root.TryGetProperty("entries", out JsonElement entries) && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in entries.EnumerateArray())
                {
                    Option<YtDlpVideo> maybeVideo = ParseVideo(entry);
                    foreach (YtDlpVideo video in maybeVideo)
                    {
                        videos.Add(video);
                    }
                }
            }

            string webpageUrl = GetString(root, "webpage_url") ?? GetString(root, "original_url") ?? string.Empty;

            YouTubeImportKind kind = !isUrl
                ? YouTubeImportKind.Search
                : IsChannelUrl(webpageUrl)
                    ? YouTubeImportKind.Channel
                    : YouTubeImportKind.Playlist;

            string title = GetString(root, "title") ?? GetString(root, "id") ?? "YouTube";
            string channelName = GetString(root, "channel") ?? GetString(root, "uploader") ?? string.Empty;

            string thumbnailUrl = GetThumbnail(root) ?? videos.Select(v => v.ThumbnailUrl).FirstOrDefault(t => t != null);

            return new YtDlpQueryResult(
                kind,
                GetString(root, "id") ?? string.Empty,
                title,
                channelName,
                webpageUrl,
                thumbnailUrl,
                videos,
                []);
        }

        // single video
        Option<YtDlpVideo> maybeSingle = ParseVideo(root);
        foreach (YtDlpVideo video in maybeSingle)
        {
            return new YtDlpQueryResult(
                YouTubeImportKind.Video,
                video.Id,
                video.Title,
                video.ChannelName,
                video.WebpageUrl,
                video.ThumbnailUrl,
                [video],
                []);
        }

        return BaseError.New("Unable to parse yt-dlp output");
    }

    private static List<YtDlpChannelHit> ParseChannelHits(string json)
    {
        var channels = new List<YtDlpChannelHit>();
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (!root.TryGetProperty("entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return channels;
        }

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            foreach (YtDlpChannelHit hit in ParseChannelHit(entry))
            {
                if (channels.All(c => c.Id != hit.Id))
                {
                    channels.Add(hit);
                }
            }
        }

        return channels;
    }

    private static Option<YtDlpChannelHit> ParseChannelHit(JsonElement entry)
    {
        if (!IsChannelEntry(entry))
        {
            return None;
        }

        string id = GetString(entry, "id") ?? GetString(entry, "channel_id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return None;
        }

        string title = GetString(entry, "title") ?? GetString(entry, "channel") ?? GetString(entry, "uploader") ?? id;
        string url = GetString(entry, "url") ?? GetString(entry, "webpage_url") ??
                     $"https://www.youtube.com/channel/{id}";
        string thumbnail = AbsolutizeUrl(GetThumbnail(entry));

        return new YtDlpChannelHit(id, title, url, thumbnail);
    }

    private static bool IsChannelEntry(JsonElement entry)
    {
        string ieKey = GetString(entry, "ie_key") ?? string.Empty;
        if (ieKey.Equals("YoutubeTab", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string url = GetString(entry, "url") ?? GetString(entry, "webpage_url") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(url) && IsChannelUrl(url))
        {
            return true;
        }

        string id = GetString(entry, "id") ?? string.Empty;
        return id.StartsWith("UC", StringComparison.Ordinal) && id.Length >= 24;
    }

    private static Option<YtDlpVideo> ParseVideo(JsonElement entry)
    {
        if (IsChannelEntry(entry))
        {
            return None;
        }

        string id = GetString(entry, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return None;
        }

        string liveStatus = GetString(entry, "live_status");
        if (liveStatus is "is_live" or "is_upcoming")
        {
            return None;
        }

        string title = GetString(entry, "title") ?? id;
        string url = GetString(entry, "webpage_url") ?? GetString(entry, "url") ?? $"https://www.youtube.com/watch?v={id}";
        string channel = GetString(entry, "channel") ?? GetString(entry, "uploader") ?? string.Empty;
        string description = GetString(entry, "description");

        double? duration = null;
        if (entry.TryGetProperty("duration", out JsonElement durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number)
        {
            duration = durationElement.GetDouble();
        }

        DateTime? uploadDate = null;
        string uploadDateString = GetString(entry, "upload_date");
        if (!string.IsNullOrWhiteSpace(uploadDateString) && DateTime.TryParseExact(
                uploadDateString,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime parsed))
        {
            uploadDate = parsed;
        }

        string thumbnail = GetThumbnail(entry);
        if (thumbnail is null && id.Length == 11)
        {
            // deterministic youtube thumbnail url
            thumbnail = $"https://i.ytimg.com/vi/{id}/hqdefault.jpg";
        }

        return new YtDlpVideo(id, title, url, channel, duration, thumbnail, uploadDate, description);
    }

    private static string GetThumbnail(JsonElement element)
    {
        string thumbnail = GetString(element, "thumbnail");
        if (!string.IsNullOrWhiteSpace(thumbnail))
        {
            return AbsolutizeUrl(thumbnail);
        }

        if (element.TryGetProperty("thumbnails", out JsonElement thumbnails) &&
            thumbnails.ValueKind == JsonValueKind.Array)
        {
            string result = null;
            foreach (JsonElement thumb in thumbnails.EnumerateArray())
            {
                string url = GetString(thumb, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    result = url;
                }
            }

            return AbsolutizeUrl(result);
        }

        return null;
    }

    private static string AbsolutizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        return url;
    }

    private static string GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsUrl(string input) =>
        Uri.TryCreate(input, UriKind.Absolute, out Uri uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsChannelUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri uri))
        {
            return false;
        }

        string path = uri.AbsolutePath;
        return path.StartsWith("/@", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/channel/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/c/", StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith("/user/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeUrl(string input)
    {
        if (!Uri.TryCreate(input, UriKind.Absolute, out Uri uri))
        {
            return input;
        }

        // for a channel root url, import the main videos tab
        bool isYouTube = uri.Host.Contains("youtube.com", StringComparison.OrdinalIgnoreCase);
        string path = uri.AbsolutePath.TrimEnd('/');

        if (isYouTube && IsChannelUrl(input) &&
            !path.EndsWith("/videos", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/shorts", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/streams", StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("/playlists", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host}{path}/videos";
        }

        return input;
    }

}
