namespace Butlr.VDevice.Config.Models;

public sealed class AppConfig
{
    public string AppId { get; set; } = string.Empty;
    public string? FriendlyName { get; set; }
    public string? Description { get; set; }
}
