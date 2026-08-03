using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public record ChannelPreviewSource(string Path, TimeSpan Seek, bool IsLive, bool IsPaused);

public record GetChannelPreviewSource(string ChannelNumber)
    : IRequest<Either<BaseError, ChannelPreviewSource>>;
