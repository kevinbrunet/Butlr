using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

// Capteur de présence — read-only, pas de commande possible
public sealed class OccupancySensorDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<OccupancySensorDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    public OccupancySensorDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<OccupancySensorDriver> logger)
    {
        _mqtt = mqtt;
        _deviceTopic = $"zigbee2mqtt/{friendlyName}";
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _mqtt.SubscribeAsync(_deviceTopic, ct);
        _mqtt.OnMessage(HandleStateAsync);
    }

    public Task ApplyCommandAsync(string deviceId, ArbitrationResult? command, CancellationToken ct = default)
    {
        // Capteur read-only — aucune commande possible
        _logger.LogWarning("OccupancySensorDriver : tentative de commande ignorée sur {DeviceId}", deviceId);
        return Task.CompletedTask;
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            var occupancy = node?["occupancy"]?.GetValue<bool>();
            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: occupancy,
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse occupancy Z2M échoué");
        }

        return Task.CompletedTask;
    }
}
