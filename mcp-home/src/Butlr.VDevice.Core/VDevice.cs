using Butlr.VDevice.Core.Capabilities;

namespace Butlr.VDevice.Core;

public sealed record VDevice
{
    public required VDeviceId Id { get; init; }
    public required string DeviceId { get; init; }
    public required string TierId { get; init; }
    public required IReadOnlySet<string> Tags { get; init; }
    public required int Priority { get; init; }
    public required ClusterId Cluster { get; init; }
    public required AttributeId Attribute { get; init; }
    public required object Value { get; init; }
    public required VDeviceDuration Duration { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastRenewAt { get; init; }

    // Champs optionnels selon actor_kind
    public string? AppId { get; init; }
    public string? ActorUserId { get; init; }
    public string? ViaAgentId { get; init; }
    public string ActorKind { get; init; } = "app";

    // Tags dérivés de actor_kind
    public static IReadOnlySet<string> TagsFor(string actorKind) => actorKind switch
    {
        "app" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "app" },
        "user_agent" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "user_agent" },
        "system" => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "system" },
        _ => throw new ArgumentException($"actor_kind inconnu : {actorKind}")
    };

    public static VDevice Create(
        string deviceId,
        string actorKind,
        string tierId,
        int priority,
        ClusterId cluster,
        AttributeId attribute,
        object value,
        VDeviceDuration duration,
        TierRegistry registry,
        DateTimeOffset now,
        string? appId = null,
        string? actorUserId = null,
        string? viaAgentId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(actorKind);
        ArgumentException.ThrowIfNullOrWhiteSpace(tierId);

        if (priority is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(priority), "Priority doit être dans [0, 100]");

        var tier = registry.Get(tierId);
        var tags = TagsFor(actorKind);

        if (!tier.Admission.Matches(tags))
            throw new ArgumentException(
                $"Tags {string.Join(",", tags)} ne satisfont pas les conditions d'admission du niveau '{tierId}'");

        if (!tier.DurationPolicy.PersistentAllowed && duration is VDeviceDuration.Persistent)
            throw new ArgumentException($"Le niveau '{tierId}' n'autorise pas les VDevices persistants");

        if (tier.DurationPolicy.TtlRequired && duration is not VDeviceDuration.Ttl)
            throw new ArgumentException($"Le niveau '{tierId}' exige un TTL explicite");

        if (tier.DurationPolicy.TtlMaxMs is { } maxMs && duration is VDeviceDuration.Ttl ttl && ttl.Ms > maxMs)
            throw new ArgumentException($"TTL {ttl.Ms}ms dépasse le maximum autorisé {maxMs}ms pour le niveau '{tierId}'");

        return new VDevice
        {
            Id = VDeviceId.New(),
            DeviceId = deviceId,
            TierId = tierId,
            Tags = tags,
            Priority = priority,
            Cluster = cluster,
            Attribute = attribute,
            Value = value,
            Duration = duration,
            CreatedAt = now,
            LastRenewAt = now,
            ActorKind = actorKind,
            AppId = appId,
            ActorUserId = actorUserId,
            ViaAgentId = viaAgentId,
        };
    }

    // Résolution automatique du tier_id par admission de tags
    public static string ResolveTierId(string actorKind, TierRegistry registry)
    {
        var tags = TagsFor(actorKind);
        // Niveau de rank le plus élevé dont les tags d'admission sont satisfaits
        var resolved = registry.Ordered
            .Where(t => t.Admission.Matches(tags))
            .MaxBy(t => t.Rank);

        return resolved?.Id ?? throw new InvalidOperationException(
            $"Aucun niveau n'accepte actor_kind='{actorKind}'");
    }
}
