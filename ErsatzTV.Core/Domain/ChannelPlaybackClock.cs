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
        return now - GetTimelineOffset(channel);
    }

    /// <summary>
    ///     Combined seek/pause/playout offset applied by <see cref="GetEffectiveNow"/>.
    ///     HLS process models must add this back onto <c>Until</c> so session buffering stays on wall clock.
    /// </summary>
    public static TimeSpan GetTimelineOffset(Channel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.PlaybackControlOffset + (channel.PlayoutOffset ?? TimeSpan.Zero);
    }

    public static Option<TimeSpan> GetProcessModelOffset(Channel channel)
    {
        TimeSpan offset = GetTimelineOffset(channel);
        return offset == TimeSpan.Zero ? Option<TimeSpan>.None : Some(offset);
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
