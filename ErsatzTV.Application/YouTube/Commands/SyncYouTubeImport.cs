using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;

namespace ErsatzTV.Application.YouTube;

public record SyncYouTubeImport(string Slug) : IRequest<Either<BaseError, YouTubeImportSyncResult>>;
