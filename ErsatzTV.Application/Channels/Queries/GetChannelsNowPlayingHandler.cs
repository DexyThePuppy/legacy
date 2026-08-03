using Flurl;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Emby;
using ErsatzTV.Core.Jellyfin;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;
using static ErsatzTV.Application.Playouts.Mapper;

namespace ErsatzTV.Application.Channels;

public class GetChannelsNowPlayingHandler(IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<GetChannelsNowPlaying, Dictionary<int, ChannelNowPlayingViewModel>>
{
    public async Task<Dictionary<int, ChannelNowPlayingViewModel>> Handle(
        GetChannelsNowPlaying request,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, ChannelNowPlayingViewModel>();
        if (request.ChannelIds is null || request.ChannelIds.Count == 0)
        {
            return result;
        }

        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        List<Channel> channels = await dbContext.Channels
            .AsNoTracking()
            .Where(c => request.ChannelIds.Contains(c.Id))
            .ToListAsync(cancellationToken);

        foreach (Channel channel in channels)
        {
            if (!channel.IsEnabled)
            {
                continue;
            }

            DateTimeOffset wallNow = DateTimeOffset.Now;
            DateTimeOffset now = ChannelPlaybackClock.GetEffectiveNow(channel, wallNow);
            TimeSpan clockSkew = channel.PlaybackPausedAt.HasValue ? TimeSpan.Zero : wallNow - now;
            int channelId = channel.MirrorSourceChannelId ?? channel.Id;

            Option<PlayoutItem> maybeItem = await dbContext.PlayoutItems
                .AsNoTracking()
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Movie).MovieMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as MusicVideo).MusicVideoMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as MusicVideo).Artist)
                .ThenInclude(a => a.ArtistMetadata)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Episode).EpisodeMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Episode).Season)
                .ThenInclude(s => s.Show)
                .ThenInclude(s => s.ShowMetadata)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as OtherVideo).OtherVideoMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Song).SongMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as Image).ImageMetadata)
                .ThenInclude(m => m.Artwork)
                .Include(i => i.MediaItem)
                .ThenInclude(mi => (mi as RemoteStream).RemoteStreamMetadata)
                .ThenInclude(m => m.Artwork)
                .ForChannelAndTime(channelId, now);

            foreach (PlayoutItem item in maybeItem)
            {
                string title = !string.IsNullOrWhiteSpace(item.CustomTitle)
                    ? item.CustomTitle
                    : GetDisplayTitle(item.MediaItem, item.ChapterTitle);

                if (string.IsNullOrWhiteSpace(title))
                {
                    continue;
                }

                result[channel.Id] = new ChannelNowPlayingViewModel(
                    channel.Id,
                    title,
                    ResolveThumbnailUrl(item.MediaItem),
                    item.StartOffset,
                    item.FinishOffset,
                    channel.PlaybackPausedAt.HasValue,
                    now,
                    clockSkew);
            }
        }

        return result;
    }

    private static string ResolveThumbnailUrl(MediaItem mediaItem)
    {
        Option<Metadata> maybeMetadata = mediaItem switch
        {
            Movie m => m.MovieMetadata.HeadOrNone().Map(x => (Metadata)x),
            MusicVideo mv => mv.MusicVideoMetadata.HeadOrNone().Map(x => (Metadata)x),
            Episode e => e.EpisodeMetadata.HeadOrNone().Map(x => (Metadata)x),
            OtherVideo ov => ov.OtherVideoMetadata.HeadOrNone().Map(x => (Metadata)x),
            Song s => s.SongMetadata.HeadOrNone().Map(x => (Metadata)x),
            Image i => i.ImageMetadata.HeadOrNone().Map(x => (Metadata)x),
            RemoteStream rs => rs.RemoteStreamMetadata.HeadOrNone().Map(x => (Metadata)x),
            _ => Option<Metadata>.None
        };

        foreach (Metadata metadata in maybeMetadata)
        {
            Artwork artwork = (metadata.Artwork ?? [])
                .Where(a => a.ArtworkKind is ArtworkKind.Thumbnail or ArtworkKind.Poster)
                .OrderBy(a => a.ArtworkKind == ArtworkKind.Thumbnail ? 0 : 1)
                .FirstOrDefault();

            if (artwork is not null)
            {
                return ToUiArtworkUrl(artwork);
            }
        }

        return string.Empty;
    }

    private static string ToUiArtworkUrl(Artwork artwork)
    {
        string path = artwork.Path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (path.StartsWith("jellyfin://", StringComparison.OrdinalIgnoreCase))
        {
            return $"artwork/thumbnails/{JellyfinUrl.RelativeProxyForArtwork(path).SetQueryParam("fillHeight", 220)}";
        }

        if (path.StartsWith("emby://", StringComparison.OrdinalIgnoreCase))
        {
            return $"artwork/thumbnails/{EmbyUrl.RelativeProxyForArtwork(path).SetQueryParam("maxHeight", 220)}";
        }

        string folder = artwork.ArtworkKind == ArtworkKind.Poster ? "posters" : "thumbnails";
        return $"artwork/{folder}/{path}";
    }
}
