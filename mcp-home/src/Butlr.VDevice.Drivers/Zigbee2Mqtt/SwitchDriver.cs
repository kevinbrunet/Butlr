using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

// Driver OnOff pour les prises/interrupteurs — distinct de LightDriver (pas de LevelControl ni ColorControl)
public sealed class SwitchDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<SwitchDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    public SwitchDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<SwitchDriver> logger)
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

    public async Task ApplyCommandAsync(string deviceId, ArbitrationResult? command, CancellationToken ct = default)
    {
        if (command is null) return;

        var on = Convert.ToBoolean(command.Value);
        var node = new JsonObject { ["state"] = on ? "ON" : "OFF" };

        await _mqtt.PublishAsync($"{_deviceTopic}/set", node.ToJsonString(), ct: ct);
        _logger.LogInformation("SwitchDriver état {State} vers {Topic}", on ? "ON" : "OFF", _deviceTopic);
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            var stateStr = node?["state"]?.GetValue<string>();
            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: stateStr?.Equals("ON", StringComparison.OrdinalIgnoreCase),
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse état switch Z2M échoué");
        }

        return Task.CompletedTask;
    }
}
