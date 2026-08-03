using ErsatzTV.Core;
using Microsoft.Extensions.Logging;
using Scriban;
using Scriban.Runtime;

namespace ErsatzTV.Application.Graphics;

public class RenderOverlayPreviewHandler(ILogger<RenderOverlayPreviewHandler> logger)
    : IRequestHandler<RenderOverlayPreview, Either<BaseError, string>>
{
    public async Task<Either<BaseError, string>> Handle(
        RenderOverlayPreview request,
        CancellationToken cancellationToken)
    {
        try
        {
            string templateText = request.HtmlTemplate ?? string.Empty;

            var scriptObject = new ScriptObject();
            scriptObject.Import(request.Variables ?? new Dictionary<string, object>(), renamer: member => member.Name);

            var context = new TemplateContext { MemberRenamer = member => member.Name };
            context.PushGlobal(scriptObject);

            string rendered = await Template.Parse(templateText).RenderAsync(context);
            return rendered;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to render overlay preview template");
            return BaseError.New($"Template error: {ex.Message}");
        }
    }
}
