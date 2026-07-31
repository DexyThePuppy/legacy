namespace ErsatzTV.Core.Interfaces.Streaming;

public interface IHtmlBrowserService
{
    Task<IHtmlBrowserPage> CreatePageAsync(
        int width,
        int height,
        string html,
        CancellationToken cancellationToken);
}

public interface IHtmlBrowserPage : IAsyncDisposable
{
    Task<byte[]> CapturePngAsync(CancellationToken cancellationToken);
    Task EndBurstModeAsync();
}
