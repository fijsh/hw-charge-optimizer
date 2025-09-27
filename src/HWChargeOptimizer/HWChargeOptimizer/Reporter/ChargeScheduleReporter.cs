using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Calculations;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.Homewizard;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ScottPlot;

namespace HWChargeOptimizer.Reporter;

public class ChargeScheduleReporter(ILogger<HomewizardScheduleService> logger, IOptionsMonitor<HWChargeOptimizerConfig> config, IHomeWizardBatteryController batteryController)
{
    private const string SystemTimeZone = "W. Europe Standard Time";

    // Use a slightly increased factor to avoid floating point precision issues in the solver
    private const double RoundingFactor = 0.01;

    public async Task RunAsync(bool generateChart = false)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(SystemTimeZone);

        logger.LogInformation("Starting calculation of optimal charging schedule...");

        var currentUtcDateTime = DateTimeOffset.UtcNow;
        currentUtcDateTime = new DateTimeOffset(currentUtcDateTime.Year, currentUtcDateTime.Month, currentUtcDateTime.Day, currentUtcDateTime.Hour, 0, 0, currentUtcDateTime.Offset);

        var cfg = config.CurrentValue;
        var batteryCfg = cfg.Homewizard.BatteryConfiguration;
        var p1 = cfg.Homewizard.P1;

        var tariffs = cfg.Zonneplan.Tariffs?.Where(t => t.Date >= currentUtcDateTime).ToList() ?? [];
        if (tariffs.Count == 0)
        {
            logger.LogError("No current tariffs available in the Zonneplan tariff list.");
            return;
        }

        var maxChargingRate = batteryCfg.MaxChargeRateKWh;
        var maxDischargingRate = batteryCfg.MaxDischargeRateKWh;

        var chargingEfficiency = batteryCfg.ChargingEfficiency;
        var dischargingEfficiency = batteryCfg.DischargingEfficiency;

        var combinedBatteryCapacity = batteryCfg.Batteries.Sum(s => s.CapacityKWh);
        var currentStateOfCharge = batteryCfg.Batteries.Select((soc, index) => soc.StateOfChargePercentage * batteryCfg.Batteries[index].CapacityKWh / 100.0).Sum();

        if (currentStateOfCharge is null)
        {
            logger.LogError("No current state of charge is stored in the configuration file. Please let the application read the state of charge from the Homewizard battery first before running the report or chart function.");
            return;
        }

        var scheduleVariables = new ScheduleVariables
        {
            Tariffs = tariffs,
            MaxChargingRate = maxChargingRate,
            MaxDischargingRate = maxDischargingRate,
            CombinedBatteryCapacity = combinedBatteryCapacity,
            CurrentStateOfCharge = (double)currentStateOfCharge,
            ChargingEfficiency = chargingEfficiency,
            DischargingEfficiency = dischargingEfficiency
        };

        var currentHousePowerUsage = await batteryController.GetLatestPowerMeasurementAsync();

        // lowest and highest tariff today
        var todayTariffs = tariffs.Where(t => TimeZoneInfo.ConvertTime(t.Date, timeZone).Date == TimeZoneInfo.ConvertTime(currentUtcDateTime.Date, timeZone)).ToList();
        var lowestTariff = todayTariffs.Min(t => t.Price);
        var highestTariff = todayTariffs.Max(t => t.Price);

        var currentTariff = tariffs.SingleOrDefault(s => s.Date == currentUtcDateTime);
        if (currentTariff == null)
        {
            logger.LogError("No current tariff found for the current hour {currentHour}. This should never happen.", currentUtcDateTime);
            return;
        }

        var currentBatteryMode = p1.BatteryMode;

        logger.LogInformation("-----------------------------------------------------------");
        logger.LogInformation("Current battery mode:                         {currentBatteryMode}", currentBatteryMode);
        logger.LogInformation("Total battery capacity:                       {combinedBatteryCapacity} kWh", combinedBatteryCapacity);
        logger.LogInformation("Current state of charge (combined):           {currentStateOfCharge:F4} kWh", currentStateOfCharge);
        logger.LogInformation("Current house power consumption / production: {currentPowerUsage} Watt", currentHousePowerUsage);
        logger.LogInformation("Current tariff:                               {currentTariff:F4} / kWh", currentTariff.Price);
        logger.LogInformation("Lowest tariff today:                          {lowestTariff:F4} / kWh", lowestTariff);
        logger.LogInformation("Highest tariff today:                         {highestTariff:F4} / kWh", highestTariff);
        logger.LogInformation("Charging efficiency:                          {chargingEfficiency} %", chargingEfficiency * 100);
        logger.LogInformation("Discharging efficiency:                       {dischargingEfficiency} %", dischargingEfficiency * 100);
        logger.LogInformation("-----------------------------------------------------------");

        logger.LogInformation("Starting calculation of optimal charging schedule...");

        // Create the solver that will calculate the most efficient charging
        using var solver = Solver.CreateSolver("SCIP");
        if (solver == null)
        {
            throw new InvalidOperationException("Failed to create SCIP solver");
        }

        var resultStatus = OptimizeSchedule.Calculate(solver, scheduleVariables);

        // Display results
        if (resultStatus is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE)
        {
            logger.LogInformation("-----------------------------------------------------------");
            logger.LogInformation("Local time  | C | D |  CQ   |  DQ   |  SoC | Tariff");

            foreach (var tariff in tariffs)
            {
                var charge = Math.Round(scheduleVariables.ChargeAmount[tariff.Date].SolutionValue(), 2);
                var discharge = Math.Round(scheduleVariables.DischargeAmount[tariff.Date].SolutionValue(), 2);
                var soc = Math.Round(scheduleVariables.StateOfCharge[tariff.Date].SolutionValue(), 2);

                var chargingStatus = charge > RoundingFactor ? "Y" : " ";
                var dischargingStatus = discharge > RoundingFactor ? "Y" : " ";

                logger.LogInformation(
                    "{DateTimeOffset:dd/MM HH:mm} | {ChargingStatus} | {DischargingStatus} | {Charge,5:F2} | {Discharge,5:F2} | {Soc,3:F2} | {TariffPrice:F5}",
                    TimeZoneInfo.ConvertTime(tariff.Date, timeZone), chargingStatus, dischargingStatus, charge, discharge, soc, tariff.Price);
            }

            // Calculate net cost
            var totalCost = tariffs.Sum(t => t.Price * scheduleVariables.ChargeAmount[t.Date].SolutionValue()) / 100;
            var totalValue = tariffs.Sum(t => t.Price * scheduleVariables.DischargeAmount[t.Date].SolutionValue()) / 100;

            logger.LogInformation("-----------------------------------------------------------");
            logger.LogInformation("Total charging cost:   € {TotalCost,5:F2}", totalCost);
            logger.LogInformation("Total discharge cost:  € {TotalValue,5:F2}", totalValue);
            logger.LogInformation("Net cost:              € {NetCost,5:F2}", totalCost - totalValue);
            logger.LogInformation("-----------------------------------------------------------");

            if (generateChart)
                CreateBatterySchedulePlot(tariffs, scheduleVariables.ChargeAmount, scheduleVariables.DischargeAmount, scheduleVariables.StateOfCharge);
        }
        else
        {
            logger.LogWarning("No solution found. Setting battery to zero charging mode.");
        }
    }

    private void CreateBatterySchedulePlot(List<Tariff> tariffs,
        Dictionary<DateTimeOffset, Variable> chargeAmount,
        Dictionary<DateTimeOffset, Variable> dischargeAmount,
        Dictionary<DateTimeOffset, Variable> stateOfCharge)
    {
        // Create arrays for plotting
        var times = new double[tariffs.Count];
        var socValues = new double[tariffs.Count];
        var chargeValues = new double[tariffs.Count];
        var dischargeValues = new double[tariffs.Count];
        var tariffValues = new double[tariffs.Count];
        var timeLabels = new string[tariffs.Count];

        // Fill arrays with data
        for (var i = 0; i < tariffs.Count; i++)
        {
            var tariff = tariffs[i];
            times[i] = i;
            socValues[i] = stateOfCharge[tariff.Date].SolutionValue();
            chargeValues[i] = chargeAmount[tariff.Date].SolutionValue();
            dischargeValues[i] = dischargeAmount[tariff.Date].SolutionValue();
            tariffValues[i] = tariff.Price;
            timeLabels[i] = tariff.Date.ToString("HH");
        }

        // Create plot
        var plot = new Plot();
        //plot.DataBackground.AntiAlias = true;
        
        // change figure colors for dark mode
        plot.FigureBackground.Color = Color.FromHex("#181818");
        plot.DataBackground.Color = Color.FromHex("#1f1f1f");

        // change axis and grid colors for dark mode
        plot.Axes.Color(Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = Color.FromHex("#404040");

        // change legend colors for dark mode
        plot.Legend.BackgroundColor = Color.FromHex("#404040");
        plot.Legend.FontColor = Color.FromHex("#d7d7d7");
        plot.Legend.OutlineColor = Color.FromHex("#d7d7d7");

        // Add state of charge as a line with markers
        var socPlot = plot.Add.Scatter(times, socValues);
        socPlot.MarkerShape = MarkerShape.FilledCircle;
        socPlot.MarkerSize = 5;
        socPlot.LineWidth = 2;
        socPlot.LineColor = Colors.Blue;
        socPlot.MarkerColor = Colors.Blue;
        socPlot.LegendText = "State of Charge";

        for (var i = 0; i < times.Length; i++)
        {
            Bar chargeBar = new()
            {
                Value = chargeValues[i],
                Position = times[i],
                Size = 0.3,
                FillColor = Colors.Purple.WithAlpha(0.7),
                LineColor = Colors.Purple,
            };

            var barPlot = plot.Add.Bar(chargeBar);
            if (i == 0)
                barPlot.LegendText = "Charge";
        }

        for (var i = 0; i < times.Length; i++)
        {
            Bar dischargeBar = new()
            {
                Value = dischargeValues[i] * -1,
                Position = times[i],
                Size = 0.3,
                FillColor = Colors.Green.WithAlpha(0.7),
                LineColor = Colors.Green,
            };

            var barPlot = plot.Add.Bar(dischargeBar);
            if (i == 0)
                barPlot.LegendText = "Discharge";
        }

        // Create a second y-axis for tariff values
        var rightAxis = plot.Axes.Right;
        rightAxis.Label.Text = "Tariff (cents)";
        rightAxis.IsVisible = true;

        // Add tariff values on the right axis
        var tariffPlot = plot.Add.Scatter(times, tariffValues);
        tariffPlot.LineWidth = 2;
        tariffPlot.LineColor = Colors.Orange;
        tariffPlot.MarkerShape = MarkerShape.FilledDiamond;
        tariffPlot.MarkerSize = 5;
        tariffPlot.MarkerColor = Colors.Orange;
        tariffPlot.LegendText = "Tariff";
        tariffPlot.Axes.YAxis = plot.Axes.Right;
        
        // Configure axes
        plot.Axes.Bottom.Label.Text = "Time";
        plot.Axes.Left.Label.Text = "Energy (kWh)";

        // Add custom tick labels
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            positions: times,
            labels: timeLabels);
        plot.Axes.Bottom.MajorTickStyle.Length = 0;

        // Add legend
        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperLeft;

        // Add title
        plot.Title("Battery Charge/Discharge Planned Schedule");
        
        // set the color palette used when coloring new items added to the plot
        plot.Add.Palette = new ScottPlot.Palettes.Penumbra();
        
        var plotPath = Path.Combine(Environment.CurrentDirectory, $"battery_schedule_{DateTimeOffset.Now:dd-MM-yyyyHHmmss}.png");
        
        // Save the plot
        plot.SavePng(plotPath, 1200, 600);

        Console.WriteLine($"Chart saved to {plotPath}");
    }
}