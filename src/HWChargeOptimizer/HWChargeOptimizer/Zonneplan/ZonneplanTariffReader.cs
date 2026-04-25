using System.Globalization;
using System.Net.Http.Headers;
using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace HWChargeOptimizer.Zonneplan;

public interface IZonneplanTariffReader
{
    Task<List<Tariff>?> GetElectricityTariffsAsync();
}

public class ZonneplanTariffReader : IZonneplanTariffReader
{
    private readonly ILogger<ZonneplanTariffReader> _logger;
    private readonly IOptionsMonitor<HWChargeOptimizerConfig> _config;
    private readonly HttpClient _client;

    public ZonneplanTariffReader(ILogger<ZonneplanTariffReader> logger, IOptionsMonitor<HWChargeOptimizerConfig> config)
    {
        _logger = logger;
        _config = config;
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://app-api.zonneplan.nl/")
        };
        _client.DefaultRequestHeaders.Add("x-app-version", "4.22.3");
        _client.DefaultRequestHeaders.Add("x-app-environment", "production");
    }
    
    public async Task<List<Tariff>?> GetElectricityTariffsAsync()
    {
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _config.CurrentValue.Zonneplan.Authentication.AccessToken);
        
        var userAccountsResponse = await _client.GetAsync("user-accounts/me");
        userAccountsResponse.EnsureSuccessStatusCode();
        var userAccountsResponseContent = await userAccountsResponse.Content.ReadAsStringAsync();

        var connectionUuid = JObject.Parse(userAccountsResponseContent).SelectToken("data.address_groups[0].connections[0].uuid")?.Value<string>();
        
        if (string.IsNullOrEmpty(connectionUuid))
        {
            _logger.LogInformation("No valid connection UUID found in response from Zonneplan.");
            return null;
        }
        
        // haal summary op met tarieven
        var summaryResponse = await _client.GetAsync($"connections/{connectionUuid}/summary");
        summaryResponse.EnsureSuccessStatusCode();
        var summaryJson = await summaryResponse.Content.ReadAsStringAsync();
		
        var summaryObj = JObject.Parse(summaryJson);

        var tariffsList = new List<Tariff>();
        if (summaryObj["data"]?["price_per_hour"] is JArray tariffsArray)
        {
            tariffsList.AddRange(tariffsArray.Select(item => new Tariff { From = DateTimeOffset.Parse(item["datetime"]!.ToString(), CultureInfo.InvariantCulture), Price = item["electricity_price"]?.Value<double>() ?? 0 }));
        }
        else
        {
            _logger.LogInformation("No tariffs found in response.");
        }

        return tariffsList;
    }
}

public class Tariff
{
    public DateTimeOffset From { get; set; }
    public double Price { get; set; }
}