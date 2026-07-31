namespace ErsatzTV.Core.Domain;

public class ChannelGraphicsElement
{
    public int ChannelId { get; set; }
    public Channel Channel { get; set; }
    public int GraphicsElementId { get; set; }
    public GraphicsElement GraphicsElement { get; set; }
}
