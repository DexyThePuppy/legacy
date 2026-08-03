using ErsatzTV.Core;

namespace ErsatzTV.Application.Channels;

public record SeekChannelPlayback(int ChannelId, TimeSpan PositionWithinItem) : IRequest<Either<BaseError, Unit>>;
