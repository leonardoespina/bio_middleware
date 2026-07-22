using Microsoft.Extensions.Hosting;
using DPUruNet;
using bio_middleware.Services;

namespace bio_middleware.Services;

public class BioDiscoveryService : BackgroundService
{
    private readonly ILogger<BioDiscoveryService> _logger;

    public BioDiscoveryService(ILogger<BioDiscoveryService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[Discovery] Iniciando servicio de escaneo de hardware...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Llamamos al método de descubrimiento en BioService
                BioService.DiscoverReaders();
            }
            catch (Exception ex)
            {
                _logger.LogError($"[Discovery] Error en escaneo USB: {ex.Message}");
            }

            // Escaneamos cada 5 segundos
            await Task.Delay(5000, stoppingToken);
        }
    }
}
