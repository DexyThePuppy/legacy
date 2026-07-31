using System.Collections.Immutable;
using System.IO.Abstractions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Metadata;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Scanner.Core.Interfaces;
using ErsatzTV.Scanner.Core.Interfaces.FFmpeg;
using ErsatzTV.Scanner.Core.Interfaces.Metadata;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Scanner.Core.Metadata;

public class RemoteStreamFolderScanner : LocalFolderScanner, IRemoteStreamFolderScanner
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IScannerProxy _scannerProxy;
    private readonly IFileSystem _fileSystem;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ILogger<RemoteStreamFolderScanner> _logger;
    private readonly IMediaItemRepository _mediaItemRepository;
    private readonly IRemoteStreamRepository _remoteStreamRepository;

    public RemoteStreamFolderScanner(
        IScannerProxy scannerProxy,
        IFileSystem fileSystem,
        ILocalFileSystem localFileSystem,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        IRemoteStreamRepository remoteStreamRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IFFmpegPngService ffmpegPngService,
        ITempFilePool tempFilePool,
        ILogger<RemoteStreamFolderScanner> logger) : base(
        fileSystem,
        localStatisticsProvider,
        metadataRepository,
        mediaItemRepository,
        imageCache,
        ffmpegPngService,
        tempFilePool,
        logger)
    {
        _scannerProxy = scannerProxy;
        _fileSystem = fileSystem;
        _localFileSystem = localFileSystem;
        _localMetadataProvider = localMetadataProvider;
        _metadataRepository = metadataRepository;
        _remoteStreamRepository = remoteStreamRepository;
        _libraryRepository = libraryRepository;
        _mediaItemRepository = mediaItemRepository;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> ScanFolder(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        decimal progressMin,
        decimal progressMax,
        CancellationToken cancellationToken)
    {
        try
        {
            IDeserializer deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .Build();

            decimal progressSpread = progressMax - progressMin;

            var foldersCompleted = 0;

            var allFolders = new System.Collections.Generic.HashSet<string>();
            var folderQueue = new Queue<string>();

            string normalizedLibraryPath = libraryPath.Path.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (libraryPath.Path != normalizedLibraryPath)
            {
                await _libraryRepository.UpdatePath(libraryPath, normalizedLibraryPath);
            }

            ImmutableHashSet<string> allTrashedItems = await _mediaItemRepository.GetAllTrashedItems(libraryPath);

            if (ShouldIncludeFolder(libraryPath.Path) && allFolders.Add(libraryPath.Path))
            {
                folderQueue.Enqueue(libraryPath.Path);
            }

            foreach (string folder in _localFileSystem.ListSubdirectories(libraryPath.Path)
                         .Filter(ShouldIncludeFolder)
                         .Filter(allFolders.Add)
                         .OrderBy(identity))
            {
                folderQueue.Enqueue(folder);
            }

            while (folderQueue.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)foldersCompleted / (foldersCompleted + folderQueue.Count);
                if (!await _scannerProxy.UpdateProgress(
                        progressMin + percentCompletion * progressSpread,
                        cancellationToken))
                {
                    return new ScanCanceled();
                }

                string remoteStreamFolder = folderQueue.Dequeue();
                Option<int> maybeParentFolder =
                    await _libraryRepository.GetParentFolderId(libraryPath, remoteStreamFolder, cancellationToken);

                foldersCompleted++;

                var filesForEtag = _localFileSystem.ListFiles(remoteStreamFolder).ToList();

                var allFiles = filesForEtag
                    .Filter(f => RemoteStreamExtensions.Contains(Path.GetExtension(f).Replace(".", string.Empty)))
                    .Filter(f => !Path.GetFileName(f).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (string subdirectory in _localFileSystem.ListSubdirectories(remoteStreamFolder)
                             .Filter(ShouldIncludeFolder)
                             .Filter(allFolders.Add)
                             .OrderBy(identity))
                {
                    folderQueue.Enqueue(subdirectory);
                }

                string etag = FolderEtag.Calculate(remoteStreamFolder, _localFileSystem);
                LibraryFolder knownFolder = await _libraryRepository.GetOrAddFolder(
                    libraryPath,
                    maybeParentFolder,
                    remoteStreamFolder);

                if (knownFolder.Etag == etag)
                {
                    if (allFiles.Any(allTrashedItems.Contains))
                    {
                        _logger.LogDebug(
                            "Previously trashed items are now present in folder {Folder}",
                            remoteStreamFolder);
                    }
                    else
                    {
                        // etag matches and no trashed items are now present, continue to next folder
                        continue;
                    }
                }
                else
                {
                    _logger.LogDebug(
                        "UPDATE: Etag has changed for folder {Folder}",
                        remoteStreamFolder);
                }

                var hasErrors = false;

                foreach (string file in allFiles.OrderBy(identity))
                {
                    Either<BaseError, MediaItemScanResult<RemoteStream>> maybeVideo = await _remoteStreamRepository
                        .GetOrAdd(libraryPath, knownFolder, file, cancellationToken)
                        .BindT(video => ParseRemoteStreamDefinition(video, deserializer, cancellationToken))
                        .BindT(video => UpdateMetadataWithDefinition(video, cancellationToken))
                        .BindT(video => UpdateOrSynthesizeStatistics(video, ffmpegPath, ffprobePath))
                        .BindT(video => UpdateLibraryFolderId(video, knownFolder))
                        .BindT(video => UpdateThumbnail(video, cancellationToken))
                        //.BindT(UpdateSubtitles)
                        .BindT(FlagNormal);

                    foreach (BaseError error in maybeVideo.LeftToSeq())
                    {
                        _logger.LogWarning("Error processing remote stream at {Path}: {Error}", file, error.Value);
                        hasErrors = true;
                    }

                    foreach (MediaItemScanResult<RemoteStream> result in maybeVideo.RightToSeq())
                    {
                        if (result.IsAdded || result.IsUpdated)
                        {
                            if (!await _scannerProxy.ReindexMediaItems([result.Item.Id], cancellationToken))
                            {
                                _logger.LogWarning("Failed to reindex media items from scanner process");
                                hasErrors = true;
                            }
                        }
                    }
                }

                // only do this once per folder and only if all files processed successfully
                if (!hasErrors)
                {
                    await _libraryRepository.SetEtag(libraryPath, knownFolder, remoteStreamFolder, etag);
                }
            }

            foreach (string path in await _remoteStreamRepository.FindRemoteStreamPaths(libraryPath, cancellationToken))
            {
                if (!_fileSystem.File.Exists(path))
                {
                    _logger.LogInformation("Flagging missing remote stream at {Path}", path);
                    List<int> remoteStreamIds = await FlagFileNotFound(libraryPath, path);
                    if (!await _scannerProxy.ReindexMediaItems(remoteStreamIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to reindex media items from scanner process");
                    }
                }
                else if (Path.GetFileName(path).StartsWith("._", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    List<int> remoteStreamIds = await _remoteStreamRepository.DeleteByPath(libraryPath, path, cancellationToken);
                    if (!await _scannerProxy.RemoveMediaItems(remoteStreamIds.ToArray(), cancellationToken))
                    {
                        _logger.LogWarning("Failed to remove media items from scanner process");
                    }
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            return Unit.Default;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<RemoteStream>>> UpdateLibraryFolderId(
        MediaItemScanResult<RemoteStream> video,
        LibraryFolder libraryFolder)
    {
        MediaFile mediaFile = video.Item.GetHeadVersion().MediaFiles.Head();
        if (mediaFile.LibraryFolderId != libraryFolder.Id)
        {
            await _libraryRepository.UpdateLibraryFolderId(mediaFile, libraryFolder.Id);
            video.IsUpdated = true;
        }

        return video;
    }

    private async Task<Either<BaseError, RemoteStreamWithDefinition>> ParseRemoteStreamDefinition(
        MediaItemScanResult<RemoteStream> result,
        IDeserializer deserializer,
        CancellationToken cancellationToken)
    {
        try
        {
            RemoteStream remoteStream = result.Item;

            string path = remoteStream.GetHeadVersion().MediaFiles.Head().Path;
            string yaml = await File.ReadAllTextAsync(path, cancellationToken);
            YamlRemoteStreamDefinition definition = deserializer.Deserialize<YamlRemoteStreamDefinition>(yaml);
            if (!definition.IsLive.HasValue)
            {
                return BaseError.New("Remote stream definition is missing required `is_live` property");
            }

            var updated = false;
            if (remoteStream.IsLive != definition.IsLive.Value)
            {
                remoteStream.IsLive = definition.IsLive.Value;
                updated = true;
            }

            if (remoteStream.Url != definition.Url)
            {
                remoteStream.Url = definition.Url;
                updated = true;
            }

            if (remoteStream.Script != definition.Script)
            {
                remoteStream.Script = definition.Script;
                updated = true;
            }

            if (TimeSpan.TryParse(definition.Duration, out TimeSpan duration))
            {
                if (remoteStream.Duration != duration)
                {
                    remoteStream.Duration = duration;
                    updated = true;
                }
            }
            else
            {
                if (remoteStream.Duration is not null)
                {
                    remoteStream.Duration = null;
                    updated = true;
                }
            }

            if (remoteStream.FallbackQuery != definition.FallbackQuery)
            {
                remoteStream.FallbackQuery = definition.FallbackQuery;
                updated = true;
            }

            if (string.IsNullOrEmpty(remoteStream.Url) && string.IsNullOrEmpty(remoteStream.Script))
            {
                return BaseError.New($"`url` or `script` is required in remote stream definition file {path}");
            }

            if (updated)
            {
                await _remoteStreamRepository.UpdateDefinition(remoteStream, cancellationToken);
                result.IsUpdated = true;
            }

            return new RemoteStreamWithDefinition(result, definition);
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, RemoteStreamWithDefinition>> UpdateMetadataWithDefinition(
        RemoteStreamWithDefinition result,
        CancellationToken cancellationToken)
    {
        try
        {
            RemoteStream remoteStream = result.Result.Item;
            string path = remoteStream.GetHeadVersion().MediaFiles.Head().Path;
            var shouldUpdate = true;

            foreach (RemoteStreamMetadata remoteStreamMetadata in Optional(remoteStream.RemoteStreamMetadata)
                         .Flatten()
                         .HeadOrNone())
            {
                shouldUpdate = remoteStreamMetadata.MetadataKind == MetadataKind.Fallback ||
                               remoteStreamMetadata.DateUpdated != _localFileSystem.GetLastWriteTime(path);
            }

            if (shouldUpdate)
            {
                remoteStream.RemoteStreamMetadata ??= [];

                _logger.LogDebug("Refreshing {Attribute} for {Path}", "Metadata", path);
                if (await _localMetadataProvider.RefreshMetadata(remoteStream, result.Definition, cancellationToken))
                {
                    result.Result.IsUpdated = true;
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<RemoteStream>>> UpdateOrSynthesizeStatistics(
        RemoteStreamWithDefinition result,
        string ffmpegPath,
        string ffprobePath)
    {
        // downloader-managed items (e.g. yt-dlp) must not be probed remotely during scan;
        // statistics are synthesized here and refreshed from the real file after download
        if (string.IsNullOrWhiteSpace(result.Definition.Downloader))
        {
            return await UpdateStatistics(result.Result, ffmpegPath, ffprobePath);
        }

        try
        {
            RemoteStream remoteStream = result.Result.Item;
            MediaVersion version = remoteStream.GetHeadVersion();

            if (version.Streams.Count == 0)
            {
                string path = version.MediaFiles.Head().Path;

                TimeSpan duration = TimeSpan.TryParse(result.Definition.Duration, out TimeSpan parsed)
                    ? parsed
                    : TimeSpan.Zero;

                var synthesized = new MediaVersion
                {
                    Name = "Main",
                    Duration = duration,
                    Width = 1920,
                    Height = 1080,
                    SampleAspectRatio = "1:1",
                    DisplayAspectRatio = "16:9",
                    RFrameRate = "30/1",
                    VideoScanKind = VideoScanKind.Progressive,
                    DateAdded = DateTime.UtcNow,
                    DateUpdated = _localFileSystem.GetLastWriteTime(path),
                    Streams =
                    [
                        new MediaStream
                        {
                            Index = 0,
                            MediaStreamKind = MediaStreamKind.Video,
                            Codec = "h264",
                            Profile = "high",
                            PixelFormat = "yuv420p",
                            Default = true
                        },
                        new MediaStream
                        {
                            Index = 1,
                            MediaStreamKind = MediaStreamKind.Audio,
                            Codec = "aac",
                            Channels = 2,
                            Default = true
                        }
                    ],
                    Chapters = []
                };

                _logger.LogDebug("Synthesizing statistics for downloader-managed remote stream at {Path}", path);

                if (await _metadataRepository.UpdateStatistics(remoteStream, synthesized))
                {
                    result.Result.IsUpdated = true;
                }
            }

            return result.Result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<RemoteStream>>> UpdateThumbnail(
        MediaItemScanResult<RemoteStream> result,
        CancellationToken cancellationToken)
    {
        try
        {
            RemoteStream remoteStream = result.Item;

            foreach (RemoteStreamMetadata metadata in remoteStream.RemoteStreamMetadata.HeadOrNone())
            {
                Option<string> maybeThumbnail = LocateThumbnail(remoteStream);
                foreach (string thumbnailFile in maybeThumbnail)
                {
                    await RefreshArtwork(thumbnailFile, metadata, ArtworkKind.Thumbnail, None, None, cancellationToken);
                }

                if (maybeThumbnail.IsNone && metadata.Artwork.Any(a => a.ArtworkKind is ArtworkKind.Thumbnail))
                {
                    await _metadataRepository.RemoveArtworkWithKind(metadata, ArtworkKind.Thumbnail);
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            return BaseError.New(ex.ToString());
        }
    }

    private Option<string> LocateThumbnail(RemoteStream remoteStream)
    {
        string path = remoteStream.MediaVersions.Head().MediaFiles.Head().Path;
        return ImageFileExtensions
            .Map(ext => Path.ChangeExtension(path, ext))
            .Filter(f => _fileSystem.File.Exists(f))
            .HeadOrNone();
    }

    private class RemoteStreamWithDefinition(
        MediaItemScanResult<RemoteStream> result,
        YamlRemoteStreamDefinition definition)
    {
        public MediaItemScanResult<RemoteStream> Result { get; } = result;

        public YamlRemoteStreamDefinition Definition { get; } = definition;
    }
}
