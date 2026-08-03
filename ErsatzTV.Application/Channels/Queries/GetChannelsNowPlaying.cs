namespace ErsatzTV.Application.Channels;

public record ChannelNowPlayingViewModel(
    int ChannelId,
    string Title,
    string ThumbnailUrl,
    DateTimeOffset Start,
    DateTimeOffset Finish,
    bool IsPaused,
    DateTimeOffset EffectiveNow,
    /// <summary>Wall clock minus effective now at fetch time; used to animate the scrubber while playing.</summary>
    TimeSpan ClockSkew);

public record GetChannelsNowPlaying(IReadOnlyList<int> ChannelIds)
    : IRequest<Dictionary<int, ChannelNowPlayingViewModel>>;
