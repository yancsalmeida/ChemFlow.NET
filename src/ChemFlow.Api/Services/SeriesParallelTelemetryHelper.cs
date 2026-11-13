using ChemFlow.Api.Contracts;
using ChemFlow.Domain.Native;

namespace ChemFlow.Api.Services;

/// <summary>
/// Shared helper methods for executing series-parallel CSTR transient trajectories
/// (A -> B -> C, A -> D with dynamic cooling jacket Tj) and mapping native/managed states to API/SignalR DTOs.
/// </summary>
public static class SeriesParallelTelemetryHelper
{
    public static string GetEngineModeLabel() =>
        NativeReactorCoreInterop.IsNativeAvailable
            ? $"Native C11 ({NativeReactorCoreInterop.VersionString})"
            : "Managed C# RK4";

    public static CstrSeriesParallelParamsFFI BuildParameters(SeriesParallelStreamRequest request)
    {
        CstrSeriesParallelParamsFFI defaults = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();
        return defaults with
        {
            VolumetricFlowLPerMin = Math.Max(1.0, request.VolumetricFlowLPerMin),
            VolumeLiters = Math.Max(1.0, request.ReactorVolumeLiters),
            CaFeedMolPerL = Math.Max(0.05, request.FeedCaMolPerL),
            CbFeedMolPerL = Math.Max(0.0, request.FeedCbMolPerL),
            CcFeedMolPerL = 0.0,
            CdFeedMolPerL = 0.0,
            TFeedKelvin = Math.Clamp(request.FeedTemperatureKelvin, 220.0, 700.0),
            JacketTInKelvin = Math.Clamp(request.JacketInletTemperatureKelvin, 200.0, 500.0),
            UaJPerMinK = Math.Max(0.0, request.CoolingUaWattsPerKelvin) * 60.0
        };
    }

    public static SeriesParallelTelemetryFrameDto MapStateToFrame(
        int frameIndex,
        double timeMinutes,
        double maxSafeTempK,
        in CstrSeriesParallelStateFFI state,
        string engineMode)
    {
        double warningThreshold = maxSafeTempK - 20.0;
        string status = state.IsThermalRunaway
            ? "CRITICAL_RUNAWAY"
            : (state.TempKelvin >= warningThreshold ? "WARNING_HIGH_TEMP" : "NORMAL");

        return new SeriesParallelTelemetryFrameDto(
            FrameIndex: frameIndex,
            TimeSeconds: Math.Round(timeMinutes * 60.0, 2),
            CaMolPerL: Math.Round(state.CaMolPerL, 4),
            CbMolPerL: Math.Round(state.CbMolPerL, 4),
            CcMolPerL: Math.Round(state.CcMolPerL, 4),
            CdMolPerL: Math.Round(state.CdMolPerL, 4),
            TemperatureKelvin: Math.Round(state.TempKelvin, 2),
            TemperatureCelsius: Math.Round(state.TempKelvin - 273.15, 2),
            JacketTemperatureKelvin: Math.Round(state.JacketTempKelvin, 2),
            JacketTemperatureCelsius: Math.Round(state.JacketTempKelvin - 273.15, 2),
            ConversionAPct: Math.Round(state.ConversionAPct, 2),
            YieldBPct: Math.Round(state.YieldBPct, 2),
            SelectivityBPct: Math.Round(state.SelectivityBPct, 2),
            RateR1MolPerLMin: Math.Round(state.RateR1MolPerLMin, 4),
            RateR2MolPerLMin: Math.Round(state.RateR2MolPerLMin, 4),
            RateR3MolPerLMin: Math.Round(state.RateR3MolPerLMin, 4),
            HeatGeneratedWatts: Math.Round(state.HeatGeneratedWatts, 1),
            HeatRemovedWatts: Math.Round(state.HeatRemovedWatts, 1),
            IsThermalRunaway: state.IsThermalRunaway,
            Status: status,
            EngineMode: engineMode
        );
    }

    public static List<SeriesParallelTelemetryFrameDto> SimulateTrajectory(
        SeriesParallelStreamRequest request,
        int steps)
    {
        CstrSeriesParallelParamsFFI p = BuildParameters(request);
        double dt = Math.Clamp(request.TimeStepMinutes, 0.001, 0.25);
        int boundedSteps = Math.Clamp(steps, 1, 1000);
        double maxSafeTempK = Math.Max(300.0, request.MaxSafeTemperatureKelvin);

        double ca = p.CaFeedMolPerL;
        double cb = p.CbFeedMolPerL;
        double cc = 0.0;
        double cd = 0.0;
        double temp = p.TFeedKelvin;
        double tempJ = p.JacketTInKelvin;
        string engineMode = GetEngineModeLabel();

        var frames = new List<SeriesParallelTelemetryFrameDto>(boundedSteps);
        for (int step = 1; step <= boundedSteps; step++)
        {
            // Keep substeps locally safe if runaway rates stiffen ODE eigenvalues
            int subSteps = temp > 430.0 ? 8 : 2;
            double subDt = dt / subSteps;
            CstrSeriesParallelStateFFI state = default;
            for (int s = 0; s < subSteps; s++)
            {
                NativeReactorCoreInterop.StepSeriesParallelDetailed(
                    in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, subDt, maxSafeTempK, out state);
            }

            frames.Add(MapStateToFrame(step, step * dt, maxSafeTempK, in state, engineMode));
        }

        return frames;
    }
}
