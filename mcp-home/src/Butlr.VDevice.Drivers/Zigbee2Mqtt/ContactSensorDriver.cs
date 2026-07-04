using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

// Capteur de contact porte/fenêtre — read-only (BooleanState cluster)
public sealed class ContactSensorDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<ContactSensorDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    public ContactSensorDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<ContactSensorDriver> logger)
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
        _logger.LogWarning("ContactSensorDriver : tentative de commande ignorée sur {DeviceId}", deviceId);
        return Task.CompletedTask;
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            // Z2M : contact=true → contact fermé (porte fermée)
            var contact = node?["contact"]?.GetValue<bool>();
            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: contact,
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse contact sensor Z2M échoué");
        }

        return Task.CompletedTask;
    }
}
