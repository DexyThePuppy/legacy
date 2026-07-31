using ErsatzTV.FFmpeg.State;
using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Graphics;

public class HtmlGraphicsElement : BaseGraphicsElement
{
    public string Html { get; set; }

    [YamlMember(Alias = "opacity_percent", ApplyNamingConventions = false)]
    public int? OpacityPercent { get; set; }

    [YamlMember(Alias = "opacity_expression", ApplyNamingConventions = false)]
    public string OpacityExpression { get; set; }

    public WatermarkLocation Location { get; set; }

    [YamlMember(Alias = "horizontal_margin_percent", ApplyNamingConventions = false)]
    public double? HorizontalMarginPercent { get; set; }

    [YamlMember(Alias = "vertical_margin_percent", ApplyNamingConventions = false)]
    public double? VerticalMarginPercent { get; set; }

    [YamlMember(Alias = "width_percent", ApplyNamingConventions = false)]
    public double? WidthPercent { get; set; }

    [YamlMember(Alias = "height_percent", ApplyNamingConventions = false)]
    public double? HeightPercent { get; set; }

    [YamlMember(Alias = "z_index", ApplyNamingConventions = false)]
    public int? ZIndex { get; set; }

    [YamlMember(Alias = "epg_entries", ApplyNamingConventions = false)]
    public int EpgEntries { get; set; }

    [YamlMember(Alias = "capture_fps", ApplyNamingConventions = false)]
    public double? CaptureFps { get; set; }

    [YamlMember(Alias = "start_seconds", ApplyNamingConventions = false)]
    public double? StartSeconds { get; set; }

    [YamlMember(Alias = "duration_seconds", ApplyNamingConventions = false)]
    public double? DurationSeconds { get; set; }

    // trigger: show this element N seconds before the end of the current item
    // (overrides start_seconds when set)
    [YamlMember(Alias = "start_seconds_from_end", ApplyNamingConventions = false)]
    public double? StartSecondsFromEnd { get; set; }
}
