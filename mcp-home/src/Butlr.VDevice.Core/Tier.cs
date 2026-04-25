namespace Butlr.VDevice.Core;

public sealed record Tier(
    string Id,
    int Rank,
    string ArbiterRef,
    IReadOnlyDictionary<string, string> ArbiterConfig,
    Admission Admission,
    DurationPolicy DurationPolicy,
    bool BypassInertia)
{
    // Preset de 3 niveaux par défaut — peut être remplacé par config yaml
    public static IReadOnlyList<Tier> DefaultPreset =>
    [
        new Tier(
            Id: "safety",
            Rank: 1,
            ArbiterRef: "WinnerTakesAll",
            ArbiterConfig: new Dictionary<string, string>(),
            Admission: Admission.Any,
            DurationPolicy: new DurationPolicy(PersistentAllowed: true, TtlRequired: false, TtlMaxMs: null),
            BypassInertia: true),
        new Tier(
            Id: "user-override",
            Rank: 2,
            ArbiterRef: "UserPriorityThenTimestamp",
            ArbiterConfig: new Dictionary<string, string>(),
            Admission: new Admission(
                TagsRequired: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "user_agent" },
                TagsForbidden: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            DurationPolicy: new DurationPolicy(PersistentAllowed: false, TtlRequired: true, TtlMaxMs: null),
            BypassInertia: false),
        new Tier(
            Id: "apps",
            Rank: 3,
            ArbiterRef: "StrictPriority",
            ArbiterConfig: new Dictionary<string, string>(),
            Admission: new Admission(
                TagsRequired: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app" },
                TagsForbidden: new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            DurationPolicy: new DurationPolicy(PersistentAllowed: true, TtlRequired: false, TtlMaxMs: null),
            BypassInertia: false),
    ];
}
