namespace Butlr.VDevice.Core.Arbiters;

public interface IArbiter
{
    // Renvoie la valeur gagnante parmi les VDevices admis, ou null si aucune décision.
    // RealState est l'état physique connu du device, peut être null.
    object? Arbitrate(IReadOnlyCollection<VDevice> admitted, object? realState);

    // Identifiant du VDevice gagnant (pour la traçabilité)
    VDeviceId? WinningId(IReadOnlyCollection<VDevice> admitted, object? realState);
}
