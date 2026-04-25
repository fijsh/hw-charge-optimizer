using System.Text;
using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace HWChargeOptimizer.HomeWizard;

public interface IHomeWizardAuthentication
{
    Task<string?> GetP1TokenAsync(string username);
}

public class HomeWizardAuthentication(ILogger<HomeWizardAuthentication> logger, IHttpClientFactory httpClientFactory, IOptionsMonitor<HWChargeOptimizerConfig> config) : IHomeWizardAuthentication
{
    public async Task<string?> GetP1TokenAsync(string username)
    {
        logger.LogInformation("Requesting Homewizard token for user: {Username}", username);
        
        try
        {
            var httpClient = httpClientFactory.CreateClient("NoSslValidation");
            httpClient.BaseAddress = new Uri(string.Concat("https://", config.CurrentValue.Homewizard.P1.Ip, "/api"));
            httpClient.DefaultRequestHeaders.Add("X-Api-Version", "2");

            var payload = new { name = username };
            var content = new StringContent(JObject.FromObject(payload).ToString(), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("/user", content);

            response.EnsureSuccessStatusCode();

            var json = JObject.Parse(await response.Content.ReadAsStringAsync());

            return json["token"]?.ToString();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting token for user: {Username}", username);
        }
        finally
        {
            logger.LogInformation("Finished requesting Homewizard token");
        }

        return null;
    }
}