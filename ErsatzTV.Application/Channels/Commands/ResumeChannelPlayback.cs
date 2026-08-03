using ErsatzTV.Core;

namespace ErsatzTV.Application.Channels;

public record ResumeChannelPlayback(int ChannelId) : IRequest<Either<BaseError, Unit>>;
