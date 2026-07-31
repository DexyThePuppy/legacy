using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using PuppeteerSharp.BrowserData;

namespace ErsatzTV.Infrastructure.Streaming.Graphics.Html;

public sealed class HtmlBrowserService(ILogger<HtmlBrowserService> logger) : IHtmlBrowserService, IAsyncDisposable
{
    private readonly SemaphoreSlim _browserLock = new(1, 1);
    private IBrowser _browser;
    private bool _disposed;

    public async Task<IHtmlBrowserPage> CreatePageAsync(
        int width,
        int height,
        string html,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IBrowser browser = await EnsureBrowserAsync(cancellationToken);
        IPage page = await browser.NewPageAsync();

        try
        {
            await page.SetViewportAsync(
                new ViewPortOptions
                {
                    Width = Math.Max(2, width),
                    Height = Math.Max(2, height),
                    DeviceScaleFactor = 1
                });

            await page.SetContentAsync(
                html ?? string.Empty,
                new SetContentOptions
                {
                    WaitUntil = [WaitUntilNavigation.Load],
                    Timeout = 30_000,
                    CancellationToken = cancellationToken
                });

            return new HtmlBrowserPage(page);
        }
        catch
        {
            await page.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _browserLock.WaitAsync();
        try
        {
            if (_browser is not null)
            {
                await _browser.CloseAsync();
                await _browser.DisposeAsync();
                _browser = null;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Error disposing HTML browser");
        }
        finally
        {
            _browserLock.Release();
            _browserLock.Dispose();
        }
    }

    private async Task<IBrowser> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        await _browserLock.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { IsConnected: true })
            {
                return _browser;
            }

            if (_browser is not null)
            {
                try
                {
                    await _browser.DisposeAsync();
                }
                catch
                {
                    // ignored
                }

                _browser = null;
            }

            Directory.CreateDirectory(FileSystemLayout.ChromiumCacheFolder);

            logger.LogInformation(
                "Preparing Chromium for HTML graphics elements at {CacheFolder}",
                FileSystemLayout.ChromiumCacheFolder);

            IBrowserFetcher fetcher = Puppeteer.CreateBrowserFetcher(
                new BrowserFetcherOptions { Path = FileSystemLayout.ChromiumCacheFolder });

            InstalledBrowser installed = await fetcher.DownloadAsync();

            _browser = await Puppeteer.LaunchAsync(
                new LaunchOptions
                {
                    Headless = true,
                    ExecutablePath = installed.GetExecutablePath(),
                    Args =
                    [
                        "--no-sandbox",
                        "--disable-setuid-sandbox",
                        "--disable-dev-shm-usage",
                        "--disable-gpu",
                        "--hide-scrollbars",
                        "--mute-audio"
                    ]
                });

            logger.LogInformation("Chromium ready for HTML graphics elements");
            return _browser;
        }
        finally
        {
            _browserLock.Release();
        }
    }

    private sealed class HtmlBrowserPage(IPage page) : IHtmlBrowserPage
    {
        private readonly ScreenshotOptions _screenshotOptions = new()
        {
            Type = ScreenshotType.Png,
            OmitBackground = true,
            BurstMode = true,
            OptimizeForSpeed = true,
            CaptureBeyondViewport = false
        };

        private bool _disposed;

        public Task<byte[]> CapturePngAsync(CancellationToken cancellationToken) =>
            page.ScreenshotDataAsync(_screenshotOptions);

        public async Task EndBurstModeAsync()
        {
            try
            {
                await page.SetBurstModeOffAsync();
            }
            catch
            {
                // page may already be closed
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await EndBurstModeAsync();
            }
            catch
            {
                // ignored
            }

            try
            {
                await page.CloseAsync();
            }
            catch
            {
                // ignored
            }

            await page.DisposeAsync();
        }
    }
}
