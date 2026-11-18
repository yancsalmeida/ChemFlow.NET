using System.Diagnostics;
using System.Diagnostics.Metrics;
using ChemFlow.Api.Contracts;

namespace ChemFlow.Api.Observability;

/// <summary>
/// OpenTelemetry ActivitySource and Prometheus Meter definitions for ChemFlow.NET.
/// </summary>
public sealed class ChemFlowTelemetry : IDisposable
{
    public const string ServiceName = "ChemFlow.NET";
    public static readonly ActivitySource ActivitySource = new(ServiceName, "1.0.0");

    private static readonly Meter StaticMeter = new(ServiceName, "1.0.0");
    public static readonly Counter<long> SilTicksTotal = StaticMeter.CreateCounter<long>(
        "chemflow_sil_ticks_total",
        unit: "ticks",
        description: "Total SIL Modbus supervisory control ticks executed.");

    public static readonly Histogram<double> ModbusRttMs = StaticMeter.CreateHistogram<double>(
        "chemflow_modbus_rtt_ms",
        unit: "ms",
        description: "Round-trip latency of Modbus TCP read/write exchange with ProcessTwin.Py.");

    public static readonly Histogram<double> NmpcSolveMs = StaticMeter.CreateHistogram<double>(
        "chemflow_nmpc_solve_ms",
        unit: "ms",
        description: "Solve duration of the Economic NMPC optimization step.");

    private readonly Meter _instanceMeter = new(ServiceName, "1.0.0");

    public ChemFlowTelemetry(Func<SupervisoryModbusStatusResponse> statusProvider)
    {
        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_running",
            () => statusProvider().IsRunning ? 1 : 0,
            description: "Whether the SIL Modbus supervisory control loop is currently active (1=running, 0=stopped).");

        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_plant_temp_c",
            () => statusProvider().LatestTick?.PlantTempC ?? double.NaN,
            unit: "Cel",
            description: "Latest measured reactor temperature received over Modbus HR 34.");

        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_ekf_temp_c",
            () => statusProvider().LatestTick?.EkfTempC ?? double.NaN,
            unit: "Cel",
            description: "Latest EKF6 estimated reactor temperature.");

        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_ekf_nis",
            () => statusProvider().LatestTick?.EkfNis ?? 0.0,
            description: "Latest chi-square Normalized Innovation Squared (NIS) statistic from EKF6.");

        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_sensor_fault_active",
            () => (statusProvider().LatestTick?.SensorFaultDetected ?? false) ? 1 : 0,
            description: "Whether EKF6 chi-square NIS fault detection is currently triggered (1=fault, 0=healthy).");

        _instanceMeter.CreateObservableGauge(
            "chemflow_sil_commanded_valve_pct",
            () => statusProvider().LatestTick?.CommandedValvePct ?? 0.0,
            unit: "%",
            description: "Latest cooling valve FCV-201 setpoint commanded by NMPC.");

        _instanceMeter.CreateObservableGauge(
            "chemflow_historian_outbox_queued",
            () => statusProvider().HistorianQueuedMessages,
            unit: "messages",
            description: "Number of pending store-and-forward Historian telemetry frames in SQLite outbox.");

        _instanceMeter.CreateObservableCounter(
            "chemflow_historian_replayed_total",
            () => statusProvider().HistorianReplayedMessages,
            unit: "messages",
            description: "Total Historian frames replayed from the SQLite outbox with Idempotency-Key.");

        _instanceMeter.CreateObservableCounter(
            "chemflow_historian_published_total",
            () => statusProvider().HistorianMeasurementsPublished,
            unit: "measurements",
            description: "Total individual telemetry measurements published from ChemFlow SIL to Historian.");
    }

    public static void RecordTick(SupervisoryModbusTickDto tick)
    {
        SilTicksTotal.Add(1,
            new KeyValuePair<string, object?>("engine_mode", tick.EngineMode),
            new KeyValuePair<string, object?>("fault_detected", tick.SensorFaultDetected));
        ModbusRttMs.Record(tick.ModbusRttMs);
        NmpcSolveMs.Record(tick.NmpcSolveMs);
    }

    public void Dispose()
    {
        _instanceMeter.Dispose();
    }
}
