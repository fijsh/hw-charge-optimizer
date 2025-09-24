using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HWChargeOptimizer.Zonneplan;

public interface IZonneplanAuthentication
{
    Task<string?> RequestTempPassAsync(string email);
    Task<JObject?> GetTempPassAsync(string email, string uuid);
    Task<JObject?> RefreshTokenAsync(string refreshToken);
}

public class ZonneplanAuthentication(ILogger<ZonneplanAuthentication> logger, IHttpClientFactory httpClientFactory) : IZonneplanAuthentication
{
    //private const string AppVersion = "4.22.3";
    private const string LoginRequestUri = "https://app-api.zonneplan.nl/auth/request";
    private const string OAuth2TokenUri = "https://app-api.zonneplan.nl/oauth/token";
    //private const string IntegrationVersion = "1.0.0"; // Vervang indien nodig
    
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();

    public async Task<string?> RequestTempPassAsync(string email)
    {
        logger.LogInformation("Starting RequestTempPassAsync for email: {Email}", email);
        try
        {
            var payload = JsonConvert.SerializeObject(new { email });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(LoginRequestUri, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var uuid = result["data"]?["uuid"]?.ToString();
            
            logger.LogInformation("RequestTempPassAsync succeeded for email: {Email}, UUID: {Uuid}", email, uuid);
            
            return uuid;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error requesting temporary login from Zonneplan API for email: {Email}", email);
            return null;
        }
        finally
        {
            logger.LogDebug("RequestTempPassAsync finished for email: {Email}", email);
        }
    }

    public async Task<JObject?> GetTempPassAsync(string email, string uuid)
    {
        logger.LogInformation("Starting GetTempPassAsync for email: {Email}, UUID: {Uuid}", email, uuid);
        try
        {
            using var response = await _httpClient.GetAsync($"{LoginRequestUri}/{uuid}");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JObject.Parse(json);
            var data = result["data"];

            if (data?["is_activated"]?.Value<bool>() == true && data["password"] != null)
            {
                logger.LogInformation("Temporary password activated for email: {Email}, UUID: {Uuid}", email, uuid);
                
                var grantParams = new
                {
                    grant_type = "one_time_password",
                    email,
                    password = data["password"]?.ToString()
                };

                var tokenResult = await RequestNewTokenAsync(grantParams);
                
                logger.LogInformation("GetTempPassAsync succeeded for email: {Email}, UUID: {Uuid}", email, uuid);
                return tokenResult;
            }

            logger.LogWarning("Temporary password not activated or missing for email: {Email}, UUID: {Uuid}", email, uuid);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting temporary password from Zonneplan API for email: {Email}, UUID: {Uuid}", email, uuid);
            return null;
        }
        finally
        {
            logger.LogDebug("GetTempPassAsync finished for email: {Email}, UUID: {Uuid}", email, uuid);
        }
    }

    public async Task<JObject?> RefreshTokenAsync(string refreshToken)
    {
        logger.LogInformation("Starting RefreshTokenAsync...");
        try
        {
            var grantParams = new
            {
                grant_type = "refresh_token",
                refresh_token = refreshToken
            };

            var result = await RequestNewTokenAsync(grantParams);
            logger.LogInformation("RefreshTokenAsync succeeded.");
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error refreshing token from Zonneplan API for refreshToken: {RefreshToken}", refreshToken);
            return null;
        }
        finally
        {
            logger.LogDebug("RefreshTokenAsync finished.");
        }
    }
    
    private async Task<JObject?> RequestNewTokenAsync(object grantParams)
    {
        logger.LogInformation("Starting RequestNewTokenAsync...");
        try
        {
            var payload = JsonConvert.SerializeObject(grantParams);
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(OAuth2TokenUri, content);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            logger.LogInformation("RequestNewTokenAsync succeeded.");
            return JObject.Parse(json);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error requesting new token from Zonneplan API with grantParams: {GrantParams}", JsonConvert.SerializeObject(grantParams));
            return null;
        }
        finally
        {
            logger.LogDebug("RequestNewTokenAsync finished.");
        }
    }
}