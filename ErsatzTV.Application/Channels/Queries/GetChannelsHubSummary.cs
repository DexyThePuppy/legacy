namespace ErsatzTV.Application.Channels;

public record ChannelHubSummaryViewModel(
    int Id,
    string Number,
    string Name,
    string Group,
    bool IsEnabled,
    bool ShowInEpg,
    bool HasPlayout,
    bool IsLive,
    int? PlayoutId,
    bool? LastBuildSuccess,
    string LastBuildMessage,
    bool SegmenterActive);

public record GetChannelsHubSummary(bool IncludeDisabled = false)
    : IRequest<List<ChannelHubSummaryViewModel>>;
