using ErsatzTV.Core;

namespace ErsatzTV.Application.Graphics;

public record RenderOverlayPreview(
    string HtmlTemplate,
    IReadOnlyDictionary<string, object> Variables) : IRequest<Either<BaseError, string>>;
