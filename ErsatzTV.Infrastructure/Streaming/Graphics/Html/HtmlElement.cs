using System.Diagnostics;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Core.Interfaces.Streaming;
using Microsoft.Extensions.Logging;
using NCalc;
using SkiaSharp;

namespace ErsatzTV.Infrastructure.Streaming.Graphics.Html;

public class HtmlElement(
    IHtmlBrowserService htmlBrowserService,
    HtmlGraphicsElement htmlElement,
    ILogger logger)
    : GraphicsElement, IDisposable
{
    private readonly object _frameLock = new();
    private CancellationTokenSource _captureCts;
    private Task _captureTask;
    private IHtmlBrowserPage _page;
    private SKBitmap _displayBitmap;
    private SKBitmap _pendingBitmap;
    private SKPointI _location;
    private Option<Expression> _maybeOpacityExpression;
    private float _opacity;
    private TimeSpan _startTime;
    private TimeSpan _endTime;
    private bool _disposed;

    public override int ZIndex { get; } = htmlElement.ZIndex ?? 0;

    public override string DebugKey { get; } = $"Html {htmlElement.DebugName()}";

    public override async Task InitializeAsync(GraphicsEngineContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(htmlElement.OpacityExpression))
            {
                var expression = new Expression(htmlElement.OpacityExpression);
                expression.EvaluateFunction += OpacityExpressionHelper.EvaluateFunction;
                _maybeOpacityExpression = expression;
            }
            else
            {
                _opacity = (htmlElement.OpacityPercent ?? 100) / 100.0f;
            }

            _startTime = TimeSpan.FromSeconds(htmlElement.StartSeconds ?? 0);

            // trigger support: start N seconds before the end of the current item
            foreach (double startSecondsFromEnd in Optional(htmlElement.StartSecondsFromEnd))
            {
                TimeSpan fromEnd = context.ContentTotalDuration - TimeSpan.FromSeconds(startSecondsFromEnd);
                _startTime = fromEnd < TimeSpan.Zero ? TimeSpan.Zero : fromEnd;
            }

            _endTime = htmlElement.DurationSeconds is > 0
                ? _startTime + TimeSpan.FromSeconds(htmlElement.DurationSeconds.Value)
                : TimeSpan.MaxValue;

            if (_endTime < context.Seek)
            {
                IsFinished = true;
                return;
            }

            int viewportWidth = Math.Max(
                2,
                (int)Math.Round((htmlElement.WidthPercent ?? 100) / 100.0 * context.FrameSize.Width));
            int viewportHeight = Math.Max(
                2,
                (int)Math.Round((htmlElement.HeightPercent ?? 100) / 100.0 * context.FrameSize.Height));

            // keep even dimensions for video friendliness
            if (viewportWidth % 2 != 0)
            {
                viewportWidth++;
            }

            if (viewportHeight % 2 != 0)
            {
                viewportHeight++;
            }

            (int horizontalMargin, int verticalMargin) = NormalMargins(
                context.FrameSize,
                htmlElement.HorizontalMarginPercent ?? 0,
                htmlElement.VerticalMarginPercent ?? 0);

            _location = CalculatePosition(
                htmlElement.Location,
                context.FrameSize.Width,
                context.FrameSize.Height,
                viewportWidth,
                viewportHeight,
                horizontalMargin,
                verticalMargin);

            double streamFps = context.FrameRate.ParsedFrameRate;
            double captureFps = htmlElement.CaptureFps ?? streamFps;
            if (captureFps <= 0)
            {
                captureFps = streamFps;
            }

            captureFps = Math.Min(captureFps, streamFps);
            var frameInterval = TimeSpan.FromSeconds(1.0 / captureFps);

            _page = await htmlBrowserService.CreatePageAsync(
                viewportWidth,
                viewportHeight,
                htmlElement.Html,
                cancellationToken);

            _captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _captureTask = CaptureLoopAsync(frameInterval, _captureCts.Token);
        }
        catch (Exception ex)
        {
            IsFinished = true;
            logger.LogWarning(ex, "Failed to initialize HTML element; will disable for this content");
            await DisposePageAsync();
        }
    }

    public override ValueTask<Option<PreparedElementImage>> PrepareImage(
        TimeSpan timeOfDay,
        TimeSpan contentTime,
        TimeSpan contentTotalTime,
        TimeSpan channelTime,
        CancellationToken cancellationToken)
    {
        if (contentTime < _startTime)
        {
            return ValueTask.FromResult(Option<PreparedElementImage>.None);
        }

        if (contentTime > _endTime)
        {
            IsFinished = true;
            return ValueTask.FromResult(Option<PreparedElementImage>.None);
        }

        float opacity = _opacity;
        foreach (Expression expression in _maybeOpacityExpression)
        {
            opacity = OpacityExpressionHelper.GetOpacity(
                expression,
                timeOfDay,
                contentTime,
                contentTotalTime,
                channelTime);
        }

        if (opacity == 0)
        {
            return ValueTask.FromResult(Option<PreparedElementImage>.None);
        }

        lock (_frameLock)
        {
            if (_pendingBitmap is not null)
            {
                _displayBitmap?.Dispose();
                _displayBitmap = _pendingBitmap;
                _pendingBitmap = null;
            }

            if (_displayBitmap is null)
            {
                return ValueTask.FromResult(Option<PreparedElementImage>.None);
            }

            return ValueTask.FromResult(
                Optional(new PreparedElementImage(_displayBitmap, _location, opacity, ZIndex, false)));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        GC.SuppressFinalize(this);

        try
        {
            _captureCts?.Cancel();
#pragma warning disable VSTHRD002
            _captureTask?.Wait(TimeSpan.FromSeconds(2));
#pragma warning restore VSTHRD002
        }
        catch
        {
            // ignored
        }

        _captureCts?.Dispose();

#pragma warning disable VSTHRD002
        DisposePageAsync().AsTask().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002

        lock (_frameLock)
        {
            _pendingBitmap?.Dispose();
            _pendingBitmap = null;
            _displayBitmap?.Dispose();
            _displayBitmap = null;
        }
    }

    private async Task CaptureLoopAsync(TimeSpan frameInterval, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var sw = Stopwatch.StartNew();

                byte[] png = await _page.CapturePngAsync(cancellationToken);
                SKBitmap decoded = SKBitmap.Decode(png);
                if (decoded is null)
                {
                    logger.LogWarning("Failed to decode HTML overlay screenshot; skipping frame");
                }
                else
                {
                    // normalize to BGRA unpremul for the graphics engine
                    SKBitmap frame = decoded;
                    if (decoded.ColorType != SKColorType.Bgra8888 || decoded.AlphaType != SKAlphaType.Unpremul)
                    {
                        frame = decoded.Copy(SKColorType.Bgra8888) ?? decoded;
                        if (!ReferenceEquals(frame, decoded))
                        {
                            decoded.Dispose();
                        }
                    }

                    lock (_frameLock)
                    {
                        _pendingBitmap?.Dispose();
                        _pendingBitmap = frame;
                    }
                }

                TimeSpan delay = frameInterval - sw.Elapsed;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected on dispose / stream end
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "HTML overlay capture loop failed; disabling element");
            IsFinished = true;
        }
        finally
        {
            try
            {
                if (_page is not null)
                {
                    await _page.EndBurstModeAsync();
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private async ValueTask DisposePageAsync()
    {
        if (_page is null)
        {
            return;
        }

        IHtmlBrowserPage page = _page;
        _page = null;
        await page.DisposeAsync();
    }
}
