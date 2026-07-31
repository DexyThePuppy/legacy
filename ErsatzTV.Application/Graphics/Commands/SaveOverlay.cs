using ErsatzTV.Core;
using ErsatzTV.FFmpeg.State;

namespace ErsatzTV.Application.Graphics;

public record SaveOverlay(
    int? Id,
    string Name,
    string Html,
    WatermarkLocation Location,
    double HorizontalMarginPercent,
    double VerticalMarginPercent,
    double WidthPercent,
    double HeightPercent,
    int OpacityPercent,
    string OpacityExpression,
    int ZIndex,
    double? CaptureFps,
    int EpgEntries,
    double? StartSeconds,
    double? DurationSeconds,
    double? StartSecondsFromEnd,
    string RawContent) : IRequest<Either<BaseError, int>>;
