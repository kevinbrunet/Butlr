namespace Butlr.VDevice.Core.Arbiters;

// Plus haute priorité gagne ; égalité résolue par timestamp serveur (le plus ancien)
public sealed class StrictPriorityArbiter : IArbiter
{
    private VDevice? Winner(IReadOnlyCollection<VDevice> admitted)
    {
        if (admitted.Count == 0) return null;
        return admitted
            .OrderByDescending(v => v.Priority)
            .ThenBy(v => v.CreatedAt)
            .First();
    }

    public object? Arbitrate(IReadOnlyCollection<VDevice> admitted, object? realState)
        => Winner(admitted)?.Value;

    public VDeviceId? WinningId(IReadOnlyCollection<VDevice> admitted, object? realState)
        => Winner(admitted)?.Id;
}
