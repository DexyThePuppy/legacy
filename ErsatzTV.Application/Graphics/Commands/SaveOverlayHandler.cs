using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Graphics;

public partial class SaveOverlayHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFileSystem fileSystem,
    IMediator mediator)
    : IRequestHandler<SaveOverlay, Either<BaseError, int>>
{
    public async Task<Either<BaseError, int>> Handle(SaveOverlay request, CancellationToken cancellationToken)
    {
        try
        {
            await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

            string path;
            if (request.Id is > 0)
            {
                Option<GraphicsElement> maybeElement = await dbContext.GraphicsElements
                    .AsNoTracking()
                    .SelectOneAsync(ge => ge.Id, ge => ge.Id == request.Id.Value, cancellationToken);

                if (maybeElement.IsNone)
                {
                    return BaseError.New($"Overlay {request.Id} does not exist");
                }

                path = maybeElement.Map(e => e.Path).Head();
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.Name))
                {
                    return BaseError.New("Overlay name is required");
                }

                path = GetNewFilePath(request.Name);
            }

            string content = string.IsNullOrWhiteSpace(request.RawContent)
                ? BuildYaml(request)
                : request.RawContent;

            await fileSystem.File.WriteAllTextAsync(path, content, cancellationToken);

            // register (or refresh) the graphics element in the database
            await mediator.Send(new RefreshGraphicsElements(), cancellationToken);

            Option<GraphicsElement> maybeSaved = await dbContext.GraphicsElements
                .AsNoTracking()
                .SelectOneAsync(ge => ge.Path, ge => ge.Path == path, cancellationToken);

            foreach (GraphicsElement saved in maybeSaved)
            {
                return saved.Id;
            }

            return BaseError.New("Failed to register overlay after saving");
        }
        catch (Exception ex)
        {
            return BaseError.New($"Failed to save overlay: {ex.Message}");
        }
    }

    private string GetNewFilePath(string name)
    {
        string slug = SlugRegex().Replace(name.ToLowerInvariant().Trim(), "-").Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "overlay";
        }

        string path = Path.Combine(FileSystemLayout.GraphicsElementsHtmlTemplatesFolder, $"{slug}.yml");
        var counter = 2;
        while (fileSystem.File.Exists(path))
        {
            path = Path.Combine(FileSystemLayout.GraphicsElementsHtmlTemplatesFolder, $"{slug}-{counter}.yml");
            counter++;
        }

        return path;
    }

    private static string BuildYaml(SaveOverlay request)
    {
        var sb = new StringBuilder();

        sb.AppendLine(CultureInfo.InvariantCulture, $"name: {QuoteYamlString(request.Name)}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"location: {request.Location}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"horizontal_margin_percent: {request.HorizontalMarginPercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"vertical_margin_percent: {request.VerticalMarginPercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"width_percent: {request.WidthPercent.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"height_percent: {request.HeightPercent.ToString(CultureInfo.InvariantCulture)}");

        if (string.IsNullOrWhiteSpace(request.OpacityExpression))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"opacity_percent: {request.OpacityPercent}");
        }
        else
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"opacity_expression: {QuoteYamlString(request.OpacityExpression)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"z_index: {request.ZIndex}");

        foreach (double captureFps in Optional(request.CaptureFps))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"capture_fps: {captureFps.ToString(CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine(CultureInfo.InvariantCulture, $"epg_entries: {request.EpgEntries}");

        foreach (double startSeconds in Optional(request.StartSeconds))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"start_seconds: {startSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (double durationSeconds in Optional(request.DurationSeconds))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"duration_seconds: {durationSeconds.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (double startSecondsFromEnd in Optional(request.StartSecondsFromEnd))
        {
            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"start_seconds_from_end: {startSecondsFromEnd.ToString(CultureInfo.InvariantCulture)}");
        }

        sb.AppendLine("html: |");
        foreach (string line in (request.Html ?? string.Empty).ReplaceLineEndings("\n").Split('\n'))
        {
            sb.Append("  ").Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string QuoteYamlString(string value)
    {
        string escaped = (value ?? string.Empty)
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

        return $"\"{escaped}\"";
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
