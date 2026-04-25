namespace Butlr.VDevice.Core.Arbiters;

// Priorité utilisateur (champ Priority) d'abord, timestamp serveur (CreatedAt) ensuite
// Prévu pour le niveau user-override : plusieurs agents-utilisateur concurrents
public sealed class UserPriorityThenTimestampArbiter : IArbiter
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
