namespace HWChargeOptimizer.Configuration;

public class HWChargeOptimizerConfig
{
    public required Homewizard Homewizard { get; set; }
    public required Zonneplan Zonneplan { get; set; }
}

public class Homewizard
{
    /// <summary>
    /// The refresh interval in minutes for battery and P1 meter updates.
    /// </summary>
    public int RefreshIntervalSeconds { get; set; } = 60;
    public required P1 P1 { get; set; }
    public required BatteryConfiguration BatteryConfiguration { get; set; }
}

public class P1
{
    public required string Ip { get; set; }
    /// <summary>
    /// For reference only, not used in the code.
    /// </summary>
    public required string Username { get; set; }
    /// <summary>
    /// The token used for authentication with the P1 meter. Refer to the Homewizard documentation for how to obtain this token.
    /// </summary>
    public required string Token { get; set; }
    
    /// <summary>
    /// Stores the latest battery mode.
    /// Possible values: 'zero', 'to_full', 'standby'
    /// </summary>
    public required string BatteryMode { get; set; }
    
    /// <summary>
    /// Latest power measurement in watts. This is taken from the P1 meter so it is the total power usage or the amount of power fed back across all phases.
    /// </summary>
    public double PowerW { get; set; }
    public double MaxConsumptionW { get; set; }
    public double MaxProductionW { get; set; }
    public double TargetPowerW { get; set; }
    
    [Newtonsoft.Json.JsonProperty("LastUpdated", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore )]
    [Newtonsoft.Json.JsonConverter(typeof(DateFormatConverter))]
    public DateTimeOffset? LastUpdated { get; set; }
}

public class BatteryConfiguration
{
    public required List<Battery> Batteries { get; set; }
    public required double MaxChargeRateKWh { get; set; }
    public required double MaxDischargeRateKWh { get; set; }
    public required double ChargingEfficiency { get; set; }
    public required double DischargingEfficiency { get; set; }
}

public class Battery
{
    /// <summary>
    /// Unique and friendly name for the battery.
    /// </summary>
    public required string Name { get; set; }
    /// <summary>
    /// For reference only, not used in the code.
    /// </summary>
    public string Username { get; set; } = string.Empty;
    /// <summary>
    /// The IP address of the battery.
    /// </summary>
    public required string Ip { get; set; }
    /// <summary>
    /// The token used for authentication with the battery. Refer to the Homewizard documentation for how to obtain this token.
    /// </summary>
    public required string Token { get; set; }
    /// <summary>
    /// The capacity in kWh of the battery.
    /// </summary>
    public required double CapacityKWh { get; set; }
    /// <summary>
    /// The latest state of charge in kWh. This is stored at runtime.</summary>
    public double? StateOfChargePercentage { get; set; }
    /// <summary>
    /// Date and time when the battery SoC was last updated. This is stored at runtime.
    /// </summary>
    public DateTimeOffset? LastUpdated { get; set; }
}

public class Zonneplan
{
    public required int RefreshIntervalMinutes { get; set; }
    public required ZonneplanAuthentication Authentication { get; set; }
    public List<Tariff>? Tariffs { get; set; }
}

public class ZonneplanAuthentication
{
    public required string BaseUri { get; set; }
    public required string Username { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    
    [Newtonsoft.Json.JsonProperty("LastUpdated", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore )]
    [Newtonsoft.Json.JsonConverter(typeof(DateFormatConverter))]
    public DateTimeOffset? LastUpdated { get; set; }
    public int ExpiresIn { get; set; }
}

public class Tariff
{
    [Newtonsoft.Json.JsonProperty("Date", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore )]
    [Newtonsoft.Json.JsonConverter(typeof(DateFormatConverter))]
    public DateTimeOffset Date { get; set; }
    public double Price { get; set; }
}

internal class DateFormatConverter : Newtonsoft.Json.Converters.IsoDateTimeConverter
{
    public DateFormatConverter()
    {
        DateTimeFormat = "yyyy-MM-ddTHH:mm:ssZ"; // ISO 8601 format
    }

    public override void WriteJson(Newtonsoft.Json.JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is DateTimeOffset dateTime)
        {
            writer.WriteValue(dateTime.ToString(DateTimeFormat));
        }
        else
        {
            base.WriteJson(writer, value, serializer);
        }
    }
}
