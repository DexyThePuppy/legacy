using ErsatzTV.Core;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public record QueryYouTube(string Input) : IRequest<Either<BaseError, YtDlpQueryResult>>;
