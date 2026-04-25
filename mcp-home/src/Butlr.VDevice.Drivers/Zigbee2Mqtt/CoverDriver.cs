using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

public sealed class CoverDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<CoverDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    public CoverDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<CoverDriver> logger)
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

        // Position [0, 100] — Z2M utilise position (0=fermé, 100=ouvert)
        var position = Convert.ToInt32(command.Value);
        var node = new JsonObject { ["position"] = position };

        await _mqtt.PublishAsync($"{_deviceTopic}/set", node.ToJsonString(), ct: ct);
        _logger.LogInformation("CoverDriver position {Position}% vers {Topic}", position, _deviceTopic);
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            var position = node?["position"]?.GetValue<int>();
            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: position,
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse état volet Z2M échoué");
        }

        return Task.CompletedTask;
    }
}
