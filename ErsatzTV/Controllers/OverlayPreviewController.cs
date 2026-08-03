using ErsatzTV.Application.Graphics;
using ErsatzTV.Core;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace ErsatzTV.Controllers;

[ApiController]
[ApiExplorerSettings(IgnoreApi = true)]
public class OverlayPreviewController(IMediator mediator) : ControllerBase
{
    [HttpGet("overlays/{id:int}/raw.html")]
    [Produces("text/html")]
    public async Task<IActionResult> GetRawHtml(
        int id,
        [FromQuery] string title = null,
        [FromQuery] string nextTitle = null,
        CancellationToken cancellationToken = default)
    {
        string requestBase = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');

        Either<BaseError, string> result = await mediator.Send(
            new GetOverlayPreviewHtml(id, requestBase, title, nextTitle),
            cancellationToken);

        foreach (BaseError error in result.LeftToSeq())
        {
            return NotFound(error.Value);
        }

        foreach (string html in result.RightToSeq())
        {
            return Content(html, "text/html; charset=utf-8");
        }

        return NotFound();
    }
}
