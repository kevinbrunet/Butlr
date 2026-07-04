namespace Butlr.VDevice.Config.Models;

public sealed class HomeConfig
{
    public string Name { get; set; } = string.Empty;
    public List<TierConfig> Tiers { get; set; } = [];
}
