using ErsatzTV.Core;

namespace ErsatzTV.Application.Channels;

public record SkipChannelPlayback(int ChannelId, ChannelPlaybackSkipDirection Direction)
    : IRequest<Either<BaseError, Unit>>;
