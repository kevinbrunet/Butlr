namespace Butlr.VDevice.Config.Models;

public sealed class DeviceConfig
{
    public string? FriendlyName { get; set; }
    public string? ExternalId { get; set; }  // topic MQTT Z2M
    public List<string> ClustersSupported { get; set; } = [];
    public FallbackConfig? Fallback { get; set; }
    public List<TierConfig> TierOverrides { get; set; } = [];
}

public sealed class FallbackConfig
{
    public bool Enabled { get; set; }
    public object? Value { get; set; }
}
