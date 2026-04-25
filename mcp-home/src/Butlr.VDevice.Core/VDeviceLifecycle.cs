namespace Butlr.VDevice.Core;

public static class VDeviceLifecycle
{
    // ~ valeurs par défaut, paramétrables en Phase 3+ par niveau (cf. ADR 0008 patché par 0014)
    public const int HeartbeatIntervalMs = 30_000;
    public const int GraceMs = 5_000;

    public static bool IsExpired(VDevice vdevice, DateTimeOffset now) => vdevice.Duration switch
    {
        VDeviceDuration.Persistent => false,
        VDeviceDuration.Ttl ttl => now > vdevice.LastRenewAt.AddMilliseconds(ttl.Ms),
        _ => false,
    };

    public static bool CanRenew(VDevice vdevice, DateTimeOffset now)
    {
        var deadline = vdevice.LastRenewAt.AddMilliseconds(HeartbeatIntervalMs + GraceMs);
        return now <= deadline;
    }

    public static VDevice Renew(VDevice vdevice, DateTimeOffset now)
    {
        if (!CanRenew(vdevice, now))
            throw new InvalidOperationException(
                $"VDevice {vdevice.Id} a expiré — renew hors fenêtre [{vdevice.LastRenewAt} + {HeartbeatIntervalMs + GraceMs}ms]");
        return vdevice with { LastRenewAt = now };
    }

    public static IReadOnlyCollection<VDeviceId> Tick(
        IEnumerable<VDevice> vdevices,
        DateTimeOffset now)
        => vdevices
            .Where(v => v.Duration is VDeviceDuration.Persistent
                        ? now > v.LastRenewAt.AddMilliseconds(HeartbeatIntervalMs + GraceMs)
                        : IsExpired(v, now))
            .Select(v => v.Id)
            .ToList();
}
