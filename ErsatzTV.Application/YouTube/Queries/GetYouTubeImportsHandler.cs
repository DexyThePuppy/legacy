using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class GetYouTubeImportsHandler(IYouTubeImportService importService)
    : IRequestHandler<GetYouTubeImports, List<YouTubeImportManifest>>
{
    public Task<List<YouTubeImportManifest>> Handle(GetYouTubeImports request, CancellationToken cancellationToken) =>
        importService.ListImports(cancellationToken);
}
