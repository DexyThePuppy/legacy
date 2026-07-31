namespace ErsatzTV.Application.YouTube;

public record YtDlpStatusViewModel(
    string YtDlpPath,
    string DenoPath,
    string Format,
    string ExtraArgs,
    int CacheMaxGb,
    double CacheUsedGb);

public record GetYtDlpStatus : IRequest<YtDlpStatusViewModel>;
