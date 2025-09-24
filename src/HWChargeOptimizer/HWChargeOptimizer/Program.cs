using System.Text;
using HomewizardBatteryOptimization.Homewizard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using HWChargeOptimizer.Configuration;
using HWChargeOptimizer.Homewizard;
using HWChargeOptimizer.Zonneplan;
using HomewizardAuthentication = HWChargeOptimizer.Homewizard.HomewizardAuthentication;
using ZonneplanAuthentication = HWChargeOptimizer.Zonneplan.ZonneplanAuthentication;

var builder = Host.CreateApplicationBuilder(args);

// Serilog configureren
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File("logs/app.log", rollingInterval: RollingInterval.Day, encoding: Encoding.UTF8)
    .CreateLogger();

// Serilog als logging provider gebruiken
builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.Services.Configure<HWChargeOptimizerConfig>(builder.Configuration.GetSection("HWChargeOptimizer"));

builder.Services.AddHttpClient("NoSslValidation")
    .ConfigurePrimaryHttpMessageHandler(() =>
        new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator });

builder.Services.AddHttpClient(); // Default HttpClient
builder.Services.AddTransient<IZonneplanAuthentication, ZonneplanAuthentication>();
builder.Services.AddSingleton<IHomewizardAuthentication, HomewizardAuthentication>();
builder.Services.AddSingleton<IHomeWizardBatteryController, HomeWizardBatteryController>();
builder.Services.AddTransient<IZonneplanTariffReader, ZonneplanTariffReader>();
builder.Services.AddTransient<ConfigWriter>();

// recurring service to refresh Zonneplan token every 4 hours
builder.Services.AddHostedService<ZonneplanScheduleService>();
builder.Services.AddHostedService<HomewizardScheduleService>();

var host = builder.Build();

await host.RunAsync();