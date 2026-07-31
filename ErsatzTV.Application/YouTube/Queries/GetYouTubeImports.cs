using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public record GetYouTubeImports : IRequest<List<YouTubeImportManifest>>;
