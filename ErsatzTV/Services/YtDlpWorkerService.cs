using System.Collections.Concurrent;
using System.Threading.Channels;
using ErsatzTV.Application;
using ErsatzTV.Application.MediaSources;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Core.YouTube;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.YouTube;
using Microsoft.EntityFrameworkCore;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Services;

public class YtDlpWorkerService : BackgroundService
{
    private const int MaxConcurrentDownloads = 2;

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private readonly ChannelReader<IYtDlpWorkerRequest> _channel;
    private readonly ChannelWriter<IYtDlpWorkerRequest> _channelWriter;
    private readonly ChannelWriter<IScannerBackgroundServiceRequest> _scannerChannel;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlpWorkerService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    private static readonly TimeSpan AuthFailureCooldown = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, bool> _inFlightDownloads = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _downloadCooldowns = new();
    private readonly SemaphoreSlim _downloadSemaphore = new(MaxConcurrentDownloads);
    private readonly SemaphoreSlim _maintenanceSemaphore = new(1);

    public YtDlpWorkerService(
        ChannelReader<IYtDlpWorkerRequest> channel,
        ChannelWriter<IYtDlpWorkerRequest> channelWriter,
        ChannelWriter<IScannerBackgroundServiceRequest> scannerChannel,
        IHttpClientFactory httpClientFactory,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<YtDlpWorkerService> logger)
    {
        _channel = channel;
        _channelWriter = channelWriter;
        _scannerChannel = scannerChannel;
        _httpClientFactory = httpClientFactory;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            _logger.LogInformation("yt-dlp worker service started");

            await foreach (IYtDlpWorkerRequest request in _channel.ReadAllAsync(stoppingToken))
            {
                switch (request)
                {
                    case DownloadYouTubeVideo download:
                        _ = Task.Run(() => DownloadVideo(download.RemoteStreamId, stoppingToken), stoppingToken);
                        break;

                    case FetchYouTubeThumbnails thumbnails:
                        _ = Task.Run(() => FetchThumbnails(thumbnails.Slug, stoppingToken), stoppingToken);
                        break;

                    case SyncYouTubeImportRequest sync:
                        _ = Task.Run(() => SyncImport(sync.Slug, stoppingToken), stoppingToken);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            _logger.LogInformation("yt-dlp worker service shutting down");
        }
    }

    private async Task DownloadVideo(int remoteStreamId, CancellationToken cancellationToken)
    {
        await _downloadSemaphore.WaitAsync(cancellationToken);
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();

            IDbContextFactory<TvContext> dbContextFactory =
                scope.ServiceProvider.GetRequiredService<IDbContextFactory<TvContext>>();
            IYouTubePlaybackResolver resolver =
                scope.ServiceProvider.GetRequiredService<IYouTubePlaybackResolver>();
            IYtDlpService ytDlpService = scope.ServiceProvider.GetRequiredService<IYtDlpService>();

            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            Option<RemoteStream> maybeRemoteStream = await dbContext.RemoteStreams
                .AsNoTracking()
                .Include(rs => rs.MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .SelectOneAsync(rs => rs.Id, rs => rs.Id == remoteStreamId, cancellationToken);

            foreach (RemoteStream remoteStream in maybeRemoteStream)
            {
                if (!resolver.IsDownloaderManaged(remoteStream))
                {
                    return;
                }

                Option<string> maybeVideoId = resolver.VideoIdForRemoteStream(remoteStream);
                foreach (string videoId in maybeVideoId)
                {
                    if (ytDlpService.GetCachedFile(videoId).IsSome)
                    {
                        return;
                    }

                    if (_downloadCooldowns.TryGetValue(videoId, out DateTimeOffset cooldownUntil) &&
                        cooldownUntil > DateTimeOffset.UtcNow)
                    {
                        _logger.LogDebug(
                            "Skipping YouTube download for {VideoId}; cooling down until {Until}",
                            videoId,
                            cooldownUntil);
                        return;
                    }

                    if (!_inFlightDownloads.TryAdd(videoId, true))
                    {
                        return;
                    }

                    try
                    {
                        Either<BaseError, string> maybeFile = await ytDlpService.DownloadVideo(
                            videoId,
                            remoteStream.Url,
                            cancellationToken);

                        foreach (BaseError error in maybeFile.LeftToSeq())
                        {
                            _logger.LogWarning(
                                "Failed to download YouTube video {VideoId}: {Error}",
                                videoId,
                                error.Value);

                            if (IsAuthOrAgeGateFailure(error.Value))
                            {
                                _downloadCooldowns[videoId] = DateTimeOffset.UtcNow.Add(AuthFailureCooldown);
                                _logger.LogWarning(
                                    "YouTube video {VideoId} needs cookies/login; will not retry for {Hours} hours",
                                    videoId,
                                    AuthFailureCooldown.TotalHours);
                            }
                        }

                        foreach (string file in maybeFile.RightToSeq())
                        {
                            _downloadCooldowns.TryRemove(videoId, out _);
                            await RefreshStatisticsFromFile(scope, dbContext, remoteStream, file, cancellationToken);
                            await ytDlpService.EnforceCacheSize(cancellationToken);
                        }
                    }
                    finally
                    {
                        _inFlightDownloads.TryRemove(videoId, out _);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to download YouTube video for remote stream {Id}", remoteStreamId);
        }
        finally
        {
            _downloadSemaphore.Release();
        }
    }

    private static bool IsAuthOrAgeGateFailure(string error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("confirm your age", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("cookies-from-browser", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Use --cookies", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Failed to decrypt with DPAPI", StringComparison.OrdinalIgnoreCase));

    private async Task RefreshStatisticsFromFile(
        IServiceScope scope,
        TvContext dbContext,
        RemoteStream remoteStream,
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            ILocalStatisticsProvider statisticsProvider =
                scope.ServiceProvider.GetRequiredService<ILocalStatisticsProvider>();
            IMetadataRepository metadataRepository =
                scope.ServiceProvider.GetRequiredService<IMetadataRepository>();

            Option<string> maybeFFprobePath = await dbContext.ConfigElements.GetValue<string>(
                ConfigElementKey.FFprobePath,
                cancellationToken);

            foreach (string ffprobePath in maybeFFprobePath)
            {
                Either<BaseError, MediaVersion> maybeVersion =
                    await statisticsProvider.GetStatistics(ffprobePath, file);

                foreach (MediaVersion version in maybeVersion.RightToSeq())
                {
                    // keep DateUpdated aligned with the yml definition so scans
                    // don't overwrite these real statistics with synthesized ones
                    string ymlPath = remoteStream.GetHeadVersion().MediaFiles.Head().Path;
                    if (File.Exists(ymlPath))
                    {
                        version.DateUpdated = File.GetLastWriteTimeUtc(ymlPath);
                    }

                    version.Chapters ??= [];

                    await metadataRepository.UpdateStatistics(remoteStream, version);
                    _logger.LogDebug("Updated statistics for downloaded YouTube video at {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh statistics for downloaded file {File}", file);
        }
    }

    private async Task FetchThumbnails(string slug, CancellationToken cancellationToken)
    {
        await _maintenanceSemaphore.WaitAsync(cancellationToken);
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            IYouTubeImportService importService =
                scope.ServiceProvider.GetRequiredService<IYouTubeImportService>();

            Option<YouTubeImportManifest> maybeManifest = await importService.GetImport(slug, cancellationToken);
            foreach (YouTubeImportManifest manifest in maybeManifest)
            {
                string folder = importService.GetImportFolder(slug);
                var downloaded = 0;

                HttpClient client = _httpClientFactory.CreateClient();

                var ymlFiles = Directory.EnumerateFiles(folder, "*.yml").ToList();
                await Parallel.ForEachAsync(
                    ymlFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
                    async (ymlFile, token) =>
                    {
                        string jpgFile = Path.ChangeExtension(ymlFile, "jpg");
                        if (File.Exists(jpgFile))
                        {
                            return;
                        }

                        try
                        {
                            YamlRemoteStreamDefinition definition = Deserializer
                                .Deserialize<YamlRemoteStreamDefinition>(await File.ReadAllTextAsync(ymlFile, token));

                            if (string.IsNullOrWhiteSpace(definition?.Thumbnail))
                            {
                                return;
                            }

                            byte[] bytes = await client.GetByteArrayAsync(definition.Thumbnail, token);
                            await File.WriteAllBytesAsync(jpgFile, bytes, token);
                            Interlocked.Increment(ref downloaded);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogDebug(ex, "Failed to download thumbnail for {File}", ymlFile);
                        }
                    });

                if (downloaded > 0)
                {
                    _logger.LogInformation(
                        "Downloaded {Count} thumbnails for YouTube import {Name}; scanning library",
                        downloaded,
                        manifest.Name);

                    await _scannerChannel.WriteAsync(new ForceScanLocalLibrary(manifest.LibraryId), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to fetch thumbnails for YouTube import {Slug}", slug);
        }
        finally
        {
            _maintenanceSemaphore.Release();
        }
    }

    private async Task SyncImport(string slug, CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            IYouTubeImportService importService =
                scope.ServiceProvider.GetRequiredService<IYouTubeImportService>();

            Either<BaseError, YouTubeImportSyncResult> maybeResult =
                await importService.SyncImport(slug, cancellationToken);

            foreach (BaseError error in maybeResult.LeftToSeq())
            {
                _logger.LogWarning("Failed to sync YouTube import {Slug}: {Error}", slug, error.Value);
            }

            foreach (YouTubeImportSyncResult result in maybeResult.RightToSeq())
            {
                if (result.Added > 0 || result.Removed > 0)
                {
                    Option<YouTubeImportManifest> maybeManifest =
                        await importService.GetImport(slug, cancellationToken);

                    foreach (YouTubeImportManifest manifest in maybeManifest)
                    {
                        await _scannerChannel.WriteAsync(
                            new ForceScanLocalLibrary(manifest.LibraryId),
                            cancellationToken);
                    }

                    await _channelWriter.WriteAsync(new FetchYouTubeThumbnails(slug), cancellationToken);
                }
            }
        }
        catch (Exception ex) when (ex is not TaskCanceledException and not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to sync YouTube import {Slug}", slug);
        }
    }
}
