namespace ErsatzTV.Core.YouTube;

// persisted as .etv-import.json inside each import folder under the youtube library
public class YouTubeImportManifest
{
    public const string FileName = ".etv-import.json";

    public string Name { get; set; }
    public YouTubeImportKind Kind { get; set; }
    public string Url { get; set; }
    public string ChannelName { get; set; }
    public string IconUrl { get; set; }
    public string Slug { get; set; }
    public int LibraryId { get; set; }
    public bool AutoSync { get; set; }
    public int SyncIntervalHours { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? LastSyncUtc { get; set; }
    public int VideoCount { get; set; }

    // monotonically increasing ordinal used to derive pseudo release dates
    // for videos with unknown upload dates (preserves oldest-to-newest order)
    public int NextIndex { get; set; }
}
