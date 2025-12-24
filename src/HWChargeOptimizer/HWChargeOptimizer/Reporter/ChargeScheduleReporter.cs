using System.Globalization;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.HomeWizard;
using Microsoft.Extensions.Options;

namespace HWChargeOptimizer.Reporter;

public class ChargeScheduleReporter(IOptionsMonitor<HWChargeOptimizerConfig> config, IHomeWizardBatteryController batteryController)
{
    public async Task RunAsync()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(Constants.SystemTimeZone);

        Console.WriteLine("Starting calculation of optimal charging schedule...");

        var currentUtcDateTime = DateTimeOffset.UtcNow;
        currentUtcDateTime = new DateTimeOffset(currentUtcDateTime.Year, currentUtcDateTime.Month, currentUtcDateTime.Day, currentUtcDateTime.Hour, 0, 0, currentUtcDateTime.Offset);

        var cfg = config.CurrentValue;
        var batteryCfg = cfg.Homewizard.BatteryConfiguration;
        var p1 = cfg.Homewizard.P1;

        var tariffs = cfg.Zonneplan.Tariffs?.Where(t => t.Date >= currentUtcDateTime).ToList() ?? [];
        if (tariffs.Count == 0)
        {
            Console.WriteLine("No current tariffs available in the Zonneplan tariff list.");
            return;
        }

        var chargingEfficiency = batteryCfg.ChargingEfficiency;
        var dischargingEfficiency = batteryCfg.DischargingEfficiency;

        var combinedBatteryCapacity = batteryCfg.Batteries.Sum(s => s.CapacityKWh);
        var currentStateOfCharge = batteryCfg.Batteries.Select((soc, index) => soc.StateOfChargePercentage * batteryCfg.Batteries[index].CapacityKWh / 100.0).Sum();

        if (currentStateOfCharge is null)
        {
            Console.WriteLine("No current state of charge is stored in the configuration file. Please let the application read the state of charge from the HomeWizard battery first before running the report or chart function.");
            return;
        }

        var currentHousePowerUsage = await batteryController.GetLatestPowerMeasurementAsync();

        // lowest and highest tariff today
        var todayTariffs = tariffs.Where(t => 
            TimeZoneInfo.ConvertTime(t.Date, timeZone).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) 
            == TimeZoneInfo.ConvertTime(currentUtcDateTime.Date, timeZone).Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
        
        var lowestTariff = todayTariffs.Min(t => t.Price);
        var highestTariff = todayTariffs.Max(t => t.Price);

        var currentTariff = tariffs.SingleOrDefault(s => s.Date == currentUtcDateTime);
        if (currentTariff == null)
        {
            Console.WriteLine($"No current tariff found for the current hour {currentUtcDateTime}. This should never happen.");
            return;
        }

        var currentBatteryMode = p1.BatteryMode;

        Console.WriteLine("-----------------------------------------------------------");
        Console.WriteLine($"Current battery mode:                         {currentBatteryMode}");
        Console.WriteLine($"Total battery capacity:                       {combinedBatteryCapacity} kWh");
        Console.WriteLine($"Current state of charge (combined):           {currentStateOfCharge:F4} kWh");
        Console.WriteLine($"Current house power consumption / production: {currentHousePowerUsage} Watt");
        Console.WriteLine($"Current tariff:                               {currentTariff.Price:F4} / kWh");
        Console.WriteLine($"Lowest tariff today:                          {lowestTariff:F4} / kWh");
        Console.WriteLine($"Highest tariff today:                         {highestTariff:F4} / kWh");
        Console.WriteLine($"Charging efficiency:                          {chargingEfficiency * 100} %");
        Console.WriteLine($"Discharging efficiency:                       {dischargingEfficiency * 100} %");
        Console.WriteLine("-----------------------------------------------------------");
        
        var lastCalculatedChargingSchedule = await File.ReadAllTextAsync(Constants.ChargingScheduleFileName);
        Console.WriteLine(lastCalculatedChargingSchedule);
    }
}