namespace Butlr.VDevice.Config.Models;

public sealed class TierConfig
{
    public string Id { get; set; } = string.Empty;
    public int Rank { get; set; }
    public string ArbiterRef { get; set; } = string.Empty;
    public Dictionary<string, string> ArbiterConfig { get; set; } = [];
    public AdmissionConfig Admission { get; set; } = new();
    public DurationPolicyConfig DurationPolicy { get; set; } = new();
    public bool BypassInertia { get; set; }
}

public sealed class AdmissionConfig
{
    public List<string> TagsRequired { get; set; } = [];
    public List<string> TagsForbidden { get; set; } = [];
}

public sealed class DurationPolicyConfig
{
    public bool PersistentAllowed { get; set; } = true;
    public bool TtlRequired { get; set; }
    public int? TtlMaxMs { get; set; }
}
