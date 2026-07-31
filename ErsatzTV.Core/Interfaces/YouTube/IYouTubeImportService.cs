using ErsatzTV.Core.YouTube;

namespace ErsatzTV.Core.Interfaces.YouTube;

public record YouTubeImportSyncResult(int Added, int Removed);

public interface IYouTubeImportService
{
    Task<List<YouTubeImportManifest>> ListImports(CancellationToken cancellationToken);

    Task<Option<YouTubeImportManifest>> GetImport(string slug, CancellationToken cancellationToken);

    Task<Either<BaseError, YouTubeImportManifest>> CreateImport(
        YtDlpQueryResult queryResult,
        string name,
        string iconUrl,
        bool autoSync,
        int syncIntervalHours,
        int libraryId,
        CancellationToken cancellationToken);

    Task<Either<BaseError, YouTubeImportSyncResult>> SyncImport(string slug, CancellationToken cancellationToken);

    Task<Either<BaseError, Unit>> UpdateImport(
        string slug,
        string name,
        string iconUrl,
        bool autoSync,
        int syncIntervalHours,
        CancellationToken cancellationToken);

    Task<Either<BaseError, Unit>> DeleteImport(string slug, CancellationToken cancellationToken);

    string GetImportFolder(string slug);
}
