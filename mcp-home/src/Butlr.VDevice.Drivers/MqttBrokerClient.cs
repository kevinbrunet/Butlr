using System.Text;
using MQTTnet;
using MQTTnet.Client;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers;

public sealed class MqttBrokerClient : IDisposable
{
    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly ILogger<MqttBrokerClient> _logger;
    private readonly List<Func<string, string, Task>> _handlers = [];

    public MqttBrokerClient(MqttConfig config, ILogger<MqttBrokerClient> logger)
    {
        _logger = logger;
        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();

        _options = new MqttClientOptionsBuilder()
            .WithTcpServer(config.Host, config.Port)
            .WithClientId($"butlr-mcp-home-{Guid.NewGuid():N}")
            .WithCleanSession()
            .Build();

        _client.ApplicationMessageReceivedAsync += OnMessageAsync;
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(_options, ct);
        _logger.LogInformation("MQTT connecté");
    }

    public async Task SubscribeAsync(string topic, CancellationToken ct = default)
    {
        var sub = new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(topic)
            .Build();
        await _client.SubscribeAsync(sub, ct);
    }

    public async Task PublishAsync(string topic, string payload, bool retain = false, CancellationToken ct = default)
    {
        var msg = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .Build();
        await _client.PublishAsync(msg, ct);
    }

    public void OnMessage(Func<string, string, Task> handler) => _handlers.Add(handler);

    private async Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;
        var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment);
        foreach (var handler in _handlers)
            await handler(topic, payload);
    }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        _logger.LogWarning("MQTT déconnecté, reconnexion dans 5s...");
        await Task.Delay(5_000);
        try { await _client.ConnectAsync(_options); }
        catch (Exception ex) { _logger.LogError(ex, "Reconnexion MQTT échouée"); }
    }

    public void Dispose() => _client.Dispose();
}

public sealed record MqttConfig(string Host, int Port);
