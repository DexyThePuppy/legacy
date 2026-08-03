namespace ErsatzTV.Core.Domain;

public static class ChannelPlaybackClock
{
    public static DateTimeOffset GetEffectiveNow(Channel channel, DateTimeOffset? wallClock = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.PlaybackPausedAt.HasValue)
        {
            return channel.PlaybackPausedAt.Value;
        }

        DateTimeOffset now = wallClock ?? DateTimeOffset.Now;
        return now - channel.PlaybackControlOffset - (channel.PlayoutOffset ?? TimeSpan.Zero);
    }

    public static void PauseAt(Channel channel, DateTimeOffset effectiveNow)
    {
        ArgumentNullException.ThrowIfNull(channel);
        channel.PlaybackPausedAt = effectiveNow;
    }

    public static void Resume(Channel channel, DateTimeOffset? wallClock = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.PlaybackPausedAt is not { } pausedAt)
        {
            return;
        }

        DateTimeOffset now = wallClock ?? DateTimeOffset.Now;
        channel.PlaybackControlOffset = now - pausedAt - (channel.PlayoutOffset ?? TimeSpan.Zero);
        channel.PlaybackPausedAt = null;
    }

    public static void SeekTo(Channel channel, DateTimeOffset target, DateTimeOffset? wallClock = null)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.PlaybackPausedAt.HasValue)
        {
            channel.PlaybackPausedAt = target;
            return;
        }

        DateTimeOffset now = wallClock ?? DateTimeOffset.Now;
        channel.PlaybackControlOffset = now - target - (channel.PlayoutOffset ?? TimeSpan.Zero);
    }
}
