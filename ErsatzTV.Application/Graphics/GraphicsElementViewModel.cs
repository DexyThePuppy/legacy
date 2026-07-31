using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Graphics;

public record GraphicsElementViewModel(int Id, string Name, string FileName, GraphicsElementKind Kind);
