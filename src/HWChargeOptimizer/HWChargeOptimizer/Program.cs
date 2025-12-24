using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.HomeWizard;
using HWChargeOptimizer.Reporter;
using HWChargeOptimizer.Zonneplan;
using ZonneplanAuthentication = HWChargeOptimizer.Zonneplan.ZonneplanAuthentication;

using Microsoft.Extensions.Configuration;

var dataConfigDir = "/data/config";
Directory.CreateDirectory(dataConfigDir);

var runtimeConfigPath = Path.Combine(dataConfigDir, "appsettings.json");

// Kopieer ALLEEN bij eerste start
if (!File.Exists(runtimeConfigPath))
{
    if (!File.Exists("appsettings.json"))
        throw new FileNotFoundException(
            "Default appsettings.json not found in application directory. Cannot create runtime configuration.");

    File.Copy("appsettings.json", runtimeConfigPath);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(dataConfigDir)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

// Serilog configureren
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("/data/logs/hwco-.log", rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8)
    .CreateLogger();

// Serilog als logging provider gebruiken
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.Services.Configure<HWChargeOptimizerConfig>(builder.Configuration.GetSection("HWChargeOptimizer"));

builder.Services.AddHttpClient("NoSslValidation")
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

builder.Services.AddHttpClient();
builder.Services.AddTransient<IZonneplanAuthentication, ZonneplanAuthentication>();
builder.Services.AddSingleton<IHomeWizardAuthentication, HomeWizardAuthentication>();
builder.Services.AddSingleton<IHomeWizardBatteryController, HomeWizardBatteryController>();
builder.Services.AddTransient<IZonneplanTariffReader, ZonneplanTariffReader>();
builder.Services.AddTransient<ChargeScheduleReporter>();
builder.Services.AddTransient<ConfigWriter>();

// Check for command-line arguments
if (args.Length > 0)
{
    var command = args[0].ToLowerInvariant();
    
    switch (command)
    {
        case "--report":
        case "-r":
            var reportHost = builder.Build();
            var reporter = reportHost.Services.GetRequiredService<ChargeScheduleReporter>();
            await reporter.RunAsync();
            break;
        
        case "--service":
        case "-s":
            builder.Services.AddHostedService<ZonneplanScheduleService>();
            builder.Services.AddHostedService<HomeWizardScheduleService>();
    
            var serviceHost = builder.Build();

            await serviceHost.RunAsync();
            break;
            
        default:
            Console.WriteLine("Invalid arguments specified.");
            WriteUsageInstructions();
            break;
    }
}
else
{
    Console.WriteLine("No arguments specified.");
    WriteUsageInstructions();
}

return;

static void WriteUsageInstructions()
{
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  hwco --report   : Generate a charge schedule report.");
    Console.WriteLine("  hwco --chart    : Generate a charge schedule chart.");
    Console.WriteLine("  hwco --service  : Run as a background service.");
}