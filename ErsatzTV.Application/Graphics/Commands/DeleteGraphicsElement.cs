using ErsatzTV.Core;

namespace ErsatzTV.Application.Graphics;

public record DeleteGraphicsElement(int GraphicsElementId) : IRequest<Either<BaseError, Unit>>;
