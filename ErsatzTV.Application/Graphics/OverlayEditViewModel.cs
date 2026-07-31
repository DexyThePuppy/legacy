using ErsatzTV.Core.Domain;
using ErsatzTV.FFmpeg.State;

namespace ErsatzTV.Application.Graphics;

public class OverlayEditViewModel
{
    public int Id { get; set; }
    public string FileName { get; set; }
    public GraphicsElementKind Kind { get; set; }

    // false => only raw yaml editing is supported (parse failed or non-html kind)
    public bool IsParsed { get; set; }
    public string RawContent { get; set; }

    public string Name { get; set; }
    public string Html { get; set; }
    public WatermarkLocation Location { get; set; }
    public double HorizontalMarginPercent { get; set; }
    public double VerticalMarginPercent { get; set; }
    public double WidthPercent { get; set; } = 100;
    public double HeightPercent { get; set; } = 100;
    public int OpacityPercent { get; set; } = 100;
    public string OpacityExpression { get; set; }
    public int ZIndex { get; set; }
    public double? CaptureFps { get; set; }
    public int EpgEntries { get; set; }
    public double? StartSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public double? StartSecondsFromEnd { get; set; }
}
