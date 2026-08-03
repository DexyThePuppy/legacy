using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.FFmpeg.State;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Streaming;

public class GetChannelRawPreviewOverlaysHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IDynamicPlayoutItemService dynamicPlayoutItemService,
    ITemplateDataRepository templateDataRepository,
    IMediator mediator)
    : IRequestHandler<GetChannelRawPreviewOverlays, Either<BaseError, string>>
{
    private static readonly Regex StyleBlockRegex = new(
        @"<style\b[^>]*>(.*?)</style>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex BodyBlockRegex = new(
        @"<body\b[^>]*>(.*?)</body>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    public async Task<Either<BaseError, string>> Handle(
        GetChannelRawPreviewOverlays request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Channel> maybeChannel = await dbContext.Channels
            .AsNoTracking()
            .Include(c => c.FFmpegProfile)
            .ThenInclude(p => p.Resolution)
            .Include(c => c.ChannelGraphicsElements)
            .ThenInclude(cge => cge.GraphicsElement)
            .SelectOneAsync(c => c.Number, c => c.Number == request.ChannelNumber, cancellationToken);

        foreach (Channel channel in maybeChannel)
        {
            List<GraphicsElement> htmlElements = (channel.ChannelGraphicsElements ?? [])
                .Select(cge => cge.GraphicsElement)
                .Where(ge => ge is { Kind: GraphicsElementKind.Html })
                .OrderBy(ge => ge.Id)
                .ToList();

            if (htmlElements.Count == 0)
            {
                return EmptyDocument();
            }

            DateTimeOffset now = ChannelPlaybackClock.GetEffectiveNow(channel);
            int channelId = channel.MirrorSourceChannelId ?? channel.Id;

            Option<PlayoutItem> maybeItem = await dbContext.PlayoutItems
                .AsNoTracking()
                .Include(i => i.MediaItem)
                .ThenInclude(mi => mi.LibraryPath)
                .ThenInclude(lp => lp.Library)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Episode).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Movie).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as MusicVideo).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as OtherVideo).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Song).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Image).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as RemoteStream).MediaVersions)
                .ThenInclude(mv => mv.MediaFiles)
                .ForChannelAndTime(channelId, now);

            if (maybeItem.IsNone)
            {
                return EmptyDocument();
            }

            PlayoutItem item = maybeItem.Head();
            Either<BaseError, PlayoutItemWithPath> maybePath =
                await dynamicPlayoutItemService.ValidatePlayoutItemPath(dbContext, item, cancellationToken);

            foreach (BaseError error in maybePath.LeftToSeq())
            {
                return error;
            }

            PlayoutItemWithPath withPath = maybePath.RightToSeq().Head();
            bool isLive = withPath.PlayoutItem.MediaItem is RemoteStream { IsLive: true };
            TimeSpan seek = TimeSpan.Zero;
            if (!isLive)
            {
                seek = now - withPath.PlayoutItem.StartOffset + withPath.PlayoutItem.InPoint;
                if (seek < TimeSpan.Zero)
                {
                    seek = TimeSpan.Zero;
                }
            }

            TimeSpan contentDuration = withPath.PlayoutItem.MediaItem.GetNonZeroDuration()
                .IfNone(withPath.PlayoutItem.OutPoint - withPath.PlayoutItem.InPoint);
            if (contentDuration <= TimeSpan.Zero)
            {
                contentDuration = withPath.PlayoutItem.FinishOffset - withPath.PlayoutItem.StartOffset;
            }

            if (contentDuration <= TimeSpan.Zero)
            {
                contentDuration = TimeSpan.FromMinutes(1);
            }

            int width = channel.FFmpegProfile?.Resolution?.Width is > 0 and var w ? w : 1920;
            int height = channel.FFmpegProfile?.Resolution?.Height is > 0 and var h ? h : 1080;

            Dictionary<string, object> variables = await BuildVariables(
                channel,
                withPath.PlayoutItem.MediaItem,
                seek,
                contentDuration,
                withPath.PlayoutItem.StartOffset,
                width,
                height,
                request.RequestBase,
                cancellationToken);

            var layers = new List<(OverlayEditViewModel Overlay, string BodyHtml, string Styles)>();

            foreach (GraphicsElement element in htmlElements)
            {
                Option<OverlayEditViewModel> maybeOverlay =
                    await mediator.Send(new GetOverlayById(element.Id), cancellationToken);

                foreach (OverlayEditViewModel overlay in maybeOverlay)
                {
                    if (!overlay.IsParsed || string.IsNullOrWhiteSpace(overlay.Html))
                    {
                        continue;
                    }

                    Either<BaseError, string> rendered = await mediator.Send(
                        new RenderOverlayPreview(overlay.Html, variables),
                        cancellationToken);

                    foreach (string html in rendered.RightToSeq())
                    {
                        layers.Add((overlay, ExtractBody(html), ExtractStyles(html)));
                    }
                }
            }

            if (layers.Count == 0)
            {
                return EmptyDocument();
            }

            return BuildDocument(layers);
        }

        return BaseError.New($"Unable to locate channel {request.ChannelNumber}");
    }

    private async Task<Dictionary<string, object>> BuildVariables(
        Channel channel,
        MediaItem mediaItem,
        TimeSpan seek,
        TimeSpan contentDuration,
        DateTimeOffset contentStart,
        int width,
        int height,
        string requestBase,
        CancellationToken cancellationToken)
    {
        string baseUrl = string.IsNullOrWhiteSpace(requestBase)
            ? $"http://127.0.0.1:{Settings.UiPort}"
            : requestBase.TrimEnd('/');

        var result = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [FFmpegProfileTemplateDataKey.Resolution] = new { Width = width, Height = height },
            [FFmpegProfileTemplateDataKey.ScaledResolution] = new { Width = width, Height = height },
            [FFmpegProfileTemplateDataKey.RFrameRate] = "30/1",
            [FFmpegProfileTemplateDataKey.FrameRate] = 30.0,
            [FFmpegProfileTemplateDataKey.RequestBase] = baseUrl,
            [ChannelTemplateDataKey.ChannelStartTime] = contentStart,
            [ChannelTemplateDataKey.Number] = channel.Number,
            [MediaItemTemplateDataKey.StreamSeek] = seek,
            [MediaItemTemplateDataKey.Start] = contentStart,
            [MediaItemTemplateDataKey.Stop] = contentStart + contentDuration,
            [MediaItemTemplateDataKey.DurationSeconds] = contentDuration.TotalSeconds,
            [MediaItemTemplateDataKey.StreamSeekSeconds] = seek.TotalSeconds,
            [MediaItemTemplateDataKey.RemainingSeconds] =
                Math.Max(0, (contentDuration - seek).TotalSeconds),
            [MediaItemTemplateDataKey.Duration] = contentDuration
        };

        Option<Dictionary<string, object>> maybeTemplateData =
            await templateDataRepository.GetMediaItemTemplateData(mediaItem, cancellationToken);
        foreach (Dictionary<string, object> templateData in maybeTemplateData)
        {
            foreach (KeyValuePair<string, object> variable in templateData)
            {
                result[variable.Key] = variable.Value;
            }
        }

        DateTimeOffset epgTime = contentStart + seek;
        Option<Dictionary<string, object>> maybeEpgData =
            await templateDataRepository.GetEpgTemplateData(channel.Number, epgTime, 2);
        foreach (Dictionary<string, object> templateData in maybeEpgData)
        {
            foreach (KeyValuePair<string, object> variable in templateData)
            {
                result[variable.Key] = variable.Value;
            }
        }

        AddNextEpgEntryVariables(result, epgTime);
        return result;
    }

    private static void AddNextEpgEntryVariables(Dictionary<string, object> result, DateTimeOffset startTime)
    {
        if (!result.TryGetValue(EpgTemplateDataKey.Epg, out object epgValue) ||
            epgValue is not System.Collections.IEnumerable enumerable)
        {
            return;
        }

        var entries = enumerable.Cast<object>().ToList();
        if (entries.Count < 2)
        {
            return;
        }

        if (entries[1] is Dictionary<string, object> dict)
        {
            result[EpgTemplateDataKey.NextTitle] = dict.GetValueOrDefault("Title");
            result[EpgTemplateDataKey.NextSubTitle] = dict.GetValueOrDefault("SubTitle");
            result[EpgTemplateDataKey.NextDescription] = dict.GetValueOrDefault("Description");
            result[EpgTemplateDataKey.NextStart] = dict.GetValueOrDefault("Start");
            result[EpgTemplateDataKey.NextStop] = dict.GetValueOrDefault("Stop");
            if (dict.GetValueOrDefault("Start") is DateTimeOffset nextStart)
            {
                result[EpgTemplateDataKey.NextStartsInSeconds] =
                    Math.Max(0, (nextStart - startTime).TotalSeconds);
            }
        }
        else if (entries[1] is EpgProgrammeTemplateData typed)
        {
            result[EpgTemplateDataKey.NextTitle] = typed.Title;
            result[EpgTemplateDataKey.NextSubTitle] = typed.SubTitle;
            result[EpgTemplateDataKey.NextDescription] = typed.Description;
            result[EpgTemplateDataKey.NextStart] = typed.Start;
            result[EpgTemplateDataKey.NextStop] = typed.Stop;
            result[EpgTemplateDataKey.NextStartsInSeconds] =
                Math.Max(0, (typed.Start - startTime).TotalSeconds);
        }
    }

    private static string ExtractStyles(string html)
    {
        var sb = new StringBuilder();
        foreach (Match match in StyleBlockRegex.Matches(html ?? string.Empty))
        {
            sb.Append("<style>").Append(match.Groups[1].Value).Append("</style>");
        }

        return sb.ToString();
    }

    private static string ExtractBody(string html)
    {
        Match match = BodyBlockRegex.Match(html ?? string.Empty);
        return match.Success ? match.Groups[1].Value : html ?? string.Empty;
    }

    private static string BuildDocument(List<(OverlayEditViewModel Overlay, string BodyHtml, string Styles)> layers)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html>");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\"/>");
        sb.AppendLine(
            """
            <style>
              html, body {
                margin: 0;
                width: 100%;
                height: 100%;
                background: transparent;
                overflow: hidden;
              }
              .etv-overlay-layer {
                position: absolute;
                box-sizing: border-box;
                overflow: hidden;
                pointer-events: none;
              }
              .etv-overlay-layer > * {
                pointer-events: none;
              }
            </style>
            """);
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        foreach ((OverlayEditViewModel overlay, string bodyHtml, string styles) in layers.OrderBy(l => l.Overlay.ZIndex))
        {
            string style = FrameStyle(overlay);
            sb.Append(CultureInfo.InvariantCulture, $"<div class=\"etv-overlay-layer\" style=\"{style}\">");
            sb.Append(styles);
            sb.Append(bodyHtml);
            sb.Append("</div>");
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static string FrameStyle(OverlayEditViewModel model)
    {
        double width = model.WidthPercent <= 0 ? 100 : model.WidthPercent;
        double height = model.HeightPercent <= 0 ? 100 : model.HeightPercent;
        double hMargin = Math.Max(0, model.HorizontalMarginPercent);
        double vMargin = Math.Max(0, model.VerticalMarginPercent);

        (double left, double top) = model.Location switch
        {
            WatermarkLocation.BottomLeft => (hMargin, 100 - height - vMargin),
            WatermarkLocation.TopLeft => (hMargin, vMargin),
            WatermarkLocation.TopRight => (100 - width - hMargin, vMargin),
            WatermarkLocation.TopMiddle => ((100 - width) / 2, vMargin),
            WatermarkLocation.RightMiddle => (100 - width - hMargin, (100 - height) / 2),
            WatermarkLocation.BottomMiddle => ((100 - width) / 2, 100 - height - vMargin),
            WatermarkLocation.LeftMiddle => (hMargin, (100 - height) / 2),
            WatermarkLocation.MiddleCenter => ((100 - width) / 2 + hMargin, (100 - height) / 2 + vMargin),
            _ => (100 - width - hMargin, 100 - height - vMargin)
        };

        double opacity = Math.Clamp(model.OpacityPercent, 0, 100) / 100.0;
        return
            $"left:{left.ToString(CultureInfo.InvariantCulture)}%;" +
            $"top:{top.ToString(CultureInfo.InvariantCulture)}%;" +
            $"width:{width.ToString(CultureInfo.InvariantCulture)}%;" +
            $"height:{height.ToString(CultureInfo.InvariantCulture)}%;" +
            $"opacity:{opacity.ToString(CultureInfo.InvariantCulture)};" +
            $"z-index:{model.ZIndex.ToString(CultureInfo.InvariantCulture)};";
    }

    private static string EmptyDocument() =>
        """
        <!DOCTYPE html>
        <html><head><meta charset="utf-8"/></head>
        <body style="margin:0;background:transparent"></body></html>
        """;
}
