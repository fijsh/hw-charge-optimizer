using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HWChargeOptimizer.Configuration;

public class ConfigWriter
{
    private const string AppSettingsFilePath = "appsettings.json";
    
    public async Task WriteAsync(HWChargeOptimizerConfig hwChargeOptimizerConfig)
    {
        var jsonText = await File.ReadAllTextAsync(AppSettingsFilePath);
        var root = JObject.Parse(jsonText);

        root["HWChargeOptimizer"] = JObject.FromObject(hwChargeOptimizerConfig);
        
        await File.WriteAllTextAsync(AppSettingsFilePath, root.ToString(Formatting.Indented));
    }
}