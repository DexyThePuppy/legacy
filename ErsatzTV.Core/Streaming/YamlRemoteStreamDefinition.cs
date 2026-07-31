using YamlDotNet.Serialization;

namespace ErsatzTV.Core.Streaming;

public class YamlRemoteStreamDefinition
{
    [YamlMember(Alias = "url", ApplyNamingConventions = false)]
    public string Url { get; set; }

    [YamlMember(Alias = "script", ApplyNamingConventions = false)]
    public string Script { get; set; }

    [YamlMember(Alias = "duration", ApplyNamingConventions = false)]
    public string Duration { get; set; }

    [YamlMember(Alias = "fallback_query", ApplyNamingConventions = false)]
    public string FallbackQuery { get; set; }

    [YamlMember(Alias = "is_live", ApplyNamingConventions = false)]
    public bool? IsLive { get; set; }

    [YamlMember(Alias = "title", ApplyNamingConventions = false)]
    public string Title { get; set; }

    [YamlMember(Alias = "plot", ApplyNamingConventions = false)]
    public string Plot { get; set; }

    [YamlMember(Alias = "year", ApplyNamingConventions = false)]
    public int? Year { get; set; }

    [YamlMember(Alias = "content_rating", ApplyNamingConventions = false)]
    public string ContentRating { get; set; }

    // when set (e.g. "yt-dlp"), playback will prefer a locally downloaded copy
    // and fall back to live streaming through the downloader
    [YamlMember(Alias = "downloader", ApplyNamingConventions = false)]
    public string Downloader { get; set; }

    [YamlMember(Alias = "release_date", ApplyNamingConventions = false)]
    public string ReleaseDate { get; set; }

    [YamlMember(Alias = "tags", ApplyNamingConventions = false)]
    public List<string> Tags { get; set; }

    [YamlMember(Alias = "thumbnail", ApplyNamingConventions = false)]
    public string Thumbnail { get; set; }
}
