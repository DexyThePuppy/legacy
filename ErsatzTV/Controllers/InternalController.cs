using System.CommandLine.Parsing;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using CliWrap;
using ErsatzTV.Application.Emby;
using ErsatzTV.Application.Jellyfin;
using ErsatzTV.Application.MediaItems;
using ErsatzTV.Application.Plex;
using ErsatzTV.Application.Streaming;
using ErsatzTV.Application.Subtitles;
using ErsatzTV.Application.Subtitles.Queries;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Interfaces.YouTube;
using ErsatzTV.Core.YouTube;
using ErsatzTV.Extensions;
using ErsatzTV.FFmpeg;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using ErsatzTV.Infrastructure.Scheduling;
using ErsatzTV.Infrastructure.YouTube;
using Flurl;
using LanguageExt.UnsafeValueAccess;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;

namespace ErsatzTV.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class InternalController : StreamingControllerBase
{
    private readonly ILogger<InternalController> _logger;
    private readonly IMediator _mediator;
    private readonly IDbContextFactory<TvContext> _dbContextFactory;
    private readonly IDynamicPlayoutItemService _dynamicPlayoutItemService;
    private readonly IPlayoutItemConverter _playoutItemConverter;
    private readonly IYtDlpService _ytDlpService;
    private readonly IYouTubePlaybackResolver _youTubePlaybackResolver;

    public InternalController(
        IGraphicsEngine graphicsEngine,
        IMediator mediator,
        IDbContextFactory<TvContext> dbContextFactory,
        IDynamicPlayoutItemService dynamicPlayoutItemService,
        IPlayoutItemConverter playoutItemConverter,
        IYtDlpService ytDlpService,
        IYouTubePlaybackResolver youTubePlaybackResolver,
        ILogger<InternalController> logger)
        : base(graphicsEngine, logger)
    {
        _mediator = mediator;
        _dbContextFactory = dbContextFactory;
        _dynamicPlayoutItemService = dynamicPlayoutItemService;
        _playoutItemConverter = playoutItemConverter;
        _ytDlpService = ytDlpService;
        _youTubePlaybackResolver = youTubePlaybackResolver;
        _logger = logger;
    }

    [HttpGet("ffmpeg/concat/{channelNumber}")]
    public Task<IActionResult> GetConcatPlaylist(string channelNumber, [FromQuery] string mode = "ts-legacy") =>
        _mediator.Send(
                new GetConcatPlaylistByChannelNumber(Request.Scheme, Request.Host.ToString(), channelNumber, mode))
            .ToActionResult();

    [HttpGet("ffmpeg/stream/{channelNumber}")]
    public Task<IActionResult> GetStream(string channelNumber) => GetTsLegacyStream(channelNumber);

    [HttpGet("ffmpeg/preview/{channelNumber}/overlays.html")]
    [Produces("text/html")]
    public async Task<IActionResult> GetChannelPreviewOverlays(
        string channelNumber,
        CancellationToken cancellationToken)
    {
        string requestBase = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
        Either<BaseError, string> result = await _mediator.Send(
            new GetChannelRawPreviewOverlays(channelNumber, requestBase),
            cancellationToken);

        foreach (BaseError error in result.LeftToSeq())
        {
            return NotFound(error.Value);
        }

        foreach (string html in result.RightToSeq())
        {
            Response.Headers.CacheControl = "no-cache, no-store";
            return Content(html, "text/html; charset=utf-8");
        }

        return NotFound();
    }

    [HttpGet("ffmpeg/preview/{channelNumber}")]
    public async Task GetChannelPreview(
        string channelNumber,
        [FromQuery] int? fps,
        CancellationToken cancellationToken)
    {
        int previewFps = Math.Clamp(fps ?? 10, 1, 60);

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<string> maybeFFmpegPath = await dbContext.ConfigElements.GetValue<string>(
            ConfigElementKey.FFmpegPath,
            cancellationToken);

        if (maybeFFmpegPath.IsNone)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        string ffmpegPath = maybeFFmpegPath.ValueUnsafe();

        Either<BaseError, ChannelPreviewSource> initialSource =
            await _mediator.Send(new GetChannelPreviewSource(channelNumber), cancellationToken);

        foreach (BaseError error in initialSource.LeftToSeq())
        {
            _logger.LogDebug("Channel preview unavailable for {Channel}: {Error}", channelNumber, error.Value);
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        Response.ContentType = "multipart/x-mixed-replace;boundary=ffmpeg";
        Response.Headers.CacheControl = "no-cache, no-store";

        while (!cancellationToken.IsCancellationRequested)
        {
            Either<BaseError, ChannelPreviewSource> maybeSource =
                await _mediator.Send(new GetChannelPreviewSource(channelNumber), cancellationToken);

            foreach (BaseError error in maybeSource.LeftToSeq())
            {
                _logger.LogDebug(
                    "Channel preview source unavailable for {Channel}, ending stream: {Error}",
                    channelNumber,
                    error.Value);
                return;
            }

            foreach (ChannelPreviewSource source in maybeSource.RightToSeq())
            {
                using FFmpegProcess process = CreateChannelPreviewProcess(ffmpegPath, source, previewFps);

                _logger.LogDebug(
                    "Starting {Fps}fps MJPEG preview segment for channel {Channel} at {Seek} (paused={Paused})",
                    previewFps,
                    channelNumber,
                    source.Seek,
                    source.IsPaused);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to start channel preview ffmpeg for channel {Channel}", channelNumber);
                    return;
                }

                try
                {
                    await process.StandardOutput.BaseStream.CopyToAsync(Response.Body, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (IOException)
                {
                    return;
                }

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                if (process.ExitCode != 0)
                {
                    _logger.LogDebug(
                        "Channel preview ffmpeg exited with code {ExitCode} for channel {Channel}",
                        process.ExitCode,
                        channelNumber);
                }

                // Paused freeze runs until disconnect; avoid a tight restart loop.
                if (source.IsPaused)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
                else if (source.IsLive)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                }
            }
        }
    }

    private static FFmpegProcess CreateChannelPreviewProcess(string ffmpegPath, ChannelPreviewSource source, int fps)
    {
        var process = new FFmpegProcess
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Higher fps dialog previews use a larger scale; table thumbs stay small.
        int scaleWidth = fps >= 30 ? 1280 : 320;
        string scale = $"scale={scaleWidth}:-2:flags=fast_bilinear";

        process.StartInfo.ArgumentList.Add("-hide_banner");
        process.StartInfo.ArgumentList.Add("-nostats");
        process.StartInfo.ArgumentList.Add("-loglevel");
        process.StartInfo.ArgumentList.Add("error");
        if (!source.IsLive && source.Seek > TimeSpan.Zero)
        {
            process.StartInfo.ArgumentList.Add("-ss");
            process.StartInfo.ArgumentList.Add(source.Seek.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }

        // While paused, freeze a single frame so the preview does not play ahead and jump back.
        if (source.IsPaused && !source.IsLive)
        {
            process.StartInfo.ArgumentList.Add("-i");
            process.StartInfo.ArgumentList.Add(source.Path);
            process.StartInfo.ArgumentList.Add("-an");
            process.StartInfo.ArgumentList.Add("-vf");
            process.StartInfo.ArgumentList.Add(
                $"{scale},loop=loop=-1:size=1:start=0,setpts=N/{fps}/TB,fps={fps}");
            process.StartInfo.ArgumentList.Add("-c:v");
            process.StartInfo.ArgumentList.Add("mjpeg");
            process.StartInfo.ArgumentList.Add("-q:v");
            process.StartInfo.ArgumentList.Add(fps >= 30 ? "5" : "8");
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("mpjpeg");
            process.StartInfo.ArgumentList.Add("-");
            return process;
        }

        if (!source.IsLive)
        {
            process.StartInfo.ArgumentList.Add("-re");
        }

        process.StartInfo.ArgumentList.Add("-i");
        process.StartInfo.ArgumentList.Add(source.Path);
        process.StartInfo.ArgumentList.Add("-an");
        // Short chunks so each restart re-reads playout seek (controls stay in sync).
        if (!source.IsLive)
        {
            process.StartInfo.ArgumentList.Add("-t");
            process.StartInfo.ArgumentList.Add("2");
        }

        process.StartInfo.ArgumentList.Add("-vf");
        process.StartInfo.ArgumentList.Add($"fps={fps},{scale}");
        process.StartInfo.ArgumentList.Add("-c:v");
        process.StartInfo.ArgumentList.Add("mjpeg");
        process.StartInfo.ArgumentList.Add("-q:v");
        process.StartInfo.ArgumentList.Add(fps >= 30 ? "5" : "8");
        process.StartInfo.ArgumentList.Add("-f");
        process.StartInfo.ArgumentList.Add("mpjpeg");
        process.StartInfo.ArgumentList.Add("-");

        return process;
    }

    [HttpGet("ffmpeg/music-video-credits/{playoutItemId:int}")]
    public async Task<IActionResult> GetMusicVideoCredits(
        int playoutItemId,
        [FromQuery]
        long? seekToMs,
        CancellationToken cancellationToken)
    {
        Option<string> maybeCreditsFile = await _mediator.Send(
            new GetMusicVideoCreditsByPlayoutItemId(playoutItemId, Optional(seekToMs)),
            cancellationToken);
        foreach (string creditsFile in maybeCreditsFile)
        {
            return new PhysicalFileResult(creditsFile, "text/x-ssa");
        }

        return File(Encoding.UTF8.GetBytes(EmptySubtitleDocument("text/x-ssa")), "text/x-ssa");
    }

    [HttpGet("ffmpeg/remote-stream/{remoteStreamId}")]
    public async Task<IActionResult> GetRemoteStream(int remoteStreamId, CancellationToken cancellationToken)
    {
        Option<RemoteStreamViewModel> maybeRemoteStream =
            await _mediator.Send(new GetRemoteStreamById(remoteStreamId), cancellationToken);

        foreach (RemoteStreamViewModel remoteStream in maybeRemoteStream)
        {
            if (!string.IsNullOrWhiteSpace(remoteStream.Url))
            {
                return new RedirectResult(remoteStream.Url);
            }

            if (!string.IsNullOrWhiteSpace(remoteStream.Script))
            {
                var split = CommandLineParser.SplitCommandLine(remoteStream.Script).ToList();
                if (split.Count > 0)
                {
                    _logger.LogDebug("Remote stream script: {Arguments}", split);

                    Command command = Cli.Wrap(split.Head());
                    if (split.Count > 1)
                    {
                        command = command.WithArguments(split.Tail());
                    }

                    var process = new FFmpegProcess
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = command.TargetFilePath,
                            Arguments = command.Arguments,
                            RedirectStandardOutput = true,
                            RedirectStandardError = false,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    HttpContext.Response.RegisterForDispose(process);

                    foreach ((string key, string value) in command.EnvironmentVariables)
                    {
                        process.StartInfo.Environment[key] = value;
                    }

                    process.Start();
                    return new FileStreamResult(process.StandardOutput.BaseStream, "video/mp2t");
                }
            }
        }

        return NotFound();
    }

    [HttpGet("ffmpeg/ytdlp/{remoteStreamId:int}")]
    public async Task<IActionResult> GetYtDlpStream(int remoteStreamId, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<RemoteStream> maybeRemoteStream = await dbContext.RemoteStreams
            .AsNoTracking()
            .Include(rs => rs.MediaVersions)
            .ThenInclude(mv => mv.MediaFiles)
            .SelectOneAsync(rs => rs.Id, rs => rs.Id == remoteStreamId, cancellationToken);

        foreach (RemoteStream remoteStream in maybeRemoteStream)
        {
            // serve from cache when a download completed after this url was handed out
            foreach (string videoId in _youTubePlaybackResolver.VideoIdForRemoteStream(remoteStream))
            {
                foreach (string cached in _ytDlpService.GetCachedFile(videoId))
                {
                    return PhysicalFile(cached, "video/mp4", enableRangeProcessing: true);
                }
            }

            if (string.IsNullOrWhiteSpace(remoteStream.Url))
            {
                return NotFound();
            }

            Option<string> maybeYtDlpPath = await _ytDlpService.LocateYtDlp(cancellationToken);
            Option<string> maybeFFmpegPath = await dbContext.ConfigElements.GetValue<string>(
                ConfigElementKey.FFmpegPath,
                cancellationToken);

            if (maybeYtDlpPath.IsNone || maybeFFmpegPath.IsNone)
            {
                _logger.LogWarning("Unable to stream via yt-dlp; yt-dlp or ffmpeg path is not configured");
                return NotFound();
            }

            YtDlpSettings settings = await _ytDlpService.GetSettings(cancellationToken);

            var ytDlpProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = maybeYtDlpPath.ValueUnsafe(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            ytDlpProcess.StartInfo.ArgumentList.Add("-f");
            ytDlpProcess.StartInfo.ArgumentList.Add(settings.Format);
            ytDlpProcess.StartInfo.ArgumentList.Add("--no-warnings");
            ytDlpProcess.StartInfo.ArgumentList.Add("--no-playlist");
            ytDlpProcess.StartInfo.ArgumentList.Add("--quiet");

            foreach (string arg in YtDlpSettings.SplitExtraArgs(settings.ExtraArgs))
            {
                ytDlpProcess.StartInfo.ArgumentList.Add(arg);
            }

            ytDlpProcess.StartInfo.ArgumentList.Add("-o");
            ytDlpProcess.StartInfo.ArgumentList.Add("-");
            ytDlpProcess.StartInfo.ArgumentList.Add(remoteStream.Url);

            // ensure deno is available for youtube signature solving
            Option<string> maybeDenoPath = await _ytDlpService.LocateDeno(cancellationToken);
            foreach (string denoPath in maybeDenoPath)
            {
                string denoDir = Path.GetDirectoryName(denoPath) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(denoDir))
                {
                    string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    ytDlpProcess.StartInfo.Environment["PATH"] =
                        $"{denoDir}{System.IO.Path.PathSeparator}{pathVariable}";
                }
            }

            var ffmpegProcess = new FFmpegProcess
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = maybeFFmpegPath.ValueUnsafe(),
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            foreach (string arg in new[]
                     {
                         "-hide_banner", "-nostats", "-loglevel", "error",
                         "-i", "pipe:0",
                         "-c", "copy",
                         "-f", "mpegts", "pipe:1"
                     })
            {
                ffmpegProcess.StartInfo.ArgumentList.Add(arg);
            }

            HttpContext.Response.RegisterForDispose(ytDlpProcess);
            HttpContext.Response.RegisterForDispose(ffmpegProcess);

            _logger.LogDebug("Live streaming remote stream {Id} via yt-dlp", remoteStreamId);

            ytDlpProcess.Start();
            Task<string> stderrTask = ytDlpProcess.StandardError.ReadToEndAsync(cancellationToken);
            ffmpegProcess.Start();

            // pump yt-dlp stdout into ffmpeg stdin in the background
            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        await ytDlpProcess.StandardOutput.BaseStream.CopyToAsync(
                            ffmpegProcess.StandardInput.BaseStream,
                            cancellationToken);
                    }
                    catch (Exception)
                    {
                        // ignored - the client disconnected or a process exited
                    }
                    finally
                    {
                        try
                        {
                            ffmpegProcess.StandardInput.Close();
                        }
                        catch (Exception)
                        {
                            // ignored
                        }
                    }
                },
                cancellationToken);

            // wait for first remuxed bytes so age-gate / cookie failures don't return empty 200
            var firstChunk = new byte[16 * 1024];
            int bytesRead;
            using (var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                startupCts.CancelAfter(TimeSpan.FromSeconds(20));
                try
                {
                    bytesRead = await ffmpegProcess.StandardOutput.BaseStream.ReadAsync(
                        firstChunk.AsMemory(0, firstChunk.Length),
                        startupCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    bytesRead = 0;
                }
            }

            if (bytesRead <= 0)
            {
                try
                {
                    if (!ytDlpProcess.HasExited)
                    {
                        ytDlpProcess.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception)
                {
                    // ignored
                }

                string stderr = string.Empty;
                try
                {
                    stderr = await stderrTask.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
                catch (Exception)
                {
                    // ignored
                }

                _logger.LogWarning(
                    "yt-dlp live stream for remote stream {Id} produced no media: {Error}",
                    remoteStreamId,
                    string.IsNullOrWhiteSpace(stderr) ? "no stderr output" : stderr.Trim());

                return StatusCode(StatusCodes.Status502BadGateway);
            }

            _ = Task.Run(
                async () =>
                {
                    try
                    {
                        string stderr = await stderrTask;
                        if (!string.IsNullOrWhiteSpace(stderr))
                        {
                            _logger.LogDebug(
                                "yt-dlp live stream stderr for remote stream {Id}: {Error}",
                                remoteStreamId,
                                stderr.Trim());
                        }
                    }
                    catch (Exception)
                    {
                        // ignored
                    }
                },
                CancellationToken.None);

            Response.ContentType = "video/mp2t";
            await Response.Body.WriteAsync(firstChunk.AsMemory(0, bytesRead), cancellationToken);
            await ffmpegProcess.StandardOutput.BaseStream.CopyToAsync(Response.Body, cancellationToken);
            return new EmptyResult();
        }

        return NotFound();
    }

    [HttpGet("/media/plex/{plexMediaSourceId:int}/{*path}")]
    public async Task<IActionResult> GetPlexMedia(
        int plexMediaSourceId,
        string path,
        CancellationToken cancellationToken)
    {
#if DEBUG_NO_SYNC
        await Task.Delay(100, cancellationToken);
        return NotFound();
#else
        Either<BaseError, PlexConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetPlexConnectionParameters(plexMediaSourceId), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath = new Uri(r.Uri, path).SetQueryParam("X-Plex-Token", r.AuthToken);
                return new RedirectResult(fullPath.ToString());
            });
#endif
    }

    [HttpGet("/media/jellyfin/{*path}")]
    public async Task<IActionResult> GetJellyfinMedia(string path, CancellationToken cancellationToken)
    {
        Either<BaseError, JellyfinConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetJellyfinConnectionParameters(), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath;

                if (path.Contains("Subtitles"))
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment(path);
                }
                else
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment("Videos")
                        .AppendPathSegment(path)
                        .AppendPathSegment("stream")
                        .SetQueryParam("static", "true");
                }

                return new RedirectResult(fullPath.ToString());
            });
    }

    [HttpGet("/media/emby/{*path}")]
    public async Task<IActionResult> GetEmbyMedia(string path, CancellationToken cancellationToken)
    {
        Either<BaseError, EmbyConnectionParametersViewModel> connectionParameters =
            await _mediator.Send(new GetEmbyConnectionParameters(), cancellationToken);

        return connectionParameters.Match<IActionResult>(
            Left: _ => new NotFoundResult(),
            Right: r =>
            {
                Url fullPath;

                if (path.Contains("Subtitles"))
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment(path)
                        .SetQueryParam("X-Emby-Token", r.ApiKey);
                }
                else
                {
                    fullPath = Flurl.Url.Parse(r.Address)
                        .AppendPathSegment("Videos")
                        .AppendPathSegment(path)
                        .AppendPathSegment("stream")
                        .SetQueryParam("static", "true")
                        .SetQueryParam("X-Emby-Token", r.ApiKey);
                }

                return new RedirectResult(fullPath.ToString());
            });
    }

    [HttpGet("/media/subtitle/{id:int}")]
    public async Task<IActionResult> GetSubtitle(
        int id,
        [FromQuery] long? seekToMs,
        CancellationToken cancellationToken)
    {
        Either<BaseError, SubtitlePathAndCodec> maybePath = await _mediator.Send(
            new GetSubtitlePathById(id),
            cancellationToken);

        foreach (SubtitlePathAndCodec pathAndCodec in maybePath.RightToSeq())
        {
            string mimeType = Path.GetExtension(pathAndCodec.Path ?? string.Empty).ToLowerInvariant() switch
            {
                ".ass" or ".ssa" => "text/x-ssa",
                ".vtt" => "text/vtt",
                _ when pathAndCodec.Codec.ToLowerInvariant() is "ass" or "ssa" => "text/x-ssa",
                _ when pathAndCodec.Codec.ToLowerInvariant() is "vtt" => "text/vtt",
                _ => "application/x-subrip"
            };

            if (seekToMs is > 0)
            {
                Either<BaseError, SeekTextSubtitleProcess> maybeProcess = await _mediator.Send(
                    new GetSeekTextSubtitleProcess(pathAndCodec, TimeSpan.FromMilliseconds(seekToMs.Value)),
                    cancellationToken);
                foreach (SeekTextSubtitleProcess processModel in maybeProcess.RightToSeq())
                {
                    Command command = processModel.Process;

                    _logger.LogDebug("ffmpeg text subtitle arguments {FFmpegArguments}", command.Arguments);

                    var process = new FFmpegProcess
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = command.TargetFilePath,
                            Arguments = command.Arguments,
                            RedirectStandardOutput = true,
                            RedirectStandardError = false,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    HttpContext.Response.RegisterForDispose(process);

                    foreach ((string key, string value) in command.EnvironmentVariables)
                    {
                        process.StartInfo.Environment[key] = value;
                    }

                    process.Start();
                    using var buffer = new MemoryStream();
                    await process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);

                    byte[] bytes = buffer.ToArray();
                    if (bytes.Length == 0)
                    {
                        return Content(EmptySubtitleDocument(mimeType), mimeType);
                    }

                    return File(bytes, mimeType);
                }

                return Content(EmptySubtitleDocument(mimeType), mimeType);
            }

            if (pathAndCodec.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return new RedirectResult(pathAndCodec.Path);
            }

            return new PhysicalFileResult(pathAndCodec.Path, mimeType);
        }

        return new NotFoundResult();
    }

    [HttpGet("/media/fallback")]
    public async Task<IActionResult> GetFallbackPlayoutJson(CancellationToken cancellationToken)
    {
        if (!Request.Headers.TryGetValue("x-etv-channel", out StringValues channelNumber) || channelNumber.Count != 1)
        {
            return BadRequest();
        }

        if (!Request.Headers.TryGetValue("x-etv-now", out StringValues nowString) || nowString.Count != 1 ||
            !DateTimeOffset.TryParse(nowString[0], out DateTimeOffset now))
        {
            return BadRequest();
        }

        await using TvContext dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
        Option<Channel> maybeChannel = await dbContext.Channels
            .SingleOrDefaultAsync(c => c.Number == channelNumber[0], cancellationToken)
            .Map(Optional);

        foreach (var channel in maybeChannel)
        {
            Either<BaseError, PlayoutItemWithPath> maybePlayoutItem =
                await _dynamicPlayoutItemService.CheckForFallbackFiller(
                    dbContext,
                    channel,
                    now,
                    cancellationToken);

            foreach (var itemWithPath in maybePlayoutItem.RightToSeq())
            {
                Option<Core.Next.PlayoutItem> maybeNextPlayoutItem = await _playoutItemConverter.ToNext(
                    channelNumber[0],
                    itemWithPath.PlayoutItem,
                    cancellationToken);

                foreach (Core.Next.PlayoutItem nextPlayoutItem in maybeNextPlayoutItem)
                {
                    return Content(
                        System.Text.Json.JsonSerializer.Serialize(nextPlayoutItem, Core.Next.Converter.Settings),
                        "application/json");
                }
            }

        }

        return NotFound();
    }

    private async Task<IActionResult> GetTsLegacyStream(string channelNumber)
    {
        var request = new GetPlayoutItemProcessByChannelNumber(
            channelNumber,
            StreamingMode.TransportStream,
            DateTimeOffset.Now,
            false,
            true,
            DateTimeOffset.Now,
            TimeSpan.Zero,
            Option<FrameRate>.None,
            IsTroubleshooting: false,
            Option<int>.None);

        Either<BaseError, PlayoutItemProcessModel> result = await _mediator.Send(request);

        return GetProcessResponse(result, channelNumber, StreamingMode.TransportStream);
    }

    private static string EmptySubtitleDocument(string mimeType) => mimeType switch
    {
        "text/x-ssa" => "[Script Info]\nScriptType: v4.00+\n\n" +
                        "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,Arial,20\n\n" +
                        "[Events]\nFormat: Layer, Start, End, Style, Text\n",
        "text/vtt" => "WEBVTT\n\n",
        _ => "1\n00:00:00,000 --> 00:00:00,001\n \n\n"
    };
}
