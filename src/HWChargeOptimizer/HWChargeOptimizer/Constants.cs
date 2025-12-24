namespace HWChargeOptimizer;

public static class Constants
{
    public const string SystemTimeZone = "W. Europe Standard Time";

    // Use a slightly increased factor to avoid floating point precision issues in the solver
    public const double RoundingFactor = 0.01;
    
    // Solver type
    public const string SolverType = "GLOP";

    public const string ChargingScheduleFileName = "/data/logs/charging-schedule.log";
    public const string PlotFileName = "/data/logs/charging-schedule.png";
}