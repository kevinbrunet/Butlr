namespace Butlr.VDevice.Config.Models;

public sealed class PermissionConfig
{
    public string AppId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string TierMax { get; set; } = string.Empty;
    public int PriorityMax { get; set; }
    public List<string> ClustersAllowed { get; set; } = [];
    public string Status { get; set; } = "pending";  // pending | granted | revoked
    public DateTimeOffset? GrantedAt { get; set; }
    public string? GrantedBy { get; set; }
}
