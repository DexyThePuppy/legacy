using System.Globalization;
using System.Text;
using System.Text.Json;
using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Core.YouTube;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Infrastructure.YouTube;

public class YouTubeImportService(IYtDlpService ytDlpService, ILogger<YouTubeImportService> logger)
    : IYouTubeImportService
{
    private static readonly DateTime PseudoDateEpoch = new(2005, 4, 23);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public async Task<List<YouTubeImportManifest>> ListImports(CancellationToken cancellationToken)
    {
        var result = new List<YouTubeImportManifest>();

        if (!Directory.Exists(FileSystemLayout.YouTubeLibraryFolder))
        {
            return result;
        }

        foreach (string folder in Directory.EnumerateDirectories(FileSystemLayout.YouTubeLibraryFolder))
        {
            string manifestPath = Path.Combine(folder, YouTubeImportManifest.FileName);
            if (File.Exists(manifestPath))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                    YouTubeImportManifest manifest = JsonSerializer.Deserialize<YouTubeImportManifest>(json);
                    if (manifest is not null)
                    {
                        result.Add(manifest);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to read YouTube import manifest at {Path}", manifestPath);
                }
            }
        }

        return result.OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<Option<YouTubeImportManifest>> GetImport(string slug, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(GetImportFolder(slug), YouTubeImportManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return None;
        }

        try
        {
            string json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return Optional(JsonSerializer.Deserialize<YouTubeImportManifest>(json));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read YouTube import manifest for {Slug}", slug);
            return None;
        }
    }

    public async Task<Either<BaseError, YouTubeImportManifest>> CreateImport(
        YtDlpQueryResult queryResult,
        string name,
        string iconUrl,
        bool autoSync,
        int syncIntervalHours,
        int libraryId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (queryResult.Videos.Count == 0)
            {
                return BaseError.New("There are no videos to import");
            }

            string displayName = string.IsNullOrWhiteSpace(name) ? queryResult.Title : name.Trim();
            string slug = GetUniqueSlug(displayName);
            string folder = GetImportFolder(slug);
            Directory.CreateDirectory(folder);

            var manifest = new YouTubeImportManifest
            {
                Name = displayName,
                Kind = queryResult.Kind,
                Url = queryResult.WebpageUrl,
                ChannelName = queryResult.ChannelName,
                IconUrl = string.IsNullOrWhiteSpace(iconUrl) ? queryResult.ThumbnailUrl : iconUrl,
                Slug = slug,
                LibraryId = libraryId,
                AutoSync = autoSync,
                SyncIntervalHours = Math.Max(1, syncIntervalHours),
                CreatedUtc = DateTime.UtcNow,
                LastSyncUtc = DateTime.UtcNow,
                VideoCount = 0,
                NextIndex = 0
            };

            foreach (YtDlpVideo video in OrderForImport(queryResult.Kind, queryResult.Videos))
            {
                await WriteVideoYaml(folder, manifest, video, cancellationToken);
            }

            manifest.VideoCount = CountVideoFiles(folder);
            await SaveManifest(manifest, cancellationToken);

            logger.LogInformation(
                "Created YouTube import {Name} ({Slug}) with {Count} videos",
                manifest.Name,
                slug,
                manifest.VideoCount);

            return manifest;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to create YouTube import");
            return BaseError.New(ex.Message);
        }
    }

    public async Task<Either<BaseError, YouTubeImportSyncResult>> SyncImport(
        string slug,
        CancellationToken cancellationToken)
    {
        Option<YouTubeImportManifest> maybeManifest = await GetImport(slug, cancellationToken);
        if (maybeManifest.IsNone)
        {
            return BaseError.New($"YouTube import {slug} does not exist");
        }

        YouTubeImportManifest manifest = maybeManifest.ValueUnsafe();
        string folder = GetImportFolder(slug);

        Either<BaseError, YtDlpQueryResult> maybeResult = await ytDlpService.Query(manifest.Url, cancellationToken);
        foreach (BaseError error in maybeResult.LeftToSeq())
        {
            return error;
        }

        YtDlpQueryResult queryResult = maybeResult.RightToSeq().Head();

        var incoming = OrderForImport(manifest.Kind, queryResult.Videos)
            .DistinctBy(v => v.Id)
            .ToList();

        var incomingIds = incoming.Map(v => v.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var existingIds = Directory.EnumerateFiles(folder, "*.yml")
            .Map(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var removed = 0;

        foreach (YtDlpVideo video in incoming.Where(v => !existingIds.Contains(v.Id)))
        {
            await WriteVideoYaml(folder, manifest, video, cancellationToken);
            added++;
        }

        foreach (string existingId in existingIds.Where(id => !incomingIds.Contains(id)))
        {
            File.Delete(Path.Combine(folder, $"{existingId}.yml"));

            string thumbnailPath = Path.Combine(folder, $"{existingId}.jpg");
            if (File.Exists(thumbnailPath))
            {
                File.Delete(thumbnailPath);
            }

            removed++;
        }

        manifest.LastSyncUtc = DateTime.UtcNow;
        manifest.VideoCount = CountVideoFiles(folder);
        await SaveManifest(manifest, cancellationToken);

        if (added > 0 || removed > 0)
        {
            logger.LogInformation(
                "Synced YouTube import {Name}: {Added} added, {Removed} removed",
                manifest.Name,
                added,
                removed);
        }

        return new YouTubeImportSyncResult(added, removed);
    }

    public async Task<Either<BaseError, Unit>> UpdateImport(
        string slug,
        string name,
        string iconUrl,
        bool autoSync,
        int syncIntervalHours,
        CancellationToken cancellationToken)
    {
        Option<YouTubeImportManifest> maybeManifest = await GetImport(slug, cancellationToken);
        if (maybeManifest.IsNone)
        {
            return BaseError.New($"YouTube import {slug} does not exist");
        }

        YouTubeImportManifest manifest = maybeManifest.ValueUnsafe();

        if (!string.IsNullOrWhiteSpace(name))
        {
            manifest.Name = name.Trim();
        }

        if (!string.IsNullOrWhiteSpace(iconUrl))
        {
            manifest.IconUrl = iconUrl.Trim();
        }

        manifest.AutoSync = autoSync;
        manifest.SyncIntervalHours = Math.Max(1, syncIntervalHours);

        await SaveManifest(manifest, cancellationToken);
        return Unit.Default;
    }

    public Task<Either<BaseError, Unit>> DeleteImport(string slug, CancellationToken cancellationToken)
    {
        try
        {
            string folder = GetImportFolder(slug);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, true);
            }

            return Task.FromResult<Either<BaseError, Unit>>(Unit.Default);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete YouTube import {Slug}", slug);
            return Task.FromResult<Either<BaseError, Unit>>(BaseError.New(ex.Message));
        }
    }

    public string GetImportFolder(string slug) => Path.Combine(FileSystemLayout.YouTubeLibraryFolder, slug);

    // channel tabs and search results are newest-first; reverse so oldest comes first
    private static IEnumerable<YtDlpVideo> OrderForImport(YouTubeImportKind kind, List<YtDlpVideo> videos) =>
        kind is YouTubeImportKind.Channel or YouTubeImportKind.Search
            ? Enumerable.Reverse(videos)
            : videos;

    private static async Task WriteVideoYaml(
        string folder,
        YouTubeImportManifest manifest,
        YtDlpVideo video,
        CancellationToken cancellationToken)
    {
        int index = manifest.NextIndex++;

        var tags = new List<string>();
        string channelName = string.IsNullOrWhiteSpace(video.ChannelName) ? manifest.ChannelName : video.ChannelName;
        if (!string.IsNullOrWhiteSpace(channelName))
        {
            tags.Add(channelName);
        }

        tags.Add($"youtube-{manifest.Slug}");

        // pseudo release date preserves exact source ordering for chronological playback
        DateTime releaseDate = PseudoDateEpoch.AddDays(index);

        var definition = new YamlRemoteStreamDefinition
        {
            Url = video.WebpageUrl,
            Downloader = "yt-dlp",
            IsLive = false,
            Title = video.Title,
            Plot = string.IsNullOrWhiteSpace(video.Description) ? null : video.Description,
            Year = video.UploadDate?.Year,
            ReleaseDate = releaseDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Tags = tags,
            Thumbnail = video.ThumbnailUrl
        };

        if (video.DurationSeconds is > 0)
        {
            definition.Duration = TimeSpan.FromSeconds(video.DurationSeconds.Value)
                .ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        string yaml = YamlSerializer.Serialize(definition);
        string path = Path.Combine(folder, $"{video.Id}.yml");
        await File.WriteAllTextAsync(path, yaml, Encoding.UTF8, cancellationToken);
    }

    private async Task SaveManifest(YouTubeImportManifest manifest, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(GetImportFolder(manifest.Slug), YouTubeImportManifest.FileName);
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
    }

    private static int CountVideoFiles(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.yml").Count() : 0;

    private string GetUniqueSlug(string name)
    {
        var builder = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
            else if (c is ' ' or '-' or '_' && builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        string slug = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "import";
        }

        if (slug.Length > 40)
        {
            slug = slug[..40].Trim('-');
        }

        string candidate = slug;
        var suffix = 2;
        while (Directory.Exists(GetImportFolder(candidate)))
        {
            candidate = $"{slug}-{suffix++}";
        }

        return candidate;
    }
}
