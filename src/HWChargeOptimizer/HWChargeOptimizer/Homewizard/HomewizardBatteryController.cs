using System.Net.Http.Headers;
using System.Text;
using HWChargeOptimizer.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace HWChargeOptimizer.Homewizard;

public interface IHomeWizardBatteryController
{
    /// <summary>
    /// Sets the battery mode.
    /// </summary>
    /// <param name="mode">The mode to set.</param>
    /// <returns>A JObject containing the updated battery status.</returns>
    Task<JObject?> SetBatteryModeAsync(string mode);

    /// <summary>
    /// This method retrieves the latest power measurement from the P1 meter.
    /// </summary>
    /// <returns>Returns the total power usage or the amount of power fed back across all phases.</returns>
    Task<int> GetLatestPowerMeasurementAsync();

    /// <summary>
    /// This method retrieves the latest battery state of charge in percentage for all configured batteries.
    /// </summary>
    /// <returns>Returns a list of <see cref="BatteryStateOfCharge"/> objects.</returns>
    Task<List<BatteryStateOfCharge>> GetBatteryStateOfChargeAsync();

    /// <summary>
    /// Used to retrieve information about the control system of the Plug-In Battery/batteries.
    /// </summary>
    /// <returns>A <see cref="BatteriesStatus"/> object</returns>
    Task<BatteriesStatus> GetBatteriesStatusAsync();
}

public class HomeWizardBatteryController : IHomeWizardBatteryController
{
    private readonly IOptionsMonitor<HWChargeOptimizerConfig> _config;
    private readonly ILogger<HomeWizardBatteryController> _logger;
    private readonly HttpClient _client;

    public HomeWizardBatteryController(ILogger<HomeWizardBatteryController> logger, IOptionsMonitor<HWChargeOptimizerConfig> config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
        _logger = logger;

        var httpClientHandler = new HttpClientHandler()
        {
            // no SSL validation
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };

        _client = new HttpClient(httpClientHandler);
        _client.DefaultRequestHeaders.Add("X-Api-Version", "2");
    }

    public async Task<List<BatteryStateOfCharge>> GetBatteryStateOfChargeAsync()
    {
        _logger.LogInformation("Requesting Homewizard state of charge...");

        List<BatteryStateOfCharge> stateOfCharge = new(_config.CurrentValue.Homewizard.BatteryConfiguration.Batteries.Count);
        foreach (var battery in _config.CurrentValue.Homewizard.BatteryConfiguration.Batteries)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{new Uri(string.Concat("https://", battery.Ip, "/api/measurement"))}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", battery.Token);
            var resp = await _client.SendAsync(request);

            resp.EnsureSuccessStatusCode();

            var jsonResponse = JObject.Parse(await resp.Content.ReadAsStringAsync());
            stateOfCharge.Add(new BatteryStateOfCharge
            {
                Name = battery.Name,
                Ip = battery.Ip,
                CapacityKWh = battery.CapacityKWh,
                StateOfChargePercentage = jsonResponse.Value<double>("state_of_charge_pct")
            });

            _logger.LogInformation("Battery '{BatteryName}' state of charge: {StateOfCharge}%", battery.Name, jsonResponse.Value<double>("state_of_charge_pct"));
        }

        return stateOfCharge;
    }

    public async Task<BatteriesStatus> GetBatteriesStatusAsync()
    {
        _logger.LogInformation("Requesting Homewizard batteries status...");

        var request = new HttpRequestMessage(HttpMethod.Get, $"{new Uri(string.Concat("https://", _config.CurrentValue.Homewizard.P1.Ip, "/api/batteries"))}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CurrentValue.Homewizard.P1.Token);
        var resp = await _client.SendAsync(request);

        resp.EnsureSuccessStatusCode();

        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

        return new BatteriesStatus
        {
            Mode = json.Value<string>("mode")!,
            PowerW = json.Value<double>("power_w"),
            TargetPowerW = json.Value<double>("target_power_w"),
            MaxConsumptionW = json.Value<double>("max_consumption_w"),
            MaxProductionW = json.Value<double>("max_production_w")
        };
    }

    public async Task<JObject?> SetBatteryModeAsync(string hwcoBatteryMode)
    {
        string[] permissions = hwcoBatteryMode switch
        {
            BatteryMode.FullCharge => [], // FullCharge mode does not allow permissions to be set, this is handled below
            BatteryMode.ZeroDischargeOnly => [Permission.DischargeAllowed],
            BatteryMode.ZeroChargeOnly => [Permission.ChargeAllowed],
            BatteryMode.Zero => [Permission.ChargeAllowed, Permission.DischargeAllowed],
            BatteryMode.Standby => [],
            _ => throw new Exception($"Unknown battery mode '{hwcoBatteryMode}'")
        };

        var mode = hwcoBatteryMode switch
        {
            BatteryMode.FullCharge => "to_full",
            BatteryMode.ZeroDischargeOnly => "zero",
            BatteryMode.ZeroChargeOnly => "zero",
            BatteryMode.Zero => "zero",
            BatteryMode.Standby => "zero", // Standby is achieved by removing all permissions in zero mode, standby should not be used anymore since this has become legacy according to the Homewizard API docs
            _ => throw new Exception($"Unknown battery mode '{hwcoBatteryMode}'")
        };
        
        _logger.LogInformation("Setting battery mode to {HwcoMode} (HomeWizard mode: {Mode}) with permissions: {Permissions}", hwcoBatteryMode, mode, string.Join(", ", permissions));

        // FullCharge mode does not allow permissions to be set
        var payload = hwcoBatteryMode == BatteryMode.FullCharge ? JObject.FromObject(new { mode }) : JObject.FromObject(new { mode, permissions });

        var request = new HttpRequestMessage(HttpMethod.Put, $"{new Uri(string.Concat("https://", _config.CurrentValue.Homewizard.P1.Ip, "/api/batteries"))}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CurrentValue.Homewizard.P1.Token);
        request.Content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json");
        var resp = await _client.SendAsync(request);

        resp.EnsureSuccessStatusCode();

        return JObject.Parse(await resp.Content.ReadAsStringAsync());
    }

    public async Task<int> GetLatestPowerMeasurementAsync()
    {
        _logger.LogInformation("Requesting latest power measurement from Homewizard P1 meter...");

        var request = new HttpRequestMessage(HttpMethod.Get, $"{new Uri(string.Concat("https://", _config.CurrentValue.Homewizard.P1.Ip, "/api/measurement"))}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.CurrentValue.Homewizard.P1.Token);
        var resp = await _client.SendAsync(request);

        resp.EnsureSuccessStatusCode();

        return JObject.Parse(await resp.Content.ReadAsStringAsync()).Value<int>("power_w");
    }
}

/// <summary>
/// These are the battery modes available in this controller. They map to Homewizard modes.
/// </summary>
internal struct BatteryMode
{
    /// <summary>
    /// NoM modus, battery tries to keep house at 0 kWh consumption. The battery will both charge and discharge as needed.
    /// </summary>
    public const string Zero = "zero";

    /// <summary>
    /// Forced full charge mode, battery will charge to 100% regardless of consumption.
    /// </summary>
    public const string FullCharge = "fullcharge";

    /// <summary>
    /// The battery will only charge, it will not discharge.
    /// </summary>
    public const string ZeroChargeOnly = "chargeonly";

    /// <summary>
    /// The battery will only discharge, it will not charge.
    /// </summary>
    public const string ZeroDischargeOnly = "dischargeonly";

    /// <summary>
    /// Battery will not charge or discharge, it will only keep the current state.
    /// </summary>
    public const string Standby = "standby";
}

internal struct Permission
{
    /// <summary>
    /// The battery is allowed to charge.
    /// </summary>
    public const string ChargeAllowed = "charge_allowed";

    /// <summary>
    /// The battery is allowed to discharge.
    /// </summary>
    public const string DischargeAllowed = "discharge_allowed";
}

/// <summary>
/// Contains the combined status of all batteries
/// </summary>
public struct BatteriesStatus
{
    /// <summary>
    /// Control mode of the Plug-In Battery. Can be either zero, to_full, or standby.
    /// </summary>
    public string Mode { get; init; }

    /// <summary>
    /// Current combined power consumption/production of the controlled Plug-In Batteries.
    /// </summary>
    public double PowerW { get; init; }

    /// <summary>
    /// Target power consumption/production of the controlled Plug-In Batteries.
    /// </summary>
    public double TargetPowerW { get; init; }

    /// <summary>
    /// Maximum allowed consumption power of the controlled Plug-In Batteries.
    /// </summary>
    public double MaxConsumptionW { get; init; }

    /// <summary>
    /// Maximum allowed production power of the controlled Plug-In Batteries.
    /// </summary>
    public double MaxProductionW { get; init; }
}

/// <summary>
/// Contains information about a single battery's state of charge.
/// </summary>
public struct BatteryStateOfCharge
{
    public string Name { get; init; }
    public string Ip { get; init; }
    public double CapacityKWh { get; init; }
    public double StateOfChargePercentage { get; init; }
}