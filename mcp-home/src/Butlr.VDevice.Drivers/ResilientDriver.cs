using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers;

// Wrapper qui ajoute retry + passage en état degraded sur échec définitif
public sealed class ResilientDriver : IDriver
{
    private readonly IDriver _inner;
    private readonly ILogger<ResilientDriver> _logger;

    // ~ délais de retry ADR 0011
    private static readonly int[] RetryDelaysMs = [1_000, 2_000, 5_000];

    public ResilientDriver(IDriver inner, ILogger<ResilientDriver> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task ApplyCommandAsync(string deviceId, ArbitrationResult? command, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt <= RetryDelaysMs.Length; attempt++)
        {
            try
            {
                await _inner.ApplyCommandAsync(deviceId, command, ct);
                return;
            }
            catch (Exception ex) when (attempt < RetryDelaysMs.Length)
            {
                _logger.LogWarning(ex, "Commande échouée pour {DeviceId}, retry {Attempt}/{Total}",
                    deviceId, attempt + 1, RetryDelaysMs.Length);
                await Task.Delay(RetryDelaysMs[attempt], ct);
            }
        }

        _logger.LogError("device.command_failed {DeviceId} — device passé en degraded", deviceId);
        // L'état degraded est publié via ObserveState() par le driver sous-jacent
    }

    public IObservable<DeviceState> ObserveState() => _inner.ObserveState();
}
