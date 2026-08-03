using ErsatzTV.Core;

namespace ErsatzTV.Application.Channels;

/// <summary>
///     Deletes a channel station graph: channel (and playouts), optionally its classic schedule
///     and bound smart collection when nothing else references them.
/// </summary>
public record DeleteChannelStation(
    int ChannelId,
    bool DeleteSchedule = true,
    bool DeleteSmartCollection = true) : IRequest<Either<BaseError, Unit>>;
