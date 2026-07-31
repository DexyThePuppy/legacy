namespace ErsatzTV.Core.Interfaces.YouTube;

public interface IYtDlpWorkerRequest;

// download a single youtube video (by remote stream media item id) into the cache
public record DownloadYouTubeVideo(int RemoteStreamId) : IYtDlpWorkerRequest;

// download missing thumbnail sidecars for all videos in an import, then rescan
public record FetchYouTubeThumbnails(string Slug) : IYtDlpWorkerRequest;

// re-check an import against youtube and apply added/removed videos
public record SyncYouTubeImportRequest(string Slug) : IYtDlpWorkerRequest;
