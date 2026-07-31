using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class UpdateYtDlpSettingsHandler(IConfigElementRepository configElementRepository)
    : IRequestHandler<UpdateYtDlpSettings, Either<BaseError, Unit>>
{
    public async Task<Either<BaseError, Unit>> Handle(
        UpdateYtDlpSettings request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(request.YtDlpPath) && !File.Exists(request.YtDlpPath))
        {
            return BaseError.New($"yt-dlp path does not exist: {request.YtDlpPath}");
        }

        if (!string.IsNullOrWhiteSpace(request.DenoPath) && !File.Exists(request.DenoPath))
        {
            return BaseError.New($"deno path does not exist: {request.DenoPath}");
        }

        if (!string.IsNullOrWhiteSpace(request.YtDlpPath))
        {
            await configElementRepository.Upsert(ConfigElementKey.YtDlpPath, request.YtDlpPath, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(request.DenoPath))
        {
            await configElementRepository.Upsert(ConfigElementKey.YtDlpDenoPath, request.DenoPath, cancellationToken);
        }

        string format = string.IsNullOrWhiteSpace(request.Format)
            ? YtDlpSettings.DefaultFormat
            : request.Format.Trim();

        await configElementRepository.Upsert(ConfigElementKey.YtDlpFormat, format, cancellationToken);
        await configElementRepository.Upsert(
            ConfigElementKey.YtDlpExtraArgs,
            request.ExtraArgs ?? string.Empty,
            cancellationToken);

        await configElementRepository.Upsert(
            ConfigElementKey.YtDlpCacheMaxGb,
            Math.Max(1, request.CacheMaxGb),
            cancellationToken);

        return Unit.Default;
    }
}
