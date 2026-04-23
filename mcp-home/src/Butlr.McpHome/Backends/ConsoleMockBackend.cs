using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Butlr.McpHome.Backends;

/// <summary>
/// Backend POC : n'appelle aucun équipement physique.
/// Logue l'action via ILogger (console structurée) et tient un état en mémoire.
/// Thread-safe : Kestrel peut appeler plusieurs handlers en parallèle.
/// </summary>
public sealed class ConsoleMockBackend : IDeviceBackend
{
    private readonly ILogger<ConsoleMockBackend> _logger;
    private readonly ConcurrentDictionary<string, bool> _lights =
        new(StringComparer.OrdinalIgnoreCase);

    public ConsoleMockBackend(ILogger<ConsoleMockBackend> logger)
    {
        _logger = logger;
    }

    public Task TurnOnLightAsync(string room, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        _lights[room] = true;
        _logger.LogInformation("turn_on_light room={Room}", room);
        return Task.CompletedTask;
    }

    public Task TurnOffLightAsync(string room, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(room);
        _lights[room] = false;
        _logger.LogInformation("turn_off_light room={Room}", room);
        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, bool> GetLightStates()
    {
        // Copie pour éviter de fuiter le dict mutable et pour que le snapshot
        // ne bouge pas sous le nez de l'appelant.
        return new Dictionary<string, bool>(_lights, StringComparer.OrdinalIgnoreCase);
    }
}
