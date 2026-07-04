using System.Text.Json.Nodes;
using System.Reactive.Subjects;
using Butlr.VDevice.Core;
using Butlr.VDevice.Orchestrator;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

public sealed class ThermostatDriver : IDriver
{
    private readonly MqttBrokerClient _mqtt;
    private readonly string _deviceTopic;
    private readonly ILogger<ThermostatDriver> _logger;
    private readonly Subject<DeviceState> _states = new();

    public ThermostatDriver(MqttBrokerClient mqtt, string friendlyName, ILogger<ThermostatDriver> logger)
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

        // Valeur en centidegrés (ex. 2100 = 21.00°C) → Z2M attend des degrés décimaux
        var setpoint = Convert.ToDouble(command.Value) / 100.0;
        var node = new JsonObject { ["occupied_heating_setpoint"] = setpoint };

        await _mqtt.PublishAsync($"{_deviceTopic}/set", node.ToJsonString(), ct: ct);
        _logger.LogInformation("ThermostatDriver commande {Setpoint}°C vers {Topic}", setpoint, _deviceTopic);
    }

    public IObservable<DeviceState> ObserveState() => _states;

    private Task HandleStateAsync(string topic, string payload)
    {
        if (!topic.Equals(_deviceTopic, StringComparison.OrdinalIgnoreCase)) return Task.CompletedTask;

        try
        {
            var node = JsonNode.Parse(payload);
            var temp = node?["local_temperature"]?.GetValue<double>();
            _states.OnNext(new DeviceState(
                DeviceId: Path.GetFileName(_deviceTopic),
                RealState: temp.HasValue ? (short)(temp.Value * 100) : null,
                HealthStatus: "online",
                UpdatedAt: DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Parse état thermostat Z2M échoué");
        }

        return Task.CompletedTask;
    }
}
