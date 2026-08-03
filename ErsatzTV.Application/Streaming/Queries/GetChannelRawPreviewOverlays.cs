using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public record GetChannelRawPreviewOverlays(string ChannelNumber, string RequestBase)
    : IRequest<Either<BaseError, string>>;
