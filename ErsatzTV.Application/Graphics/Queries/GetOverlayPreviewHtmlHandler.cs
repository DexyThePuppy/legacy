using System.Globalization;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Graphics;

public class GetOverlayPreviewHtmlHandler(IMediator mediator)
    : IRequestHandler<GetOverlayPreviewHtml, Either<BaseError, string>>
{
    private const int FrameWidth = 1920;
    private const int FrameHeight = 1080;
    private const double ItemDurationSeconds = 120;

    public async Task<Either<BaseError, string>> Handle(
        GetOverlayPreviewHtml request,
        CancellationToken cancellationToken)
    {
        Option<OverlayEditViewModel> maybeOverlay = await mediator.Send(new GetOverlayById(request.Id), cancellationToken);
        foreach (OverlayEditViewModel overlay in maybeOverlay)
        {
            if (overlay.Kind is not GraphicsElementKind.Html || !overlay.IsParsed)
            {
                return BaseError.New("Only HTML overlays can be previewed as raw HTML");
            }

            Dictionary<string, object> variables = BuildSampleVariables(
                request.RequestBase,
                request.Title ?? "Sample Episode Title",
                request.NextTitle ?? "Next Video Title");

            return await mediator.Send(
                new RenderOverlayPreview(overlay.Html ?? string.Empty, variables),
                cancellationToken);
        }

        return BaseError.New($"Overlay {request.Id} not found");
    }

    public static int SampleEpgCount => 8;

    public static IReadOnlyList<OverlayPreviewEpgItem> BuildSampleEpg(string title, string nextTitle)
    {
        string[] sampleTitles =
        [
            title ?? "Now Playing",
            nextTitle ?? "Next Video Title",
            "I Bought an Unused McFlurry Maker on eBay from 2003",
            "If You Solve A Crossword Clue, The Word Appears in Real Life",
            "Building a Tiny Desk Setup From Scratch",
            "Retro Tech: The First Consumer Camcorders",
            "Why This Airport Gate Design Failed",
            "A Tour of Forgotten Shopping Malls"
        ];
        string[] sampleChannels =
        [
            "Current Channel",
            "Up Next Channel",
            "Barry Lewis",
            "Aliensrock",
            "Setup Lab",
            "Retro Tech",
            "Design Failures",
            "Dead Malls"
        ];
        int[] sampleMinutes = [24, 19, 24, 16, 12, 31, 18, 22];

        var items = new List<OverlayPreviewEpgItem>(sampleTitles.Length);
        for (var i = 0; i < sampleTitles.Length; i++)
        {
            items.Add(
                new OverlayPreviewEpgItem(
                    sampleTitles[i],
                    sampleChannels[i],
                    $"{sampleMinutes[i]}:00",
                    string.Empty));
        }

        return items;
    }

    public static Dictionary<string, object> BuildSampleVariables(
        string requestBase,
        string title,
        string nextTitle,
        int epgIndex = 0,
        string epgSlideDirection = "next")
    {
        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<OverlayPreviewEpgItem> sampleItems = BuildSampleEpg(title, nextTitle);
        var sampleEpg = new List<Dictionary<string, object>>();

        for (var i = 0; i < sampleItems.Count; i++)
        {
            OverlayPreviewEpgItem item = sampleItems[i];
            DateTimeOffset start = now.AddMinutes(i * 25);
            int minutes = int.TryParse(item.DurationDisplay.Split(':')[0], out int parsed) ? parsed : 20;
            sampleEpg.Add(
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["Title"] = item.Title,
                    ["SubTitle"] = item.SubTitle,
                    ["Description"] = string.Empty,
                    ["Rating"] = i % 3 == 0 ? "TV-14" : string.Empty,
                    ["Icon"] = item.Icon ?? string.Empty,
                    ["Start"] = start,
                    ["Stop"] = start.AddMinutes(minutes),
                    ["DurationSeconds"] = minutes * 60d,
                    ["DurationDisplay"] = item.DurationDisplay,
                    ["TimeDisplay"] = start.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                });
        }

        int offset = sampleEpg.Count == 0
            ? 0
            : ((epgIndex % sampleEpg.Count) + sampleEpg.Count) % sampleEpg.Count;
        if (offset > 0)
        {
            sampleEpg = sampleEpg.Skip(offset).Concat(sampleEpg.Take(offset)).ToList();
        }

        string nowTitle = sampleEpg.Count > 0 && sampleEpg[0].TryGetValue("Title", out object t0)
            ? t0?.ToString() ?? title ?? string.Empty
            : title ?? string.Empty;
        string upcomingTitle = sampleEpg.Count > 1 && sampleEpg[1].TryGetValue("Title", out object t1)
            ? t1?.ToString() ?? nextTitle ?? string.Empty
            : nextTitle ?? string.Empty;
        string upcomingSubTitle = sampleEpg.Count > 1 && sampleEpg[1].TryGetValue("SubTitle", out object st1)
            ? st1?.ToString() ?? "Coming up next"
            : "Coming up next";

        string baseUrl = string.IsNullOrWhiteSpace(requestBase)
            ? $"http://127.0.0.1:{SystemEnvironment.UiPort}"
            : requestBase.TrimEnd('/');

        string slideDirection = string.Equals(epgSlideDirection, "prev", StringComparison.OrdinalIgnoreCase)
            ? "prev"
            : "next";

        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["MediaItem_Title"] = nowTitle,
            ["MediaItem_Plot"] = "Sample plot for overlay preview.",
            ["MediaItem_ContentRating"] = "TV-14",
            ["MediaItem_ShowTitle"] = "Sample Show",
            ["MediaItem_ShowYear"] = "2024",
            ["MediaItem_Artist"] = "Sample Artist",
            ["MediaItem_Album"] = "Sample Album",
            ["MediaItem_Genres"] = "Tech, Comedy",
            ["MediaItem_Studios"] = "Sample Studio",
            ["Channel_Number"] = "1",
            ["Next_Title"] = upcomingTitle,
            ["Next_SubTitle"] = upcomingSubTitle,
            ["Next_Description"] = "Next item description.",
            ["MediaItem_DurationSeconds"] = ItemDurationSeconds,
            ["MediaItem_StreamSeekSeconds"] = 0d,
            ["MediaItem_RemainingSeconds"] = ItemDurationSeconds,
            ["MediaItem_Duration"] = TimeSpan.FromSeconds(ItemDurationSeconds),
            ["MediaItem_StreamSeek"] = TimeSpan.Zero,
            ["Next_StartsInSeconds"] = 30d,
            ["MediaItem_Start"] = now,
            ["MediaItem_Stop"] = now.AddSeconds(ItemDurationSeconds),
            ["Next_Start"] = now.AddSeconds(30),
            ["Next_Stop"] = now.AddSeconds(630),
            ["Channel_StartTime"] = now.Date,
            ["Resolution"] = new { Width = FrameWidth, Height = FrameHeight },
            ["ScaledResolution"] = new { Width = FrameWidth, Height = FrameHeight },
            ["FrameRate"] = 30.0,
            ["RFrameRate"] = "30/1",
            ["RequestBase"] = baseUrl,
            ["Epg"] = sampleEpg,
            ["EpgSlideDirection"] = slideDirection
        };
    }
}

public sealed record OverlayPreviewEpgItem(
    string Title,
    string SubTitle,
    string DurationDisplay,
    string Icon);
