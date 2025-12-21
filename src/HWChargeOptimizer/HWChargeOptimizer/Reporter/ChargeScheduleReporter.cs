using System.Reflection;
using Google.OrTools.LinearSolver;
using HWChargeOptimizer.Calculations;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.Homewizard;
using Microsoft.Extensions.Options;
using ScottPlot;

namespace HWChargeOptimizer.Reporter;

public class ChargeScheduleReporter(IOptionsMonitor<HWChargeOptimizerConfig> config, IHomeWizardBatteryController batteryController)
{
    private const string SystemTimeZone = "W. Europe Standard Time";

    // Use a slightly increased factor to avoid floating point precision issues in the solver
    private const double RoundingFactor = 0.01;

    public async Task RunAsync(bool generateChart = false)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(SystemTimeZone);

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

        var maxChargingRate = batteryCfg.MaxChargeRateKWh;
        var maxDischargingRate = batteryCfg.MaxDischargeRateKWh;

        var chargingEfficiency = batteryCfg.ChargingEfficiency;
        var dischargingEfficiency = batteryCfg.DischargingEfficiency;

        var combinedBatteryCapacity = batteryCfg.Batteries.Sum(s => s.CapacityKWh);
        var currentStateOfCharge = batteryCfg.Batteries.Select((soc, index) => soc.StateOfChargePercentage * batteryCfg.Batteries[index].CapacityKWh / 100.0).Sum();

        if (currentStateOfCharge is null)
        {
            Console.WriteLine("No current state of charge is stored in the configuration file. Please let the application read the state of charge from the Homewizard battery first before running the report or chart function.");
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

        Console.WriteLine("Starting calculation of optimal charging schedule...");

        // Create the solver that will calculate the most efficient charging
        using var solver = Solver.CreateSolver("GLOP");
        if (solver == null)
        {
            throw new InvalidOperationException("Failed to create SCIP solver");
        }

        var resultStatus = OptimizeSchedule.Calculate(solver, scheduleVariables);

        // Display results
        if (resultStatus is Solver.ResultStatus.OPTIMAL or Solver.ResultStatus.FEASIBLE)
        {
            Console.WriteLine("-----------------------------------------------------------");
            Console.WriteLine("Local time  | C | D |  CQ   |  DQ   |  SoC | Tariff");

            foreach (var tariff in tariffs)
            {
                var charge = Math.Round(scheduleVariables.ChargeAmount[tariff.Date].SolutionValue(), 2);
                var discharge = Math.Round(scheduleVariables.DischargeAmount[tariff.Date].SolutionValue(), 2);
                var soc = Math.Round(scheduleVariables.StateOfCharge[tariff.Date].SolutionValue(), 2);

                var chargingStatus = charge > RoundingFactor ? "Y" : " ";
                var dischargingStatus = discharge > RoundingFactor ? "Y" : " ";

                Console.WriteLine(
                    $"{TimeZoneInfo.ConvertTime(tariff.Date, timeZone):dd/MM HH:mm} | {chargingStatus} | {dischargingStatus} | {charge,5:F2} | {discharge,5:F2} | {soc,3:F2} | {tariff.Price:F5}");
            }

            // Calculate net cost
            var totalCost = tariffs.Sum(t => t.Price * scheduleVariables.ChargeAmount[t.Date].SolutionValue()) / 100;
            var totalValue = tariffs.Sum(t => t.Price * scheduleVariables.DischargeAmount[t.Date].SolutionValue()) / 100;

            Console.WriteLine("-----------------------------------------------------------");
            Console.WriteLine($"Total charging cost:   € {totalCost,5:F2}", totalCost);
            Console.WriteLine($"Total discharge cost:  € {totalValue,5:F2}", totalValue);
            Console.WriteLine($"Net cost:              € {totalCost - totalValue,5:F2}");
            Console.WriteLine("-----------------------------------------------------------");

            if (generateChart)
                CreateBatterySchedulePlot(tariffs, scheduleVariables.ChargeAmount, scheduleVariables.DischargeAmount, scheduleVariables.StateOfCharge);
        }
        else
        {
            Console.WriteLine("No solution found. Setting battery to zero charging mode.");
        }
    }

    private static void CreateBatterySchedulePlot(List<Tariff> tariffs,
        Dictionary<DateTimeOffset, Variable> chargeAmount,
        Dictionary<DateTimeOffset, Variable> dischargeAmount,
        Dictionary<DateTimeOffset, Variable> stateOfCharge)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(SystemTimeZone);
        
        // convert tariff times to local time zone for plotting
        foreach (var tariff in tariffs)
        {
            tariff.Date = TimeZoneInfo.ConvertTime(tariff.Date, timeZone);
        }
        
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

        var fontLocation = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Cannot find executing assembly location."), @"Fonts/Roboto-VariableFont.ttf");
        if (!File.Exists(fontLocation))
            throw new InvalidOperationException($"Font file not found at {fontLocation}. Cannot create chart.");
        
        Console.WriteLine("Using font file at " + fontLocation);
        
        // Add a font file to use its typeface for fonts with a given name
        Fonts.AddFontFile(
            name: "Roboto",
            path: Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? throw new InvalidOperationException("Cannot find fonts."), @"Fonts/Roboto-VariableFont.ttf"));
        
        plot.Font.Set("Roboto");
        
        // change figure colors for dark mode
        plot.FigureBackground.Color = Color.FromHex("#181818");
        plot.DataBackground.Color = Color.FromHex("#1f1f1f");

        // change axis and grid colors for dark mode
        plot.Axes.Color(Color.FromHex("#d7d7d7"));
        plot.Grid.MajorLineColor = Color.FromHex("#404040");

        // change legend colors for dark mode
        plot.Legend.BackgroundColor = Color.FromHex("#404040").WithAlpha(0.7);;
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
        
        var plotPath = Path.Combine(Environment.CurrentDirectory, $"battery-schedule-{DateTimeOffset.Now:dd-MM-yyyy-HHmmss}.png");
        
        // Save the plot
        plot.SavePng(plotPath, 1200, 600);

        Console.WriteLine($"Chart saved to {plotPath}");
    }
}