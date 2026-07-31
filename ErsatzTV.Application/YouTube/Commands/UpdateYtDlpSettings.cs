using ErsatzTV.Core;

namespace ErsatzTV.Application.YouTube;

public record UpdateYtDlpSettings(
    string YtDlpPath,
    string DenoPath,
    string Format,
    string ExtraArgs,
    int CacheMaxGb) : IRequest<Either<BaseError, Unit>>;
