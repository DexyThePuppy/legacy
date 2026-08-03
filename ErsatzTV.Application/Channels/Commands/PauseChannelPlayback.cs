using ErsatzTV.Core;

namespace ErsatzTV.Application.Channels;

public record PauseChannelPlayback(int ChannelId) : IRequest<Either<BaseError, Unit>>;
