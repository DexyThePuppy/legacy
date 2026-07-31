namespace ErsatzTV.Core.Metadata;

public static class EpgTemplateDataKey
{
    public static readonly string Epg = "Epg";

    // convenience variables for the next epg entry (upcoming video)
    public static readonly string NextTitle = "Next_Title";
    public static readonly string NextSubTitle = "Next_SubTitle";
    public static readonly string NextDescription = "Next_Description";
    public static readonly string NextStart = "Next_Start";
    public static readonly string NextStop = "Next_Stop";
    public static readonly string NextStartsInSeconds = "Next_StartsInSeconds";
}
