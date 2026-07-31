using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class QueryYouTubeHandler(IYtDlpService ytDlpService)
    : IRequestHandler<QueryYouTube, Either<BaseError, YtDlpQueryResult>>
{
    public Task<Either<BaseError, YtDlpQueryResult>> Handle(QueryYouTube request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Input))
        {
            return Task.FromResult<Either<BaseError, YtDlpQueryResult>>(
                BaseError.New("Enter a search query or a video, playlist or channel url"));
        }

        return ytDlpService.Query(request.Input.Trim(), cancellationToken);
    }
}
