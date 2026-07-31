using ErsatzTV.Core;

namespace ErsatzTV.Application.YouTube;

public record UpdateYouTubeImport(
    string Slug,
    string Name,
    string IconUrl,
    bool AutoSync,
    int SyncIntervalHours) : IRequest<Either<BaseError, Unit>>;
