using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HWChargeOptimizer.Zonneplan;

public class ZonneplanScheduleService(
    ILogger<ZonneplanScheduleService> logger,
    IZonneplanAuthentication zonneplanAuthentication,
    IOptionsMonitor<HWChargeOptimizerConfig> config,
    ConfigWriter configWriter,
    IZonneplanTariffReader zonneplanTariffReader) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Refreshing Zonneplan token...");
                
                var currentDateTime = DateTimeOffset.UtcNow;

                var response = await zonneplanAuthentication.RefreshTokenAsync(config.CurrentValue.Zonneplan.Authentication.RefreshToken);

                if (response == null)
                {
                    logger.LogWarning("No response from Zonneplan API when refreshing token.");
                    continue;
                } 
                
                config.CurrentValue.Zonneplan.Authentication.AccessToken = response.Value<string>("access_token") ?? throw new InvalidOperationException("Zonneplan access token is null. This is not expected.");
                config.CurrentValue.Zonneplan.Authentication.RefreshToken = response.Value<string>("refresh_token") ?? throw new InvalidOperationException("Zonneplan refresh token is null. This is not expected.");
                config.CurrentValue.Zonneplan.Authentication.ExpiresIn = response.Value<int>("expires_in");
                await configWriter.WriteAsync(config.CurrentValue);

                logger.LogInformation("Zonneplan token refreshed. Updating tariffs...");

                // retrieve latest tariffs
                var tariffs = await zonneplanTariffReader.GetElectricityTariffsAsync();
                if (tariffs == null || tariffs.Count == 0)
                {
                    logger.LogWarning("No tariffs found in Zonneplan Api response.");
                    continue;
                }

                logger.LogInformation("{count} tariffs retrieved.", tariffs.Count);
                tariffs.ForEach(t => logger.LogInformation("Tariff {from}: {price} ct.", t.From, t.Price / 100000));

                config.CurrentValue.Zonneplan.Tariffs?.Clear();

                var newConfigTariffs = tariffs.Select(tariff => new HWChargeOptimizer.Configuration.Tariff { Date = tariff.From, Price = tariff.Price / 100000 }).ToList();
                config.CurrentValue.Zonneplan.Tariffs = newConfigTariffs;

                // Update the last updated time after successfully retrieving tariffs
                config.CurrentValue.Zonneplan.Authentication.LastUpdated = currentDateTime;
                await configWriter.WriteAsync(config.CurrentValue);

                logger.LogInformation("Tariffs updated.");
            }
            catch (Exception ex)
            {
                logger.LogError("Error in ZonneplanScheduleService: {message}", ex.Message);
            }
            finally
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    logger.LogInformation("Zonneplan schedule service is stopping, because cancellation was requested.");
                }
                else
                {
                    logger.LogInformation("ZonneplanScheduleService waiting {interval} min for next run.", config.CurrentValue.Zonneplan.RefreshIntervalMinutes);
                    await Task.Delay(config.CurrentValue.Zonneplan.RefreshIntervalMinutes * 60 * 1000, stoppingToken);
                }
            }
        }
    }
}