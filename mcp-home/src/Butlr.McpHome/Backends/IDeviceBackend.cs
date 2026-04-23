namespace Butlr.McpHome.Backends;

/// <summary>
/// Abstraction au-dessus d'un backend qui pilote des devices (lumières au POC).
/// Impls successives prévues : console mock → MQTT → Home Assistant.
/// Cf. docs/architecture.md §7.2.
/// </summary>
public interface IDeviceBackend
{
    Task TurnOnLightAsync(string room, CancellationToken cancellationToken = default);

    Task TurnOffLightAsync(string room, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot immuable de l'état des lumières (pièce → allumée ?).
    /// Sert la future web UI §7.4 et les tests.
    /// </summary>
    IReadOnlyDictionary<string, bool> GetLightStates();
}
