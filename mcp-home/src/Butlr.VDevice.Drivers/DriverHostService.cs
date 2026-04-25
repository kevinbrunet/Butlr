using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Butlr.VDevice.Drivers;

public sealed class DriverHostService : BackgroundService
{
    private readonly MqttBrokerClient _mqtt;
    private readonly ILogger<DriverHostService> _logger;

    public DriverHostService(MqttBrokerClient mqtt, ILogger<DriverHostService> logger)
    {
        _mqtt = mqtt;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int delay = 1_000;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _mqtt.ConnectAsync(stoppingToken);
                _logger.LogInformation("DriverHostService connecté au broker MQTT");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Connexion MQTT échouée, retry dans {Delay}ms", delay);
                await Task.Delay(delay, stoppingToken);
                delay = Math.Min(delay * 2, 30_000);
            }
        }
    }
}
