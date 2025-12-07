# Homewizard Charge Optimizer

**Homewizard Charge Optimizer** automates Homewizard battery charging based on Zonneplan's dynamic electricity tariffs.  
It fetches daily tariffs from Zonneplan, analyzes them, and schedules battery charging for the cheapest hours.  
The goal: maximize savings and efficiency by charging when electricity is cheapest (or negative).  

Run it autonomously on a Raspberry Pi for always-optimized battery charging.

**Example of a calculated charging schedule:**

![battery_schedule.png](battery_schedule.png)

This project is a .NET 9 application that integrates with Homewizard batteries and Zonneplan's API to manage battery charging intelligently.  
It is designed for C# developers who want a customizable, open-source alternative to Home Assistant.

The applications uses linear programming (https://developers.google.com/optimization/lp) to optimize battery charging schedules based on electricity prices, battery capacity, and SoC.
Every 5 minutes (configurable) it polls the Homewizard P1 meter for current consumption/production data and battery status and recalculates the optimal charging schedule.

**Disclaimer: I cannot guarantee that this application will work for you. Use at your own risk. I am not responsible for any damage or issues caused by using this application.**

---

## Features

- Automatic retrieval of Zonneplan dynamic tariffs
- Smart scheduling for Homewizard battery charging
- Support for multiple Homewizard batteries
- Configurable thresholds and parameters
- Runs as a background service on for example a Raspberry Pi
- Automatic restart on reboot when configured as a service
- Chart generation of the active charge plan for easy visualization
- Command line report functionality for quick status checks

---

## Known Limitations
- The Zonneplan API has rate limits. Avoid setting the tariff refresh interval too low to prevent hitting these limits.

## Prerequisites

- Windows or Linux systems
- .NET 9 SDK and Runtime
- Homewizard battery and P1 meter
- Zonneplan account with API access
- ! You need to have the latest (bèta) firmware of the Homewizard P1 meter since support for discharge only mode has been implemented in the main branch.

---

## Installation on a Raspberry Pi

Check the [Wiki](https://github.com/fijsh/hw-charge-optimizer/wiki/Installation-on-a-Raspberry-Pi) for the latest instructions:

## Creating tokens for Homewizard and Zonneplan

Check the [Wiki](https://github.com/fijsh/hw-charge-optimizer/wiki/Creating-Homewizard-and-Zonneplan-tokens) for information about how to create tokens for Homewizard and Zonneplan.

## Configure the Application

The `appsettings.json` file contains all configuration settings.

### Example Configuration

```json
{
  "HWChargeOptimizer": {
    "Homewizard": {
      "RefreshIntervalMinutes": 5,
      "P1": {
        "Ip": "ip address of your p1 meter",
        "Username": "your-p1-username",
        "Token": "your-p1-token",
        "BatteryMode": "standby",
        "PowerW": 0,
        "MaxConsumptionW": 0,
        "MaxProductionW": 0,
        "TargetPowerW": 0,
        "LastUpdated": null
      },
      "Batteries": [
        {
          "Name": "Battery 1",
          "Username": "your-battery-username",
          "Ip": "ip address of your battery",
          "Token": "your-battery-token",
          "StateOfChargePercentage": 0.0,
          "CapacityKWh": 2.7
        }
      ],
      "MaxChargeRateKWh": 0.8,
      "MaxDischargeRateKWh": 0.8,
      "ChargingEfficiency": 0.8,
      "DischargingEfficiency": 1
    },
    "Zonneplan": {
      "RefreshIntervalMinutes": 60,
      "Authentication": {
        "BaseUri": "https://api.zonneplan.nl",
        "Username": "your-zonneplan-username",
        "AccessToken": "your access token",
        "RefreshToken": "your refresh token",
        "LastUpdated": null,
        "ExpiresIn": 0
      },
      "Tariffs": []
    }
  }
}
```

#### Configuration Settings Explained

- **Homewizard**
    - `RefreshIntervalMinutes`: How often (in minutes) to poll the Homewizard battery and P1 meter. Recommended: 5 minutes.
    - `P1`: Homewizard P1 meter settings.
        - `Ip`: IP address of your P1 meter.
        - `Username`: Username for the P1 meter (friendly name for reference only).
        - `Token`: Authentication token for the P1 meter.
        - `BatteryMode`: Current battery mode (`zero`, `to_full`, `standby`). (auto-managed)
        - `PowerW`, `MaxConsumptionW`, `MaxProductionW`, `TargetPowerW`: Internal state. (auto-managed).
        - `MaxConsumptionW`: Max consumption in watts. retrieved from P1 meter (auto-managed).
        - `MaxProductionW`: Max production in watts. retrieved from P1 meter. (auto-managed),
        - `TargetPowerW`: Target power in watts. retrieved from P1 meter. (auto-managed),
        - `LastUpdated`: Last update timestamp (auto-managed).
    - `Batteries`: List of Homewizard batteries to control.
        - `Name`: Friendly name.
        - `Username`: Username for the battery (for reference only).
        - `Ip`: IP address of the battery.
        - `Token`: Authentication token for the battery.
        - `CapacityKWh`: Battery capacity in kWh.
        - `StateOfChargePercentage`: Current state of charge percentage (0-100). (auto-managed)
        - `LastUpdated`: Last update timestamp (auto-managed).
    - `MaxChargeRateKWh`: Maximum charge rate in kWh. Set according to your battery specs.
    - `MaxDischargeRateKWh`: Maximum discharge rate in kWh. Set according to your battery specs.
    - `ChargingEfficiency`: Charging efficiency (0-1). Set according to your battery specs.
    - `DischargingEfficiency`: Discharging efficiency (0-1). Set according to your battery specs.
- **Zonneplan**
    - `RefreshIntervalMinutes`: How often (in minutes) to fetch new tariffs from Zonneplan. Don't set too low to avoid hitting API limits. Recommended: 60 minutes.
    - `Authentication`: Zonneplan API credentials.
        - `BaseUri`: Zonneplan API base URL.
        - `Username`: Your Zonneplan account username.
        - `AccessToken`: First access token for Zonneplan API, after this will be auto-managed.
        - `RefreshToken`: First refresh token for Zonneplan API, after this will be auto-managed.
        - `LastUpdated`, First update timestamp (auto-managed).
        - `ExpiresIn`: Number of seconds until the access token expires (auto-managed).
    - `Tariffs`: List of tariffs (auto-managed).

---

**For auto-managed fields, the application will update them as needed. Just enter a 0 for numeric fields and null for date fields. See the example appsettings.json in the repo.**

## Troubleshooting

The application uses Serilog for logging. If you encounter issues:

- Ensure your configuration file is correct and tokens are valid.
- Verify network connectivity to Homewizard and Zonneplan endpoints.
- Make sure you don't hit Zonneplan API rate limits.
- You must create a local user/token for the P1 meter and each Homewizard battery.

---

## Contributing

Feel free to submit pull requests! For issues, use the GitHub issue tracker.
If you fork the project, it is nice to add a reference to the original repository and author.
---

## License

```markdown
Licensed under the Creative Commons Attribution-NonCommercial (CC BY-NC) license.

Commercial use is not allowed without prior permission from the author.
```
---
