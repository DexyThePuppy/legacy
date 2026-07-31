using System.IO.Abstractions;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Graphics;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ErsatzTV.Application.Graphics;

public class GetOverlayByIdHandler(
    IDbContextFactory<TvContext> dbContextFactory,
    IFileSystem fileSystem,
    ILogger<GetOverlayByIdHandler> logger)
    : IRequestHandler<GetOverlayById, Option<OverlayEditViewModel>>
{
    public async Task<Option<OverlayEditViewModel>> Handle(GetOverlayById request, CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<GraphicsElement> maybeElement = await dbContext.GraphicsElements
            .AsNoTracking()
            .SelectOneAsync(ge => ge.Id, ge => ge.Id == request.Id, cancellationToken);

        foreach (GraphicsElement element in maybeElement)
        {
            if (!fileSystem.File.Exists(element.Path))
            {
                return Option<OverlayEditViewModel>.None;
            }

            string rawContent = await fileSystem.File.ReadAllTextAsync(element.Path, cancellationToken);

            var result = new OverlayEditViewModel
            {
                Id = element.Id,
                FileName = Path.GetFileName(element.Path),
                Kind = element.Kind,
                RawContent = rawContent,
                Name = element.Name
            };

            if (element.Kind is GraphicsElementKind.Html)
            {
                try
                {
                    IDeserializer deserializer = new DeserializerBuilder()
                        .WithNamingConvention(CamelCaseNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();

                    var htmlElement = deserializer.Deserialize<HtmlGraphicsElement>(rawContent);

                    result.IsParsed = true;
                    result.Name = htmlElement.Name;
                    result.Html = htmlElement.Html;
                    result.Location = htmlElement.Location;
                    result.HorizontalMarginPercent = htmlElement.HorizontalMarginPercent ?? 0;
                    result.VerticalMarginPercent = htmlElement.VerticalMarginPercent ?? 0;
                    result.WidthPercent = htmlElement.WidthPercent ?? 100;
                    result.HeightPercent = htmlElement.HeightPercent ?? 100;
                    result.OpacityPercent = htmlElement.OpacityPercent ?? 100;
                    result.OpacityExpression = htmlElement.OpacityExpression;
                    result.ZIndex = htmlElement.ZIndex ?? 0;
                    result.CaptureFps = htmlElement.CaptureFps;
                    result.EpgEntries = htmlElement.EpgEntries;
                    result.StartSeconds = htmlElement.StartSeconds;
                    result.DurationSeconds = htmlElement.DurationSeconds;
                    result.StartSecondsFromEnd = htmlElement.StartSecondsFromEnd;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(
                        ex,
                        "Failed to parse overlay {Path} for form editing; falling back to raw yaml editing",
                        element.Path);

                    result.IsParsed = false;
                }
            }

            return result;
        }

        return Option<OverlayEditViewModel>.None;
    }
}
