namespace ErsatzTV.Application.Playouts;

/// <summary>
///     Queues a Reset build for every playout whose last build failed (e.g. empty smart collection
///     while media was still indexing).
/// </summary>
public record RebuildFailedPlayouts : IRequest;
