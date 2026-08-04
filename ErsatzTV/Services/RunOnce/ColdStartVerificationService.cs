using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using CliWrap;
using CliWrap.Buffered;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.FFmpeg;

namespace ErsatzTV.Services.RunOnce;

public class ColdStartVerificationService(
    IServiceScopeFactory serviceScopeFactory,
    SystemStartup systemStartup,
    ILogger<ColdStartVerificationService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PortReadyTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        await systemStartup.WaitForDatabase(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        await systemStartup.WaitForSearchIndex(stoppingToken);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        if (!await WaitForPortAsync(Settings.UiPort, PortReadyTimeout, stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            logger.LogError(
                "Coldstart verification failed: UI port {UiPort} did not begin listening in time",
                Settings.UiPort);
            return;
        }

        var failures = new List<string>();

        await VerifyScannerAsync(failures, stoppingToken);
        await VerifyUiHttpAsync(failures, stoppingToken);
        await VerifyStreamingPortAsync(failures, stoppingToken);
        await VerifyFFmpegAsync(failures, stoppingToken);

        if (failures.Count == 0)
        {
            logger.LogInformation(
                "Coldstart verification passed (scanner, UI :{UiPort}, streaming :{StreamingPort}, ffmpeg)",
                Settings.UiPort,
                Settings.StreamingPort);
            return;
        }

        logger.LogError(
            "Coldstart verification failed ({FailureCount}): {Failures}",
            failures.Count,
            string.Join("; ", failures));
    }

    private async Task VerifyScannerAsync(List<string> failures, CancellationToken cancellationToken)
    {
        Option<string> maybeScanner = ResolveScannerPath();
        if (maybeScanner.IsNone)
        {
            const string Failure = "scanner executable not found";
            failures.Add(Failure);
            logger.LogError("Coldstart: {Failure}", Failure);
            return;
        }

        foreach (string scannerPath in maybeScanner)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(CheckTimeout);

                BufferedCommandResult result = await Cli.Wrap(scannerPath)
                    .WithArguments(["verify"])
                    .WithValidation(CommandResultValidation.None)
                    .WithWorkingDirectory(Path.GetDirectoryName(scannerPath) ?? AppContext.BaseDirectory)
                    .ExecuteBufferedAsync(timeoutCts.Token);

                if (result.ExitCode != 0)
                {
                    string detail = FirstNonEmptyLine(result.StandardError, result.StandardOutput)
                        .IfNone($"exit code {result.ExitCode}");
                    string failure = $"scanner verify failed ({detail})";
                    failures.Add(failure);
                    logger.LogError("Coldstart: {Failure}", failure);
                    return;
                }

                logger.LogInformation("Coldstart: scanner verify ok ({Scanner})", scannerPath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                string failure = $"scanner verify threw: {ex.Message}";
                failures.Add(failure);
                logger.LogError(ex, "Coldstart: {Failure}", failure);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                const string Failure = "scanner verify timed out";
                failures.Add(Failure);
                logger.LogError("Coldstart: {Failure}", Failure);
            }
        }
    }

    private async Task VerifyUiHttpAsync(List<string> failures, CancellationToken cancellationToken)
    {
        string url = $"http://127.0.0.1:{Settings.UiPort}/";
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using HttpResponseMessage response = await http.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                string failure = $"UI HTTP {url} returned {(int)response.StatusCode}";
                failures.Add(failure);
                logger.LogError("Coldstart: {Failure}", failure);
                return;
            }

            logger.LogInformation("Coldstart: UI HTTP ok ({Url} => {StatusCode})", url, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string failure = $"UI HTTP {url} failed: {ex.Message}";
            failures.Add(failure);
            logger.LogError(ex, "Coldstart: {Failure}", failure);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string failure = $"UI HTTP {url} timed out";
            failures.Add(failure);
            logger.LogError("Coldstart: {Failure}", failure);
        }
    }

    private async Task VerifyStreamingPortAsync(List<string> failures, CancellationToken cancellationToken)
    {
        try
        {
            if (!await WaitForPortAsync(Settings.StreamingPort, TimeSpan.FromSeconds(5), cancellationToken))
            {
                string failure = $"streaming port {Settings.StreamingPort} is not listening";
                failures.Add(failure);
                logger.LogError("Coldstart: {Failure}", failure);
                return;
            }

            logger.LogInformation("Coldstart: streaming port ok (:{StreamingPort})", Settings.StreamingPort);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string failure = $"streaming port check failed: {ex.Message}";
            failures.Add(failure);
            logger.LogError(ex, "Coldstart: {Failure}", failure);
        }
    }

    private async Task VerifyFFmpegAsync(List<string> failures, CancellationToken cancellationToken)
    {
        try
        {
            using IServiceScope scope = serviceScopeFactory.CreateScope();
            IFFmpegLocator ffmpegLocator = scope.ServiceProvider.GetRequiredService<IFFmpegLocator>();

            Option<string> maybeFFmpeg = await ffmpegLocator.ValidatePath(
                "ffmpeg",
                ConfigElementKey.FFmpegPath,
                cancellationToken);
            Option<string> maybeFFprobe = await ffmpegLocator.ValidatePath(
                "ffprobe",
                ConfigElementKey.FFprobePath,
                cancellationToken);

            if (maybeFFmpeg.IsNone)
            {
                const string Failure = "ffmpeg executable not found";
                failures.Add(Failure);
                logger.LogError("Coldstart: {Failure}", Failure);
            }
            else
            {
                foreach (string path in maybeFFmpeg)
                {
                    logger.LogInformation("Coldstart: ffmpeg ok ({Path})", path);
                }
            }

            if (maybeFFprobe.IsNone)
            {
                const string Failure = "ffprobe executable not found";
                failures.Add(Failure);
                logger.LogError("Coldstart: {Failure}", Failure);
            }
            else
            {
                foreach (string path in maybeFFprobe)
                {
                    logger.LogInformation("Coldstart: ffprobe ok ({Path})", path);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string failure = $"ffmpeg check failed: {ex.Message}";
            failures.Add(failure);
            logger.LogError(ex, "Coldstart: {Failure}", failure);
        }
    }

    private static Option<string> ResolveScannerPath()
    {
        string executable = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "ErsatzTV.Scanner.exe"
            : "ErsatzTV.Scanner";

        string processFileName = Environment.ProcessPath ?? string.Empty;
        string processExecutable = Path.GetFileNameWithoutExtension(processFileName);
        string folderName = Path.GetDirectoryName(processFileName);
        if ("dotnet".Equals(processExecutable, StringComparison.OrdinalIgnoreCase))
        {
            folderName = AppContext.BaseDirectory;
        }

        if (string.IsNullOrWhiteSpace(folderName))
        {
            return None;
        }

        string localFileName = Path.Combine(folderName, executable);
        return File.Exists(localFileName) ? Some(localFileName) : None;
    }

    private static async Task<bool> WaitForPortAsync(int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        while (!timeoutCts.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", port, timeoutCts.Token);
                return true;
            }
            catch (SocketException)
            {
                await Task.Delay(200, timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return false;
    }

    private static Option<string> FirstNonEmptyLine(params string[] chunks)
    {
        var text = new StringBuilder();
        foreach (string chunk in chunks)
        {
            if (!string.IsNullOrWhiteSpace(chunk))
            {
                text.AppendLine(chunk.Trim());
            }
        }

        string combined = text.ToString();
        if (string.IsNullOrWhiteSpace(combined))
        {
            return None;
        }

        using var reader = new StringReader(combined);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                return Some(line.Length > 300 ? line[..300] : line);
            }
        }

        return None;
    }
}
