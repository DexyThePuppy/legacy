using System.Threading.Channels;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;
using ErsatzTV.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Services;

// periodically:
// - downloads upcoming youtube playout items before they air ("downloaded as they are queued")
// - re-syncs imports that have auto-sync enabled and are due
public class YouTubeSyncService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan LookAheadWindow = TimeSpan.FromMinutes(30);

    private readonly ChannelWriter<IYtDlpWorkerRequest> _workerChannel;
    private readonly ILogger<YouTubeSyncService> _logger;
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public YouTubeSyncService(
        ChannelWriter<IYtDlpWorkerRequest> workerChannel,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<YouTubeSyncService> logger)
    {
        _workerChannel = workerChannel;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // let startup services (including migrations) settle first
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _logger.LogInformation("YouTube sync service started");

        using var timer = new PeriodicTimer(TickInterval);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DownloadUpcomingPlayoutItems(stoppingToken);
                await SyncDueImports(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in YouTube sync service tick");
            }
        }

        _logger.LogInformation("YouTube sync service shutting down");
    }

    private async Task DownloadUpcomingPlayoutItems(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IDbContextFactory<TvContext> dbContextFactory =
            scope.ServiceProvider.GetRequiredService<IDbContextFactory<TvContext>>();

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        DateTime now = DateTime.UtcNow;
        DateTime windowEnd = now.Add(LookAheadWindow);

        List<int> upcomingIds = await dbContext.PlayoutItems
            .AsNoTracking()
            .Where(pi =>
                pi.Finish > now &&
                pi.Start < windowEnd &&
                pi.MediaItem is RemoteStream)
            .OrderBy(pi => pi.Start)
            .Select(pi => pi.MediaItemId)
            .ToListAsync(cancellationToken);

        foreach (int mediaItemId in upcomingIds.Distinct())
        {
            // the worker skips items that are not downloader-managed or already cached
            await _workerChannel.WriteAsync(new DownloadYouTubeVideo(mediaItemId), cancellationToken);
        }
    }

    private async Task SyncDueImports(CancellationToken cancellationToken)
    {
        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IYouTubeImportService importService =
            scope.ServiceProvider.GetRequiredService<IYouTubeImportService>();

        List<YouTubeImportManifest> imports = await importService.ListImports(cancellationToken);
        foreach (YouTubeImportManifest manifest in imports.Where(m => m.AutoSync))
        {
            DateTime lastSync = manifest.LastSyncUtc ?? manifest.CreatedUtc;
            if (DateTime.UtcNow - lastSync >= TimeSpan.FromHours(Math.Max(1, manifest.SyncIntervalHours)))
            {
                _logger.LogInformation("YouTube import {Name} is due for sync", manifest.Name);
                await _workerChannel.WriteAsync(new SyncYouTubeImportRequest(manifest.Slug), cancellationToken);
            }
        }
    }
}
