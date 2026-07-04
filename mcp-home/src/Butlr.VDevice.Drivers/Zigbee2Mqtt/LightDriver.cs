using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

public sealed class LightDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<LightDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    // ~ délai minimum entre deux commandes pour l'inertie (cf. ADR 0004, 0011)
    private const int InertiaMs = 100;
    private DateTimeOffset _lastCommandAt = DateTimeOffset.MinValue;

    public LightDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<LightDriver> logger)
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

        // Bypass inertie si le niveau gagnant l'exige (ex. safety)
        if (!command.BypassInertia)
        {
            var elapsed = DateTimeOffset.UtcNow - _lastCommandAt;
            if (elapsed.TotalMilliseconds < InertiaMs)
                await Task.Delay((int)(InertiaMs - elapsed.TotalMilliseconds), ct);
        }

        var payload = BuildPayload(command);
        if (payload is null) return;

        await _mqtt.PublishAsync($"{_deviceTopic}/set", payload, ct: ct);
        _lastCommandAt = DateTimeOffset.UtcNow;
        _logger.LogInformation("LightDriver commande envoyée à {Topic}", _deviceTopic);
    }

    private string? BuildPayload(ArbitrationResult command)
    {
        var node = new JsonObject();

        if (command.WinningTierId is not null)
        {
            // OnOff
            if (command.Value is bool onOff)
            {
                node["state"] = onOff ? "ON" : "OFF";
                return node.ToJsonString();
            }
            // LevelControl — Z2M utilise brightness [0-254]
            if (command.Value is byte level)
            {
                node["brightness"] = (int)level;
                node["state"] = level > 0 ? "ON" : "OFF";
                return node.ToJsonString();
            }
        }

        _logger.LogWarning("LightDriver : type de valeur inconnu {Type}", command.Value.GetType().Name);
        return null;
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            var stateStr = node?["state"]?.GetValue<string>();
            object? realState = stateStr?.Equals("ON", StringComparison.OrdinalIgnoreCase) == true;

            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: realState,
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse état Z2M échoué pour {Topic}", topic);
        }

        return Task.CompletedTask;
    }
}
