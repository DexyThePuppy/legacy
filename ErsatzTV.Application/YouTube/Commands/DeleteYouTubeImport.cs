using ErsatzTV.Core;

namespace ErsatzTV.Application.YouTube;

public record DeleteYouTubeImport(string Slug) : IRequest<Either<BaseError, Unit>>;
