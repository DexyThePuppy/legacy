namespace ErsatzTV.Application.Graphics;

public record GetOverlayById(int Id) : IRequest<Option<OverlayEditViewModel>>;
