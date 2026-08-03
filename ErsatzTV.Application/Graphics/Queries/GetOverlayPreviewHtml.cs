using ErsatzTV.Core;

namespace ErsatzTV.Application.Graphics;

public record GetOverlayPreviewHtml(
    int Id,
    string RequestBase,
    string Title = null,
    string NextTitle = null) : IRequest<Either<BaseError, string>>;
