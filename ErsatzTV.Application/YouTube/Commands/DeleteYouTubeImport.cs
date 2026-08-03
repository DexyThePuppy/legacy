using ErsatzTV.Core;

namespace ErsatzTV.Application.YouTube;

public record DeleteYouTubeImport(
    string Slug,
    bool DeleteLinkedStation = false) : IRequest<Either<BaseError, Unit>>;
