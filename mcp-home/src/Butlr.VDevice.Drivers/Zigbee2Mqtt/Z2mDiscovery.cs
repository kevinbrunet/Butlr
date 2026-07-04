using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers.Zigbee2Mqtt;

public sealed class Z2mDiscovery
{
    private readonly MqttBrokerClient _mqtt;
    private readonly ILogger<Z2mDiscovery> _logger;
    private readonly Dictionary<string, Z2mDevice> _devices = [];

    public Z2mDiscovery(MqttBrokerClient mqtt, ILogger<Z2mDiscovery> logger)
    {
        _mqtt = mqtt;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _mqtt.SubscribeAsync("zigbee2mqtt/bridge/devices", ct);
        _mqtt.OnMessage(HandleMessageAsync);
    }

    private Task HandleMessageAsync(string topic, string payload)
    {
        if (!topic.Equals("zigbee2mqtt/bridge/devices", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        try
        {
            var devices = JsonSerializer.Deserialize<Z2mDeviceAnnounce[]>(payload,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (devices is null) return Task.CompletedTask;

            foreach (var d in devices)
            {
                _devices[d.FriendlyName] = new Z2mDevice(d.FriendlyName, d.Type, d.IeeeAddress);
                _logger.LogInformation("Z2M device découvert : {Name} ({Type})", d.FriendlyName, d.Type);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Parse Z2M bridge/devices échoué");
        }

        return Task.CompletedTask;
    }

    public IReadOnlyDictionary<string, Z2mDevice> Devices => _devices;
}

public sealed record Z2mDevice(string FriendlyName, string Type, string IeeeAddress);

public sealed record Z2mDeviceAnnounce(string FriendlyName, string Type, string IeeeAddress);
