using ErsatzTV.Core;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Application.YouTube;

public class GetYtDlpStatusHandler(IYtDlpService ytDlpService)
    : IRequestHandler<GetYtDlpStatus, YtDlpStatusViewModel>
{
    public async Task<YtDlpStatusViewModel> Handle(GetYtDlpStatus request, CancellationToken cancellationToken)
    {
        Option<string> ytDlpPath = await ytDlpService.LocateYtDlp(cancellationToken);
        Option<string> denoPath = await ytDlpService.LocateDeno(cancellationToken);
        YtDlpSettings settings = await ytDlpService.GetSettings(cancellationToken);

        double cacheUsedGb = 0;
        if (Directory.Exists(FileSystemLayout.YouTubeCacheFolder))
        {
            long bytes = new DirectoryInfo(FileSystemLayout.YouTubeCacheFolder)
                .EnumerateFiles()
                .Sum(f => f.Length);

            cacheUsedGb = Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 2);
        }

        return new YtDlpStatusViewModel(
            ytDlpPath.IfNone(string.Empty),
            denoPath.IfNone(string.Empty),
            settings.Format,
            settings.ExtraArgs,
            settings.CacheMaxGb,
            cacheUsedGb);
    }
}
