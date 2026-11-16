using System.Runtime.InteropServices;

namespace ChemFlow.Domain.Native;

/// <summary>
/// C-ABI compatible Plain Old Data struct matching CstrParamsFFI in ReactorCore.C.
/// Models the standard continuous exothermic CSTR with cooling jacket.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CstrParamsFFI
{
    public double VolumetricFlowLPerMin;  // q: Volumetric flow rate (L/min)
    public double VolumeLiters;             // V: Reactor fluid volume (L)
    public double K0PerMin;                // k0: Arrhenius frequency factor (1/min)
    public double EaOverRKelvin;          // Ea/R: Activation energy / R (Kelvin)
    public double DeltaHJPerMol;         // -dH: Heat of reaction (J/mol)
    public double RhoCpJPerLK;            // rho*Cp: Fluid density * heat capacity (J/(L·K))
    public double UaJPerMinK;             // UA: Heat transfer area * coefficient (J/(min·K))
    public double CaFeedMolPerL;         // CA0: Feed reactant concentration (mol/L)
    public double TFeedKelvin;             // T0: Feed temperature (K)
    public double TCoolantKelvin;          // Tc: Jacket coolant temperature (K)
}

/// <summary>
/// Detailed CSTR state information matching CstrStateFFI in ReactorCore.C.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CstrStateFFI
{
    public double CaMolPerL;
    public double TempKelvin;
    public double ConversionPct;
    public double ReactionRateMolPerLMin;
    public byte IsRunawayByte;

    public bool IsRunaway
    {
        readonly get => IsRunawayByte != 0;
        init => IsRunawayByte = (byte)(value ? 1 : 0);
    }
}

/// <summary>
/// Diagnostic statistics matching CstrAdaptiveStatsFFI in ReactorCore.C.
/// Tracks accepted/rejected step counts and local truncation error norms.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CstrAdaptiveStatsFFI
{
    public nuint StepsAccepted;
    public nuint StepsRejected;
    public double FinalH;
    public double MinH;
    public double MaxLocalError;
}

/// <summary>
/// C-ABI compatible Plain Old Data struct matching CstrSeriesParallelParamsFFI in ReactorCore.C.
/// Models complex series-parallel kinetics (A -> B -> C and A -> D) with dynamic jacket cooling.
/// Total size: 176 bytes (22 x 64-bit IEEE-754 doubles).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CstrSeriesParallelParamsFFI
{
    public double VolumetricFlowLPerMin;
    public double VolumeLiters;
    public double RhoCpJPerLK;

    public double K0_1PerMin;
    public double Ea1OverRKelvin;
    public double DeltaH1JPerMol;

    public double K0_2PerMin;
    public double Ea2OverRKelvin;
    public double DeltaH2JPerMol;

    public double K0_3PerMin;
    public double Ea3OverRKelvin;
    public double DeltaH3JPerMol;

    public double CaFeedMolPerL;
    public double CbFeedMolPerL;
    public double CcFeedMolPerL;
    public double CdFeedMolPerL;
    public double TFeedKelvin;

    public double JacketVolumeLiters;
    public double JacketFlowLPerMin;
    public double JacketRhoCpJPerLK;
    public double JacketTInKelvin;
    public double UaJPerMinK;
}

/// <summary>
/// Detailed state information matching CstrSeriesParallelStateFFI in ReactorCore.C.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct CstrSeriesParallelStateFFI
{
    public double CaMolPerL;
    public double CbMolPerL;
    public double CcMolPerL;
    public double CdMolPerL;
    public double TempKelvin;
    public double JacketTempKelvin;
    public double ConversionAPct;
    public double YieldBPct;
    public double SelectivityBPct;
    public double RateR1MolPerLMin;
    public double RateR2MolPerLMin;
    public double RateR3MolPerLMin;
    public double HeatGeneratedWatts;
    public double HeatRemovedWatts;
    public byte IsThermalRunawayByte;

    public bool IsThermalRunaway
    {
        readonly get => IsThermalRunawayByte != 0;
        init => IsThermalRunawayByte = (byte)(value ? 1 : 0);
    }
}

/// <summary>
/// High-performance native C-ABI Foreign Function Interface (FFI) bridge
/// connecting ChemFlow.NET (.NET 8) with the ReactorCore.C (C11) numerical engine.
/// Includes automatic shared library resolution and managed analytical RK4 fallback.
/// </summary>
public static class NativeReactorCoreInterop
{
    private const string LibraryName = "reactorcore";

    public static bool IsNativeAvailable { get; }
    public static bool HasNativeSimdSupport { get; }
    public static string VersionString { get; }

    /// <summary>
    /// Returns the compiled SIMD instruction width: 512 for AVX-512, 256 for AVX2, 0 for scalar.
    /// </summary>
    public static int SimdLevel
    {
        get
        {
            if (!IsNativeAvailable) return 0;
            try { return NativeMethods.reactor_core_simd_level(); }
            catch { return 0; }
        }
    }

    static NativeReactorCoreInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeReactorCoreInterop).Assembly, ResolveDllImport);

        try
        {
            IntPtr ptr = NativeMethods.reactor_core_version();
            VersionString = Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
            IsNativeAvailable = VersionString.Contains("ReactorCore.C", StringComparison.OrdinalIgnoreCase);
            HasNativeSimdSupport = IsNativeAvailable && (NativeMethods.reactor_core_has_simd() == 1);
        }
        catch
        {
            IsNativeAvailable = false;
            HasNativeSimdSupport = false;
            VersionString = "Managed C# Fallback Engine (libreactorcore.so not found)";
        }
    }

    private static IntPtr ResolveDllImport(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName && libraryName != "libreactorcore.so")
            return IntPtr.Zero;

        // 1. Explicit environment variable override
        string? envPath = Environment.GetEnvironmentVariable("REACTOR_CORE_LIB_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
        {
            if (NativeLibrary.TryLoad(envPath, out IntPtr handle))
                return handle;
        }

        // 2. Candidate probe paths
        string[] candidates =
        [
            "/usr/local/lib/libreactorcore.so",
            "/home/uuu/Documents/Projetos/ReactorCore.C/libreactorcore.so",
            Path.Combine(AppContext.BaseDirectory, "libreactorcore.so"),
            Path.Combine(AppContext.BaseDirectory, "../../../../ReactorCore.C/libreactorcore.so"),
            Path.Combine(AppContext.BaseDirectory, "../../../../../ReactorCore.C/libreactorcore.so"),
            Path.Combine(AppContext.BaseDirectory, "../../ReactorCore.C/libreactorcore.so")
        ];

        foreach (string candidate in candidates)
        {
            try
            {
                string fullPath = Path.GetFullPath(candidate);
                if (File.Exists(fullPath) && NativeLibrary.TryLoad(fullPath, out IntPtr handle))
                {
                    return handle;
                }
            }
            catch
            {
                // Continue searching candidate paths
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Populates parameters with Fogler literature benchmark defaults.
    /// </summary>
    public static CstrParamsFFI GetDefaultParameters()
    {
        if (IsNativeAvailable)
        {
            try
            {
                var p = new CstrParamsFFI();
                if (NativeMethods.reactor_cstr_default_params(ref p) == 0)
                    return p;
            }
            catch { }
        }

        return new CstrParamsFFI
        {
            VolumetricFlowLPerMin = 100.0,
            VolumeLiters = 100.0,
            K0PerMin = 7.08e10,
            EaOverRKelvin = 8330.1,
            DeltaHJPerMol = 200000.0,
            RhoCpJPerLK = 4000.0,
            UaJPerMinK = 40000.0,
            CaFeedMolPerL = 1.0,
            TFeedKelvin = 350.0,
            TCoolantKelvin = 300.0
        };
    }

    /// <summary>
    /// Evaluates the Arrhenius reaction rate r(Ca, T) = k0 * exp(-Ea / (R*T)) * Ca.
    /// </summary>
    public static double ReactionRate(in CstrParamsFFI parameters, double ca, double tempK)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_reaction_rate(in parameters, ca, tempK);
            }
            catch { }
        }

        if (ca <= 0.0 || tempK <= 0.0) return 0.0;
        double k = parameters.K0PerMin * Math.Exp(-parameters.EaOverRKelvin / tempK);
        return k * ca;
    }

    /// <summary>
    /// Advances reactor state by a single RK4 step of length dt (minutes).
    /// </summary>
    public static int StepRk4(in CstrParamsFFI parameters, ref double ca, ref double tempK, double dt)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_step_rk4(in parameters, ref ca, ref tempK, dt);
            }
            catch { }
        }

        return ManagedStepRk4(in parameters, ref ca, ref tempK, dt);
    }

    /// <summary>
    /// Advances reactor state and evaluates conversion, reaction rate, and runaway status.
    /// </summary>
    public static int StepDetailed(
        in CstrParamsFFI parameters,
        ref double ca,
        ref double tempK,
        double dt,
        double runawayThresholdK,
        out CstrStateFFI outState)
    {
        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_step_detailed(in parameters, ref ca, ref tempK, dt, runawayThresholdK, out outState);
                if (rc == 0) return 0;
            }
            catch { }
        }

        int managedRc = ManagedStepRk4(in parameters, ref ca, ref tempK, dt);
        double conversion = parameters.CaFeedMolPerL > 0.0
            ? Math.Max(0.0, Math.Min(100.0, (1.0 - (ca / parameters.CaFeedMolPerL)) * 100.0))
            : 0.0;
        double rate = ReactionRate(in parameters, ca, tempK);

        outState = new CstrStateFFI
        {
            CaMolPerL = ca,
            TempKelvin = tempK,
            ConversionPct = conversion,
            ReactionRateMolPerLMin = rate,
            IsRunaway = tempK >= runawayThresholdK
        };

        return managedRc;
    }

    /// <summary>
    /// Simulates a full continuous batch trajectory over multiple RK4 steps.
    /// </summary>
    public static int SimulateTrajectory(
        in CstrParamsFFI parameters,
        double initialCa,
        double initialTempK,
        double dt,
        int steps,
        double[] outTime,
        double[] outCa,
        double[] outTemp)
    {
        if (outTime.Length < steps + 1 || outCa.Length < steps + 1 || outTemp.Length < steps + 1)
            throw new ArgumentException("Output arrays must have length at least steps + 1.");

        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_simulate_trajectory(
                    in parameters, initialCa, initialTempK, dt, (nuint)steps, outTime, outCa, outTemp);
            }
            catch { }
        }

        // Managed trajectory solver
        outTime[0] = 0.0;
        outCa[0] = initialCa;
        outTemp[0] = initialTempK;

        double curCa = initialCa;
        double curTemp = initialTempK;
        double t = 0.0;

        for (int i = 1; i <= steps; i++)
        {
            ManagedStepRk4(in parameters, ref curCa, ref curTemp, dt);
            t += dt;
            outTime[i] = t;
            outCa[i] = curCa;
            outTemp[i] = curTemp;
        }

        return 0;
    }

    /// <summary>
    /// Advances reactor state across interval dt using adaptive RKF45 with local error control.
    /// Uses default tolerances (rtol = 1e-5, atol = 1e-7).
    /// </summary>
    public static int StepAdaptive(
        in CstrParamsFFI parameters,
        ref double ca,
        ref double tempK,
        double dt,
        out CstrAdaptiveStatsFFI stats)
    {
        return StepAdaptive(in parameters, ref ca, ref tempK, dt, 1e-5, 1e-7, out stats);
    }

    /// <summary>
    /// Advances reactor state across interval dt using adaptive RKF45 with local error control.
    /// Dynamically contracts step size during rapid thermal transients and expands during steady state.
    /// </summary>
    public static int StepAdaptive(
        in CstrParamsFFI parameters,
        ref double ca,
        ref double tempK,
        double dt,
        double rtol,
        double atol,
        out CstrAdaptiveStatsFFI stats)
    {
        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_step_adaptive(in parameters, ref ca, ref tempK, dt, rtol, atol, out stats);
                if (rc == 0) return 0;
            }
            catch { }
        }

        return ManagedStepAdaptive(in parameters, ref ca, ref tempK, dt, rtol, atol, out stats);
    }

    /// <summary>
    /// Advances reactor state using adaptive RKF45 and returns detailed reaction diagnostics.
    /// Uses default tolerances (rtol = 1e-5, atol = 1e-7).
    /// </summary>
    public static int StepDetailedAdaptive(
        in CstrParamsFFI parameters,
        ref double ca,
        ref double tempK,
        double dt,
        double runawayThresholdK,
        out CstrStateFFI outState,
        out CstrAdaptiveStatsFFI outStats)
    {
        return StepDetailedAdaptive(in parameters, ref ca, ref tempK, dt, runawayThresholdK, 1e-5, 1e-7, out outState, out outStats);
    }

    /// <summary>
    /// Advances reactor state using adaptive RKF45 and returns detailed reaction diagnostics.
    /// </summary>
    public static int StepDetailedAdaptive(
        in CstrParamsFFI parameters,
        ref double ca,
        ref double tempK,
        double dt,
        double runawayThresholdK,
        double rtol,
        double atol,
        out CstrStateFFI outState,
        out CstrAdaptiveStatsFFI outStats)
    {
        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_step_detailed_adaptive(
                    in parameters, ref ca, ref tempK, dt, rtol, atol, runawayThresholdK, out outState, out outStats);
                if (rc == 0) return 0;
            }
            catch { }
        }

        int managedRc = ManagedStepAdaptive(in parameters, ref ca, ref tempK, dt, rtol, atol, out outStats);
        double conversion = parameters.CaFeedMolPerL > 0.0
            ? Math.Max(0.0, Math.Min(100.0, (1.0 - (ca / parameters.CaFeedMolPerL)) * 100.0))
            : 0.0;
        double rate = ReactionRate(in parameters, ca, tempK);

        outState = new CstrStateFFI
        {
            CaMolPerL = ca,
            TempKelvin = tempK,
            ConversionPct = conversion,
            ReactionRateMolPerLMin = rate,
            IsRunaway = tempK >= runawayThresholdK
        };

        return managedRc;
    }

    /// <summary>
    /// Simulates a batch trajectory using adaptive RKF45 error control with default tolerances.
    /// </summary>
    public static int SimulateAdaptiveTrajectory(
        in CstrParamsFFI parameters,
        double initialCa,
        double initialTempK,
        double dt,
        int steps,
        double[] outTime,
        double[] outCa,
        double[] outTemp,
        out CstrAdaptiveStatsFFI outStats)
    {
        return SimulateAdaptiveTrajectory(in parameters, initialCa, initialTempK, dt, steps, 1e-5, 1e-7, outTime, outCa, outTemp, out outStats);
    }

    /// <summary>
    /// Simulates a batch trajectory using adaptive RKF45 error control.
    /// </summary>
    public static int SimulateAdaptiveTrajectory(
        in CstrParamsFFI parameters,
        double initialCa,
        double initialTempK,
        double dt,
        int steps,
        double rtol,
        double atol,
        double[] outTime,
        double[] outCa,
        double[] outTemp,
        out CstrAdaptiveStatsFFI outStats)
    {
        if (outTime.Length < steps + 1 || outCa.Length < steps + 1 || outTemp.Length < steps + 1)
            throw new ArgumentException("Output arrays must have length at least steps + 1.");

        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_simulate_adaptive_trajectory(
                    in parameters, initialCa, initialTempK, dt, (nuint)steps, rtol, atol, outTime, outCa, outTemp, out outStats);
            }
            catch { }
        }

        // Managed adaptive trajectory solver
        outTime[0] = 0.0;
        outCa[0] = initialCa;
        outTemp[0] = initialTempK;

        double curCa = initialCa;
        double curTemp = initialTempK;
        double t = 0.0;

        nuint totalAcc = 0;
        nuint totalRej = 0;
        double minH = double.MaxValue;
        double maxErr = 0.0;
        double lastH = dt / 10.0;

        for (int i = 1; i <= steps; i++)
        {
            ManagedStepAdaptive(in parameters, ref curCa, ref curTemp, dt, rtol, atol, out CstrAdaptiveStatsFFI stepStats);
            t += dt;
            outTime[i] = t;
            outCa[i] = curCa;
            outTemp[i] = curTemp;

            totalAcc += stepStats.StepsAccepted;
            totalRej += stepStats.StepsRejected;
            if (stepStats.MinH < minH) minH = stepStats.MinH;
            if (stepStats.MaxLocalError > maxErr) maxErr = stepStats.MaxLocalError;
            lastH = stepStats.FinalH;
        }

        outStats = new CstrAdaptiveStatsFFI
        {
            StepsAccepted = totalAcc,
            StepsRejected = totalRej,
            FinalH = lastH,
            MinH = minH == double.MaxValue ? dt : minH,
            MaxLocalError = maxErr
        };

        return 0;
    }

    /// <summary>
    /// Advances an ensemble of N CSTR reactors simultaneously by a single RK4 step of length dt (min).
    /// Utilizes AVX2 and FMA SIMD parallelization when native engine is available.
    /// Updates caArray and tempKArray in-place.
    /// </summary>
    public static int StepBatchRk4(
        in CstrParamsFFI parameters,
        double[] caArray,
        double[] tempKArray,
        double dt)
    {
        ArgumentNullException.ThrowIfNull(caArray);
        ArgumentNullException.ThrowIfNull(tempKArray);
        if (caArray.Length != tempKArray.Length)
            throw new ArgumentException("Concentration and temperature arrays must have equal length.");
        if (caArray.Length == 0 || dt <= 0.0) return -1;

        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_batch_step_rk4(in parameters, caArray, tempKArray, (nuint)caArray.Length, dt);
                if (rc == 0) return 0;
            }
            catch { }
        }

        // Managed SIMD/Parallel fallback
        return ManagedStepBatchRk4(in parameters, caArray, tempKArray, dt);
    }

    /// <summary>
    /// Advances an ensemble of N CSTR reactors simultaneously and evaluates detailed telemetry metrics
    /// (conversion %, reaction rate, runaway status) for each individual reactor.
    /// </summary>
    public static int StepBatchDetailed(
        in CstrParamsFFI parameters,
        double[] caArray,
        double[] tempKArray,
        double dt,
        double runawayThresholdK,
        CstrStateFFI[] outStates)
    {
        ArgumentNullException.ThrowIfNull(caArray);
        ArgumentNullException.ThrowIfNull(tempKArray);
        ArgumentNullException.ThrowIfNull(outStates);
        if (caArray.Length != tempKArray.Length || outStates.Length < caArray.Length)
            throw new ArgumentException("Arrays must have matching dimensions.");
        if (caArray.Length == 0 || dt <= 0.0) return -1;

        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_batch_step_detailed(
                    in parameters, caArray, tempKArray, (nuint)caArray.Length, dt, runawayThresholdK, outStates);
                if (rc == 0) return 0;
            }
            catch { }
        }

        return ManagedStepBatchDetailed(in parameters, caArray, tempKArray, dt, runawayThresholdK, outStates);
    }

    /// <summary>
    /// Simulates high-speed batch dynamic trajectories for an ensemble of N reactors simultaneously.
    /// Memory layouts:
    ///   outTime: length steps + 1
    ///   outCa, outTemp: length (steps + 1) * nReactors
    /// </summary>
    public static int SimulateBatchTrajectory(
        in CstrParamsFFI parameters,
        double[] initialCaArray,
        double[] initialTempKArray,
        double dt,
        int steps,
        double[] outTime,
        double[] outCa,
        double[] outTemp)
    {
        ArgumentNullException.ThrowIfNull(initialCaArray);
        ArgumentNullException.ThrowIfNull(initialTempKArray);
        ArgumentNullException.ThrowIfNull(outTime);
        ArgumentNullException.ThrowIfNull(outCa);
        ArgumentNullException.ThrowIfNull(outTemp);

        int n = initialCaArray.Length;
        if (initialTempKArray.Length != n)
            throw new ArgumentException("Initial states arrays must have equal length.");
        if (outTime.Length < steps + 1)
            throw new ArgumentException("outTime length must be at least steps + 1.");
        if (outCa.Length < (steps + 1) * n || outTemp.Length < (steps + 1) * n)
            throw new ArgumentException("Trajectory output arrays must have length at least (steps + 1) * nReactors.");

        if (IsNativeAvailable)
        {
            try
            {
                int rc = NativeMethods.reactor_cstr_batch_simulate_trajectory(
                    in parameters, initialCaArray, initialTempKArray, (nuint)n, dt, (nuint)steps, outTime, outCa, outTemp);
                if (rc == 0) return 0;
            }
            catch { }
        }

        return ManagedSimulateBatchTrajectory(in parameters, initialCaArray, initialTempKArray, dt, steps, outTime, outCa, outTemp);
    }

    private static int ManagedStepBatchRk4(in CstrParamsFFI p, double[] ca, double[] tempK, double dt)
    {
        for (int i = 0; i < ca.Length; i++)
        {
            double c = ca[i];
            double t = tempK[i];
            ManagedStepRk4(in p, ref c, ref t, dt);
            ca[i] = c;
            tempK[i] = t;
        }
        return 0;
    }

    private static int ManagedStepBatchDetailed(
        in CstrParamsFFI p,
        double[] ca,
        double[] tempK,
        double dt,
        double runawayThresholdK,
        CstrStateFFI[] outStates)
    {
        for (int i = 0; i < ca.Length; i++)
        {
            double c = ca[i];
            double t = tempK[i];
            ManagedStepRk4(in p, ref c, ref t, dt);
            ca[i] = c;
            tempK[i] = t;
            if (c < 0.0) { c = 0.0; ca[i] = 0.0; }
            double conversion = p.CaFeedMolPerL > 0.0
                ? Math.Max(0.0, Math.Min(100.0, (1.0 - (c / p.CaFeedMolPerL)) * 100.0))
                : 0.0;
            double rate = ReactionRate(in p, c, t);

            outStates[i] = new CstrStateFFI
            {
                CaMolPerL = c,
                TempKelvin = t,
                ConversionPct = conversion,
                ReactionRateMolPerLMin = rate,
                IsRunaway = t >= runawayThresholdK
            };
        }
        return 0;
    }

    private static int ManagedSimulateBatchTrajectory(
        in CstrParamsFFI p,
        double[] ca0,
        double[] temp0,
        double dt,
        int steps,
        double[] outTime,
        double[] outCa,
        double[] outTemp)
    {
        int n = ca0.Length;
        outTime[0] = 0.0;
        for (int j = 0; j < n; j++)
        {
            outCa[j] = ca0[j];
            outTemp[j] = temp0[j];
        }

        double[] curCa = (double[])ca0.Clone();
        double[] curTemp = (double[])temp0.Clone();

        for (int s = 1; s <= steps; s++)
        {
            ManagedStepBatchRk4(in p, curCa, curTemp, dt);
            outTime[s] = s * dt;
            int offset = s * n;
            for (int j = 0; j < n; j++)
            {
                outCa[offset + j] = curCa[j];
                outTemp[offset + j] = curTemp[j];
            }
        }
        return 0;
    }

    private static int ManagedStepRk4(in CstrParamsFFI p, ref double ca, ref double tempK, double dt)
    {
        if (dt <= 0.0) return -1;

        double y0 = ca;
        double y1 = tempK;

        // k1
        EvaluateDerivatives(in p, y0, y1, out double dy0_1, out double dy1_1);

        // k2
        EvaluateDerivatives(in p, y0 + 0.5 * dt * dy0_1, y1 + 0.5 * dt * dy1_1, out double dy0_2, out double dy1_2);

        // k3
        EvaluateDerivatives(in p, y0 + 0.5 * dt * dy0_2, y1 + 0.5 * dt * dy1_2, out double dy0_3, out double dy1_3);

        // k4
        EvaluateDerivatives(in p, y0 + dt * dy0_3, y1 + dt * dy1_3, out double dy0_4, out double dy1_4);

        // Step
        ca = y0 + (dt / 6.0) * (dy0_1 + 2.0 * dy0_2 + 2.0 * dy0_3 + dy0_4);
        tempK = y1 + (dt / 6.0) * (dy1_1 + 2.0 * dy1_2 + 2.0 * dy1_3 + dy1_4);

        if (ca < 0.0) ca = 0.0;
        if (tempK < 50.0) tempK = 50.0;

        return 0;
    }

    private static void EvaluateDerivatives(
        in CstrParamsFFI p,
        double ca,
        double t,
        out double dCaDt,
        out double dTdt)
    {
        double safeCa = Math.Max(0.0, ca);
        double safeT = Math.Max(50.0, t);

        double k = p.K0PerMin * Math.Exp(-p.EaOverRKelvin / safeT);
        double r = k * safeCa;

        double spaceTimeInv = p.VolumetricFlowLPerMin / p.VolumeLiters;
        double heatGenCoeff = p.DeltaHJPerMol / p.RhoCpJPerLK;
        double heatRemCoeff = p.UaJPerMinK / (p.VolumeLiters * p.RhoCpJPerLK);

        dCaDt = spaceTimeInv * (p.CaFeedMolPerL - safeCa) - r;
        dTdt = spaceTimeInv * (p.TFeedKelvin - safeT)
               + heatGenCoeff * r
               - heatRemCoeff * (safeT - p.TCoolantKelvin);
    }

    private static int ManagedStepAdaptive(
        in CstrParamsFFI p,
        ref double ca,
        ref double tempK,
        double dt,
        double rtol,
        double atol,
        out CstrAdaptiveStatsFFI stats)
    {
        if (dt <= 0.0)
        {
            stats = default;
            return -1;
        }

        if (rtol <= 0.0) rtol = 1e-5;
        if (atol <= 0.0) atol = 1e-7;

        double curCa = ca;
        double curTemp = tempK;
        double t = 0.0;
        double h = dt / 10.0;
        double hMin = 1e-9;
        double hMax = dt;
        const double safety = 0.90;

        nuint accepted = 0;
        nuint rejected = 0;
        double minH = double.MaxValue;
        double maxLocalErr = 0.0;
        double finalH = h;

        while (t < dt - 1e-12 * dt)
        {
            if (t + h > dt)
                h = dt - t;

            if (h < hMin) h = hMin;

            // Evaluate RKF45 stages
            // k1
            EvaluateDerivatives(in p, curCa, curTemp, out double k1_ca, out double k1_t);

            // k2: t + h/4, y + h/4 * k1
            EvaluateDerivatives(in p, curCa + h * 0.25 * k1_ca, curTemp + h * 0.25 * k1_t, out double k2_ca, out double k2_t);

            // k3: t + 3/8*h, y + h*(3/32*k1 + 9/32*k2)
            EvaluateDerivatives(in p,
                curCa + h * ((3.0 / 32.0) * k1_ca + (9.0 / 32.0) * k2_ca),
                curTemp + h * ((3.0 / 32.0) * k1_t + (9.0 / 32.0) * k2_t),
                out double k3_ca, out double k3_t);

            // k4: t + 12/13*h
            EvaluateDerivatives(in p,
                curCa + h * ((1932.0 / 2197.0) * k1_ca - (7200.0 / 2197.0) * k2_ca + (7296.0 / 2197.0) * k3_ca),
                curTemp + h * ((1932.0 / 2197.0) * k1_t - (7200.0 / 2197.0) * k2_t + (7296.0 / 2197.0) * k3_t),
                out double k4_ca, out double k4_t);

            // k5: t + h
            EvaluateDerivatives(in p,
                curCa + h * ((439.0 / 216.0) * k1_ca - 8.0 * k2_ca + (3680.0 / 513.0) * k3_ca - (845.0 / 4104.0) * k4_ca),
                curTemp + h * ((439.0 / 216.0) * k1_t - 8.0 * k2_t + (3680.0 / 513.0) * k3_t - (845.0 / 4104.0) * k4_t),
                out double k5_ca, out double k5_t);

            // k6: t + h/2
            EvaluateDerivatives(in p,
                curCa + h * ((-8.0 / 27.0) * k1_ca + 2.0 * k2_ca - (3544.0 / 2565.0) * k3_ca + (1859.0 / 4104.0) * k4_ca - (11.0 / 40.0) * k5_ca),
                curTemp + h * ((-8.0 / 27.0) * k1_t + 2.0 * k2_t - (3544.0 / 2565.0) * k3_t + (1859.0 / 4104.0) * k4_t - (11.0 / 40.0) * k5_t),
                out double k6_ca, out double k6_t);

            // 4th order candidate
            double y4_ca = curCa + h * ((25.0 / 216.0) * k1_ca + (1408.0 / 2565.0) * k3_ca + (2197.0 / 4104.0) * k4_ca - 0.2 * k5_ca);
            double y4_t = curTemp + h * ((25.0 / 216.0) * k1_t + (1408.0 / 2565.0) * k3_t + (2197.0 / 4104.0) * k4_t - 0.2 * k5_t);

            // 5th order candidate
            double y5_ca = curCa + h * ((16.0 / 135.0) * k1_ca + (6656.0 / 12825.0) * k3_ca + (28561.0 / 56430.0) * k4_ca - (9.0 / 50.0) * k5_ca + (2.0 / 55.0) * k6_ca);
            double y5_t = curTemp + h * ((16.0 / 135.0) * k1_t + (6656.0 / 12825.0) * k3_t + (28561.0 / 56430.0) * k4_t - (9.0 / 50.0) * k5_t + (2.0 / 55.0) * k6_t);

            // RMS error norm
            double scaleCa = atol + rtol * Math.Max(Math.Abs(curCa), Math.Abs(y5_ca));
            double scaleT = atol + rtol * Math.Max(Math.Abs(curTemp), Math.Abs(y5_t));
            if (scaleCa <= 0.0) scaleCa = 1e-15;
            if (scaleT <= 0.0) scaleT = 1e-15;

            double ratioCa = (y5_ca - y4_ca) / scaleCa;
            double ratioT = (y5_t - y4_t) / scaleT;
            double errorNorm = Math.Sqrt(0.5 * (ratioCa * ratioCa + ratioT * ratioT));

            if (h < minH) minH = h;
            if (errorNorm > maxLocalErr) maxLocalErr = errorNorm;

            if (errorNorm <= 1.0)
            {
                // Step accepted
                t += h;
                curCa = Math.Max(0.0, y5_ca);
                curTemp = Math.Max(50.0, y5_t);
                accepted++;
                finalH = h;

                double factor = errorNorm == 0.0 ? 5.0 : Math.Clamp(safety * Math.Pow(errorNorm, -0.20), 0.2, 5.0);
                h = Math.Clamp(h * factor, hMin, hMax);
            }
            else
            {
                // Step rejected
                rejected++;
                if (h <= hMin)
                {
                    stats = new CstrAdaptiveStatsFFI
                    {
                        StepsAccepted = accepted,
                        StepsRejected = rejected,
                        FinalH = h,
                        MinH = minH,
                        MaxLocalError = maxLocalErr
                    };
                    return -1;
                }

                double factor = Math.Clamp(safety * Math.Pow(errorNorm, -0.20), 0.2, 0.90);
                h = Math.Max(hMin, h * factor);
            }
        }

        ca = curCa;
        tempK = curTemp;
        stats = new CstrAdaptiveStatsFFI
        {
            StepsAccepted = accepted,
            StepsRejected = rejected,
            FinalH = finalH,
            MinH = minH == double.MaxValue ? dt : minH,
            MaxLocalError = maxLocalErr
        };
        return 0;
    }

    /// <summary>
    /// Populates series-parallel parameters with literature defaults.
    /// </summary>
    public static CstrSeriesParallelParamsFFI GetSeriesParallelDefaultParameters()
    {
        if (IsNativeAvailable)
        {
            try
            {
                var p = new CstrSeriesParallelParamsFFI();
                if (NativeMethods.reactor_cstr_series_parallel_default_params(ref p) == 0)
                    return p;
            }
            catch { }
        }

        return new CstrSeriesParallelParamsFFI
        {
            VolumetricFlowLPerMin = 100.0,
            VolumeLiters = 100.0,
            RhoCpJPerLK = 4000.0,
            K0_1PerMin = 7.08e10,
            Ea1OverRKelvin = 8330.1,
            DeltaH1JPerMol = 200000.0,
            K0_2PerMin = 3.50e9,
            Ea2OverRKelvin = 7500.0,
            DeltaH2JPerMol = 120000.0,
            K0_3PerMin = 1.20e9,
            Ea3OverRKelvin = 9000.0,
            DeltaH3JPerMol = 150000.0,
            CaFeedMolPerL = 1.0,
            CbFeedMolPerL = 0.0,
            CcFeedMolPerL = 0.0,
            CdFeedMolPerL = 0.0,
            TFeedKelvin = 350.0,
            JacketVolumeLiters = 25.0,
            JacketFlowLPerMin = 50.0,
            JacketRhoCpJPerLK = 4184.0,
            JacketTInKelvin = 295.0,
            UaJPerMinK = 40000.0
        };
    }

    /// <summary>
    /// Calculates instantaneous reaction rates for series-parallel kinetics:
    /// r1 = k1*Ca, r2 = k2*Cb, r3 = k3*Ca.
    /// </summary>
    public static void SeriesParallelRates(
        in CstrSeriesParallelParamsFFI p,
        double ca,
        double cb,
        double tempK,
        out double r1,
        out double r2,
        out double r3)
    {
        if (IsNativeAvailable)
        {
            try
            {
                if (NativeMethods.reactor_cstr_series_parallel_rates(in p, ca, cb, tempK, out r1, out r2, out r3) == 0)
                    return;
            }
            catch { }
        }

        ManagedSeriesParallelRates(in p, ca, cb, tempK, out r1, out r2, out r3);
    }

    /// <summary>
    /// Advances state [Ca, Cb, Cc, Cd, T, Tj] by a single RK4 step.
    /// </summary>
    public static int StepSeriesParallelRk4(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_step_rk4(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
            }
            catch { }
        }

        return ManagedSeriesParallelStepRk4(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
    }

    /// <summary>
    /// Advances state and computes detailed metrics (conversion, yield, selectivity, heat rates, runaway).
    /// </summary>
    public static int StepSeriesParallelDetailed(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt,
        double runawayThresholdK,
        out CstrSeriesParallelStateFFI state)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_step_detailed(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, runawayThresholdK, out state);
            }
            catch { }
        }

        return ManagedSeriesParallelStepDetailed(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, runawayThresholdK, out state);
    }

    /// <summary>
    /// Advances single-reaction CSTR state using the L-stable 3rd-order Rosenbrock stiff solver (ROS3L) with analytical Jacobian.
    /// </summary>
    public static int StepStiffRos3(
        in CstrParamsFFI p,
        ref double ca,
        ref double temp,
        double dt)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_step_ros3(in p, ref ca, ref temp, dt);
            }
            catch { }
        }

        return ManagedStepStiffRos3(in p, ref ca, ref temp, dt);
    }

    /// <summary>
    /// Advances 6-variable series-parallel CSTR state using a single fixed L-stable ROS3L step (exact 6x6 Jacobian).
    /// </summary>
    public static int StepSeriesParallelStiffRos3(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_step_ros3(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
            }
            catch { }
        }

        return ManagedSeriesParallelStepRos3(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
    }

    /// <summary>
    /// Advances 6-variable series-parallel CSTR state using adaptive embedded ROS3L(2) stiff integration and computes detailed metrics.
    /// </summary>
    public static int StepSeriesParallelStiffDetailed(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt,
        double runawayThresholdK,
        out CstrSeriesParallelStateFFI state,
        double rtol = 1e-5,
        double atol = 1e-7)
    {
        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_step_ros3_detailed(
                    in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, rtol, atol, runawayThresholdK, out state);
            }
            catch { }
        }

        return ManagedSeriesParallelStepRos3Detailed(
            in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt, rtol, atol, runawayThresholdK, out state);
    }

    /// <summary>
    /// Advances an ensemble of N series-parallel reactors simultaneously via SIMD AVX-512/AVX2 vectorization.
    /// </summary>
    public static int StepSeriesParallelBatchRk4(
        in CstrSeriesParallelParamsFFI p,
        double[] ca,
        double[] cb,
        double[] cc,
        double[] cd,
        double[] temp,
        double[] tempJ,
        double dt)
    {
        int n = ca.Length;
        if (cb.Length != n || cc.Length != n || cd.Length != n || temp.Length != n || tempJ.Length != n || n == 0 || dt <= 0.0)
            return -1;

        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_batch_step_rk4(in p, ca, cb, cc, cd, temp, tempJ, (nuint)n, dt);
            }
            catch { }
        }

        for (int i = 0; i < n; i++)
        {
            ManagedSeriesParallelStepRk4(in p, ref ca[i], ref cb[i], ref cc[i], ref cd[i], ref temp[i], ref tempJ[i], dt);
        }
        return 0;
    }

    /// <summary>
    /// Advances an ensemble of N series-parallel reactors and evaluates detailed telemetry.
    /// </summary>
    public static int StepSeriesParallelBatchDetailed(
        in CstrSeriesParallelParamsFFI p,
        double[] ca,
        double[] cb,
        double[] cc,
        double[] cd,
        double[] temp,
        double[] tempJ,
        double dt,
        double runawayThresholdK,
        CstrSeriesParallelStateFFI[] outStates)
    {
        int n = ca.Length;
        if (cb.Length != n || cc.Length != n || cd.Length != n || temp.Length != n || tempJ.Length != n || outStates.Length != n || n == 0 || dt <= 0.0)
            return -1;

        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_batch_step_detailed(in p, ca, cb, cc, cd, temp, tempJ, (nuint)n, dt, runawayThresholdK, outStates);
            }
            catch { }
        }

        for (int i = 0; i < n; i++)
        {
            ManagedSeriesParallelStepDetailed(in p, ref ca[i], ref cb[i], ref cc[i], ref cd[i], ref temp[i], ref tempJ[i], dt, runawayThresholdK, out outStates[i]);
        }
        return 0;
    }

    /// <summary>
    /// Simulates parallel dynamic trajectories for N series-parallel reactors.
    /// </summary>
    public static int SimulateSeriesParallelBatchTrajectory(
        in CstrSeriesParallelParamsFFI p,
        double[] ca0,
        double[] cb0,
        double[] cc0,
        double[] cd0,
        double[] temp0,
        double[] tempJ0,
        double dt,
        nuint steps,
        double[] outTime,
        double[] outCa,
        double[] outCb,
        double[] outCc,
        double[] outCd,
        double[] outTemp,
        double[] outTempJ)
    {
        int n = ca0.Length;
        if (cb0.Length != n || cc0.Length != n || cd0.Length != n || temp0.Length != n || tempJ0.Length != n || n == 0 || dt <= 0.0)
            return -1;

        if (IsNativeAvailable)
        {
            try
            {
                return NativeMethods.reactor_cstr_series_parallel_batch_simulate_trajectory(
                    in p, ca0, cb0, cc0, cd0, temp0, tempJ0, (nuint)n, dt, steps,
                    outTime, outCa, outCb, outCc, outCd, outTemp, outTempJ
                );
            }
            catch { }
        }

        // Managed fallback
        double[] curCa = (double[])ca0.Clone();
        double[] curCb = (double[])cb0.Clone();
        double[] curCc = (double[])cc0.Clone();
        double[] curCd = (double[])cd0.Clone();
        double[] curT = (double[])temp0.Clone();
        double[] curTj = (double[])tempJ0.Clone();

        outTime[0] = 0.0;
        for (int j = 0; j < n; j++)
        {
            outCa[j] = curCa[j];
            outCb[j] = curCb[j];
            outCc[j] = curCc[j];
            outCd[j] = curCd[j];
            outTemp[j] = curT[j];
            outTempJ[j] = curTj[j];
        }

        for (nuint s = 1; s <= steps; s++)
        {
            outTime[s] = (double)s * dt;
            int offset = (int)s * n;
            for (int j = 0; j < n; j++)
            {
                ManagedSeriesParallelStepRk4(in p, ref curCa[j], ref curCb[j], ref curCc[j], ref curCd[j], ref curT[j], ref curTj[j], dt);
                outCa[offset + j] = curCa[j];
                outCb[offset + j] = curCb[j];
                outCc[offset + j] = curCc[j];
                outCd[offset + j] = curCd[j];
                outTemp[offset + j] = curT[j];
                outTempJ[offset + j] = curTj[j];
            }
        }
        return 0;
    }

    #region Managed Series-Parallel Reference Engine

    private static void ManagedSeriesParallelRates(
        in CstrSeriesParallelParamsFFI p,
        double ca,
        double cb,
        double tempK,
        out double r1,
        out double r2,
        out double r3)
    {
        if (tempK <= 0.0)
        {
            r1 = r2 = r3 = 0.0;
            return;
        }

        double evalCa = Math.Max(0.0, ca);
        double evalCb = Math.Max(0.0, cb);
        double evalT = Math.Max(50.0, tempK);

        double k1 = p.K0_1PerMin * Math.Exp(-p.Ea1OverRKelvin / evalT);
        double k2 = p.K0_2PerMin * Math.Exp(-p.Ea2OverRKelvin / evalT);
        double k3 = p.K0_3PerMin * Math.Exp(-p.Ea3OverRKelvin / evalT);

        r1 = k1 * evalCa;
        r2 = k2 * evalCb;
        r3 = k3 * evalCa;
    }

    private static void ManagedSeriesParallelDerivatives(
        in CstrSeriesParallelParamsFFI p,
        double ca,
        double cb,
        double cc,
        double cd,
        double temp,
        double tempJ,
        out double dCa,
        out double dCb,
        out double dCc,
        out double dCd,
        out double dTemp,
        out double dTempJ)
    {
        double evalCa = Math.Max(0.0, ca);
        double evalCb = Math.Max(0.0, cb);
        double evalCc = Math.Max(0.0, cc);
        double evalCd = Math.Max(0.0, cd);
        double evalT = Math.Max(50.0, temp);
        double evalTj = Math.Max(50.0, tempJ);

        ManagedSeriesParallelRates(in p, evalCa, evalCb, evalT, out double r1, out double r2, out double r3);

        double spaceTimeInv = p.VolumetricFlowLPerMin / p.VolumeLiters;
        double qjOverVj = p.JacketFlowLPerMin / p.JacketVolumeLiters;

        double heatGen = (p.DeltaH1JPerMol * r1 + p.DeltaH2JPerMol * r2 + p.DeltaH3JPerMol * r3) / p.RhoCpJPerLK;
        double heatRemReactor = (p.UaJPerMinK / (p.VolumeLiters * p.RhoCpJPerLK)) * (evalT - evalTj);
        double heatRemJacket = (p.UaJPerMinK / (p.JacketVolumeLiters * p.JacketRhoCpJPerLK)) * (evalT - evalTj);

        dCa = spaceTimeInv * (p.CaFeedMolPerL - evalCa) - r1 - r3;
        dCb = spaceTimeInv * (p.CbFeedMolPerL - evalCb) + r1 - r2;
        dCc = spaceTimeInv * (p.CcFeedMolPerL - evalCc) + r2;
        dCd = spaceTimeInv * (p.CdFeedMolPerL - evalCd) + r3;
        dTemp = spaceTimeInv * (p.TFeedKelvin - evalT) + heatGen - heatRemReactor;
        dTempJ = qjOverVj * (p.JacketTInKelvin - evalTj) + heatRemJacket;
    }

    private static int ManagedSeriesParallelStepRk4(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt)
    {
        if (dt <= 0.0) return -1;
        double halfDt = 0.5 * dt;

        // Stage 1
        ManagedSeriesParallelDerivatives(in p, ca, cb, cc, cd, temp, tempJ,
            out double k1_ca, out double k1_cb, out double k1_cc, out double k1_cd, out double k1_t, out double k1_tj);

        // Stage 2
        ManagedSeriesParallelDerivatives(in p,
            ca + halfDt * k1_ca, cb + halfDt * k1_cb, cc + halfDt * k1_cc, cd + halfDt * k1_cd,
            temp + halfDt * k1_t, tempJ + halfDt * k1_tj,
            out double k2_ca, out double k2_cb, out double k2_cc, out double k2_cd, out double k2_t, out double k2_tj);

        // Stage 3
        ManagedSeriesParallelDerivatives(in p,
            ca + halfDt * k2_ca, cb + halfDt * k2_cb, cc + halfDt * k2_cc, cd + halfDt * k2_cd,
            temp + halfDt * k2_t, tempJ + halfDt * k2_tj,
            out double k3_ca, out double k3_cb, out double k3_cc, out double k3_cd, out double k3_t, out double k3_tj);

        // Stage 4
        ManagedSeriesParallelDerivatives(in p,
            ca + dt * k3_ca, cb + dt * k3_cb, cc + dt * k3_cc, cd + dt * k3_cd,
            temp + dt * k3_t, tempJ + dt * k3_tj,
            out double k4_ca, out double k4_cb, out double k4_cc, out double k4_cd, out double k4_t, out double k4_tj);

        double dtSix = dt / 6.0;
        ca += dtSix * (k1_ca + 2.0 * k2_ca + 2.0 * k3_ca + k4_ca);
        cb += dtSix * (k1_cb + 2.0 * k2_cb + 2.0 * k3_cb + k4_cb);
        cc += dtSix * (k1_cc + 2.0 * k2_cc + 2.0 * k3_cc + k4_cc);
        cd += dtSix * (k1_cd + 2.0 * k2_cd + 2.0 * k3_cd + k4_cd);
        temp += dtSix * (k1_t + 2.0 * k2_t + 2.0 * k3_t + k4_t);
        tempJ += dtSix * (k1_tj + 2.0 * k2_tj + 2.0 * k3_tj + k4_tj);

        if (ca < 0.0) ca = 0.0;
        if (cb < 0.0) cb = 0.0;
        if (cc < 0.0) cc = 0.0;
        if (cd < 0.0) cd = 0.0;
        if (temp < 50.0) temp = 50.0;
        if (tempJ < 50.0) tempJ = 50.0;

        return 0;
    }

    private static int ManagedSeriesParallelStepDetailed(
        in CstrSeriesParallelParamsFFI p,
        ref double ca,
        ref double cb,
        ref double cc,
        ref double cd,
        ref double temp,
        ref double tempJ,
        double dt,
        double runawayThresholdK,
        out CstrSeriesParallelStateFFI state)
    {
        int rc = ManagedSeriesParallelStepRk4(in p, ref ca, ref cb, ref cc, ref cd, ref temp, ref tempJ, dt);
        if (rc != 0)
        {
            state = default;
            return rc;
        }

        ManagedSeriesParallelRates(in p, ca, cb, temp, out double r1, out double r2, out double r3);

        double ca0 = p.CaFeedMolPerL > 1e-12 ? p.CaFeedMolPerL : 1.0;
        double convA = ((p.CaFeedMolPerL - ca) / ca0) * 100.0;
        double yieldB = ((cb - p.CbFeedMolPerL) / ca0) * 100.0;

        double totalProd = cb + cc + cd;
        double selectB = totalProd > 1e-12 ? (cb / totalProd) * 100.0 : 100.0;

        double heatGenJPerMin = p.VolumeLiters * (p.DeltaH1JPerMol * r1 + p.DeltaH2JPerMol * r2 + p.DeltaH3JPerMol * r3);
        double heatRemJPerMin = p.UaJPerMinK * (temp - tempJ);

        state = new CstrSeriesParallelStateFFI
        {
            CaMolPerL = ca,
            CbMolPerL = cb,
            CcMolPerL = cc,
            CdMolPerL = cd,
            TempKelvin = temp,
            JacketTempKelvin = tempJ,
            ConversionAPct = convA,
            YieldBPct = yieldB,
            SelectivityBPct = selectB,
            RateR1MolPerLMin = r1,
            RateR2MolPerLMin = r2,
            RateR3MolPerLMin = r3,
            HeatGeneratedWatts = heatGenJPerMin / 60.0,
            HeatRemovedWatts = heatRemJPerMin / 60.0,
            IsThermalRunaway = (temp >= runawayThresholdK)
        };

        return 0;
    }

    private const double Ros3Gamma = 0.435866521508458999416;
    private const double Ros3A21   = 2.29428036027904181;
    private const double Ros3C21   = -7.55800273184117177;
    private const double Ros3C31   = 12.22566466169111443;
    private const double Ros3C32   = 5.74019720866211269;
    private const double Ros3M1    = 0.75157658521103743;
    private const double Ros3M2    = -0.57431947473051181;
    private const double Ros3M3    = 0.38238006004650693;
    private const double Ros3E1    = -0.76819871711192689;
    private const double Ros3E2    = -0.23673864922848881;
    private const double Ros3E3    = 0.38238006004650693;

    private static int ManagedStepStiffRos3(in CstrParamsFFI p, ref double ca, ref double tempK, double dt)
    {
        if (dt <= 0.0 || p.VolumeLiters <= 0.0) return -1;
        double caV = Math.Max(0.0, ca);
        double tV = Math.Clamp(tempK, 50.0, 2500.0);

        double s = p.VolumetricFlowLPerMin / p.VolumeLiters;
        double cg = p.DeltaHJPerMol / p.RhoCpJPerLK;
        double cr = p.UaJPerMinK / (p.VolumeLiters * p.RhoCpJPerLK);
        double k = p.K0PerMin * Math.Exp(-p.EaOverRKelvin / tV);
        double drDt = caV * k * p.EaOverRKelvin / (tV * tV);

        double invGh = 1.0 / (Ros3Gamma * dt);
        double w00 = invGh - (-s - k);
        double w01 = drDt;
        double w10 = -(cg * k);
        double w11 = invGh - (-s - cr + cg * drDt);
        double det = w00 * w11 - w01 * w10;
        if (Math.Abs(det) < 1e-15 || !double.IsFinite(det)) return -1;
        double invDet = 1.0 / det;

        CstrParamsFFI prm = p;
        void Rhs2(double c, double t, out double f0, out double f1)
        {
            double cv = Math.Max(0.0, c);
            double tv = Math.Clamp(t, 50.0, 2500.0);
            double r = prm.K0PerMin * Math.Exp(-prm.EaOverRKelvin / tv) * cv;
            f0 = s * (prm.CaFeedMolPerL - cv) - r;
            f1 = s * (prm.TFeedKelvin - tv) + cg * r - cr * (tv - prm.TCoolantKelvin);
        }

        Rhs2(caV, tV, out double f10, out double f11);
        double w1_0 = (w11 * f10 - w01 * f11) * invDet;
        double w1_1 = (-w10 * f10 + w00 * f11) * invDet;

        Rhs2(caV + Ros3A21 * w1_0, tV + Ros3A21 * w1_1, out double f20, out double f21);
        double invDt = 1.0 / dt;
        double r20 = f20 + Ros3C21 * invDt * w1_0;
        double r21 = f21 + Ros3C21 * invDt * w1_1;
        double w2_0 = (w11 * r20 - w01 * r21) * invDet;
        double w2_1 = (-w10 * r20 + w00 * r21) * invDet;

        double r30 = f20 + (Ros3C31 * w1_0 + Ros3C32 * w2_0) * invDt;
        double r31 = f21 + (Ros3C31 * w1_1 + Ros3C32 * w2_1) * invDt;
        double w3_0 = (w11 * r30 - w01 * r31) * invDet;
        double w3_1 = (-w10 * r30 + w00 * r31) * invDet;

        double caNext = caV + Ros3M1 * w1_0 + Ros3M2 * w2_0 + Ros3M3 * w3_0;
        double tNext = tV + Ros3M1 * w1_1 + Ros3M2 * w2_1 + Ros3M3 * w3_1;
        if (!double.IsFinite(caNext) || !double.IsFinite(tNext)) return -1;

        ca = Math.Max(0.0, caNext);
        tempK = Math.Clamp(tNext, 50.0, 2500.0);
        return 0;
    }

    private static bool ManagedSeriesParallelRos3Single(
        in CstrSeriesParallelParamsFFI p,
        double[] xIn,
        double dt,
        double[] xOut,
        double[]? errOut)
    {
        double ca = Math.Max(0.0, xIn[0]);
        double cb = Math.Max(0.0, xIn[1]);
        double t = Math.Clamp(xIn[4], 50.0, 2500.0);

        double k1 = p.K0_1PerMin * Math.Exp(-p.Ea1OverRKelvin / t);
        double k2 = p.K0_2PerMin * Math.Exp(-p.Ea2OverRKelvin / t);
        double k3 = p.K0_3PerMin * Math.Exp(-p.Ea3OverRKelvin / t);
        double invT2 = 1.0 / (t * t);
        double dr1 = ca * k1 * p.Ea1OverRKelvin * invT2;
        double dr2 = cb * k2 * p.Ea2OverRKelvin * invT2;
        double dr3 = ca * k3 * p.Ea3OverRKelvin * invT2;

        double s = p.VolumetricFlowLPerMin / p.VolumeLiters;
        double sj = p.JacketFlowLPerMin / p.JacketVolumeLiters;
        double c1 = p.DeltaH1JPerMol / p.RhoCpJPerLK;
        double c2 = p.DeltaH2JPerMol / p.RhoCpJPerLK;
        double c3 = p.DeltaH3JPerMol / p.RhoCpJPerLK;
        double hr = p.UaJPerMinK / (p.VolumeLiters * p.RhoCpJPerLK);
        double hj = p.UaJPerMinK / (p.JacketVolumeLiters * p.JacketRhoCpJPerLK);

        double invGh = 1.0 / (Ros3Gamma * dt);
        var w = new double[6, 6];
        for (int i = 0; i < 6; i++) w[i, i] = invGh;

        w[0, 0] -= -s - k1 - k3; w[0, 4] -= -(dr1 + dr3);
        w[1, 0] -= k1; w[1, 1] -= -s - k2; w[1, 4] -= dr1 - dr2;
        w[2, 1] -= k2; w[2, 2] -= -s; w[2, 4] -= dr2;
        w[3, 0] -= k3; w[3, 3] -= -s; w[3, 4] -= dr3;
        w[4, 0] -= c1 * k1 + c3 * k3; w[4, 1] -= c2 * k2;
        w[4, 4] -= -s - hr + c1 * dr1 + c2 * dr2 + c3 * dr3; w[4, 5] -= hr;
        w[5, 4] -= hj; w[5, 5] -= -sj - hj;

        var piv = new int[6];
        for (int i = 0; i < 6; i++) piv[i] = i;
        for (int k = 0; k < 6; k++)
        {
            int maxRow = k;
            double maxVal = Math.Abs(w[k, k]);
            for (int i = k + 1; i < 6; i++)
            {
                double v = Math.Abs(w[i, k]);
                if (v > maxVal) { maxVal = v; maxRow = i; }
            }
            if (maxVal < 1e-15 || !double.IsFinite(maxVal)) return false;
            if (maxRow != k)
            {
                for (int j = 0; j < 6; j++) (w[k, j], w[maxRow, j]) = (w[maxRow, j], w[k, j]);
                (piv[k], piv[maxRow]) = (piv[maxRow], piv[k]);
            }
            double invDiag = 1.0 / w[k, k];
            for (int i = k + 1; i < 6; i++)
            {
                double mult = w[i, k] * invDiag;
                w[i, k] = mult;
                for (int j = k + 1; j < 6; j++) w[i, j] -= mult * w[k, j];
            }
        }

        double[] SolveLu(double[] b)
        {
            var res = new double[6];
            for (int i = 0; i < 6; i++)
            {
                double sum = b[piv[i]];
                for (int j = 0; j < i; j++) sum -= w[i, j] * res[j];
                res[i] = sum;
            }
            for (int i = 5; i >= 0; i--)
            {
                double sum = res[i];
                for (int j = i + 1; j < 6; j++) sum -= w[i, j] * res[j];
                res[i] = sum / w[i, i];
            }
            return res;
        }

        double[] EvalRhs(in CstrSeriesParallelParamsFFI prm, double[] xv)
        {
            double x0 = Math.Max(0.0, xv[0]), x1 = Math.Max(0.0, xv[1]);
            double x2 = Math.Max(0.0, xv[2]), x3 = Math.Max(0.0, xv[3]);
            double xt = Math.Clamp(xv[4], 50.0, 2500.0), xtj = Math.Clamp(xv[5], 50.0, 2500.0);
            ManagedSeriesParallelRates(in prm, x0, x1, xt, out double r1, out double r2, out double r3);
            return
            [
                s * (prm.CaFeedMolPerL - x0) - r1 - r3,
                s * (prm.CbFeedMolPerL - x1) + r1 - r2,
                s * (prm.CcFeedMolPerL - x2) + r2,
                s * (prm.CdFeedMolPerL - x3) + r3,
                s * (prm.TFeedKelvin - xt) + (c1 * r1 + c2 * r2 + c3 * r3) - hr * (xt - xtj),
                sj * (prm.JacketTInKelvin - xtj) + hj * (xt - xtj)
            ];
        }

        double[] f1 = EvalRhs(in p, xIn);
        double[] w1 = SolveLu(f1);

        var y2 = new double[6];
        for (int i = 0; i < 6; i++) y2[i] = xIn[i] + Ros3A21 * w1[i];
        double[] f2 = EvalRhs(in p, y2);

        double invDt = 1.0 / dt;
        var rhs2 = new double[6];
        for (int i = 0; i < 6; i++) rhs2[i] = f2[i] + Ros3C21 * invDt * w1[i];
        double[] w2 = SolveLu(rhs2);

        var rhs3 = new double[6];
        for (int i = 0; i < 6; i++) rhs3[i] = f2[i] + (Ros3C31 * w1[i] + Ros3C32 * w2[i]) * invDt;
        double[] w3 = SolveLu(rhs3);

        for (int i = 0; i < 6; i++)
        {
            double v = xIn[i] + Ros3M1 * w1[i] + Ros3M2 * w2[i] + Ros3M3 * w3[i];
            if (!double.IsFinite(v)) return false;
            xOut[i] = v;
            if (errOut != null) errOut[i] = Ros3E1 * w1[i] + Ros3E2 * w2[i] + Ros3E3 * w3[i];
        }
        return true;
    }

    private static int ManagedSeriesParallelStepRos3(
        in CstrSeriesParallelParamsFFI p,
        ref double ca, ref double cb, ref double cc, ref double cd, ref double temp, ref double tempJ, double dt)
    {
        if (dt <= 0.0 || p.VolumeLiters <= 0.0 || p.JacketVolumeLiters <= 0.0) return -1;
        double[] x = [ca, cb, cc, cd, temp, tempJ];
        var xNext = new double[6];
        if (!ManagedSeriesParallelRos3Single(in p, x, dt, xNext, null)) return -1;
        ca = Math.Max(0.0, xNext[0]); cb = Math.Max(0.0, xNext[1]);
        cc = Math.Max(0.0, xNext[2]); cd = Math.Max(0.0, xNext[3]);
        temp = Math.Clamp(xNext[4], 50.0, 2500.0); tempJ = Math.Clamp(xNext[5], 50.0, 2500.0);
        return 0;
    }

    private static int ManagedSeriesParallelStepRos3Detailed(
        in CstrSeriesParallelParamsFFI p,
        ref double ca, ref double cb, ref double cc, ref double cd, ref double temp, ref double tempJ,
        double dt, double rtol, double atol, double runawayThresholdK, out CstrSeriesParallelStateFFI state)
    {
        state = default;
        if (dt <= 0.0 || p.VolumeLiters <= 0.0 || p.JacketVolumeLiters <= 0.0) return -1;

        double rel = rtol > 0.0 ? rtol : 1e-5;
        double abs = atol > 0.0 ? atol : 1e-7;
        double[] x = [ca, cb, cc, cd, temp, tempJ];
        var xCand = new double[6];
        var errVec = new double[6];
        double t = 0.0, h = dt;
        const double hMin = 1e-8;

        for (int step = 0; step < 4096 && t < dt - 1e-12; step++)
        {
            if (t + h > dt) h = dt - t;
            if (!ManagedSeriesParallelRos3Single(in p, x, h, xCand, errVec))
            {
                h *= 0.25;
                if (h < hMin) return -1;
                continue;
            }
            double errNorm = 0.0;
            for (int i = 0; i < 6; i++)
            {
                double sc = abs + rel * Math.Max(Math.Abs(x[i]), Math.Abs(xCand[i]));
                double ratio = Math.Abs(errVec[i]) / sc;
                errNorm += ratio * ratio;
            }
            errNorm = Math.Sqrt(errNorm / 6.0);
            if (errNorm <= 1.0 || h <= hMin * 1.1)
            {
                for (int i = 0; i < 6; i++)
                    x[i] = i < 4 ? Math.Max(0.0, xCand[i]) : Math.Clamp(xCand[i], 50.0, 2500.0);
                t += h;
                double scale = errNorm > 1e-12 ? Math.Clamp(0.9 * Math.Pow(errNorm, -1.0 / 3.0), 0.2, 4.0) : 4.0;
                h *= scale;
            }
            else
            {
                double scale = Math.Clamp(0.9 * Math.Pow(errNorm, -1.0 / 3.0), 0.1, 0.5);
                h = Math.Max(hMin, h * scale);
            }
        }
        if (t < dt - 1e-9) return -1;

        ca = x[0]; cb = x[1]; cc = x[2]; cd = x[3]; temp = x[4]; tempJ = x[5];
        ManagedSeriesParallelRates(in p, ca, cb, temp, out double r1, out double r2, out double r3);
        double ca0 = p.CaFeedMolPerL > 1e-12 ? p.CaFeedMolPerL : 1.0;
        double totalProd = cb + cc + cd;
        state = new CstrSeriesParallelStateFFI
        {
            CaMolPerL = ca, CbMolPerL = cb, CcMolPerL = cc, CdMolPerL = cd,
            TempKelvin = temp, JacketTempKelvin = tempJ,
            ConversionAPct = ((p.CaFeedMolPerL - ca) / ca0) * 100.0,
            YieldBPct = ((cb - p.CbFeedMolPerL) / ca0) * 100.0,
            SelectivityBPct = totalProd > 1e-12 ? (cb / totalProd) * 100.0 : 100.0,
            RateR1MolPerLMin = r1, RateR2MolPerLMin = r2, RateR3MolPerLMin = r3,
            HeatGeneratedWatts = p.VolumeLiters * (p.DeltaH1JPerMol * r1 + p.DeltaH2JPerMol * r2 + p.DeltaH3JPerMol * r3) / 60.0,
            HeatRemovedWatts = p.UaJPerMinK * (temp - tempJ) / 60.0,
            IsThermalRunaway = temp >= runawayThresholdK
        };
        return 0;
    }

    #endregion

    private static class NativeMethods
    {
        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr reactor_core_version();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_default_params(ref CstrParamsFFI out_params);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern double reactor_cstr_reaction_rate(in CstrParamsFFI parameters, double ca, double t);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_step_rk4(in CstrParamsFFI parameters, ref double ca, ref double temp_k, double dt);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_step_detailed(
            in CstrParamsFFI parameters,
            ref double ca,
            ref double temp_k,
            double dt,
            double runaway_threshold_k,
            out CstrStateFFI out_state
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_simulate_trajectory(
            in CstrParamsFFI parameters,
            double initial_ca,
            double initial_temp_k,
            double dt,
            nuint steps,
            [Out] double[] out_time,
            [Out] double[] out_ca,
            [Out] double[] out_temp
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_step_adaptive(
            in CstrParamsFFI parameters,
            ref double ca,
            ref double temp_k,
            double dt,
            double rtol,
            double atol,
            out CstrAdaptiveStatsFFI out_stats
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_step_detailed_adaptive(
            in CstrParamsFFI parameters,
            ref double ca,
            ref double temp_k,
            double dt,
            double rtol,
            double atol,
            double runaway_threshold_k,
            out CstrStateFFI out_state,
            out CstrAdaptiveStatsFFI out_stats
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_simulate_adaptive_trajectory(
            in CstrParamsFFI parameters,
            double initial_ca,
            double initial_temp_k,
            double dt,
            nuint steps,
            double rtol,
            double atol,
            [Out] double[] out_time,
            [Out] double[] out_ca,
            [Out] double[] out_temp,
            out CstrAdaptiveStatsFFI out_stats
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_core_has_simd();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_batch_step_rk4(
            in CstrParamsFFI parameters,
            [In, Out] double[] ca_array,
            [In, Out] double[] temp_array,
            nuint n_reactors,
            double dt
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_batch_step_detailed(
            in CstrParamsFFI parameters,
            [In, Out] double[] ca_array,
            [In, Out] double[] temp_array,
            nuint n_reactors,
            double dt,
            double runaway_threshold_k,
            [In, Out] CstrStateFFI[] out_states
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_batch_simulate_trajectory(
            in CstrParamsFFI parameters,
            [In] double[] ca0_array,
            [In] double[] temp0_array,
            nuint n_reactors,
            double dt,
            nuint steps,
            [Out] double[] out_time,
            [Out] double[] out_ca,
            [Out] double[] out_temp
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_core_simd_level();

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_default_params(ref CstrSeriesParallelParamsFFI out_params);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_rates(
            in CstrSeriesParallelParamsFFI parameters,
            double Ca,
            double Cb,
            double T,
            out double out_r1,
            out double out_r2,
            out double out_r3
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_step_rk4(
            in CstrSeriesParallelParamsFFI parameters,
            ref double ca,
            ref double cb,
            ref double cc,
            ref double cd,
            ref double temp,
            ref double temp_j,
            double dt
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_step_detailed(
            in CstrSeriesParallelParamsFFI parameters,
            ref double ca,
            ref double cb,
            ref double cc,
            ref double cd,
            ref double temp,
            ref double temp_j,
            double dt,
            double runaway_threshold_k,
            out CstrSeriesParallelStateFFI out_state
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_batch_step_rk4(
            in CstrSeriesParallelParamsFFI parameters,
            [In, Out] double[] ca_array,
            [In, Out] double[] cb_array,
            [In, Out] double[] cc_array,
            [In, Out] double[] cd_array,
            [In, Out] double[] temp_array,
            [In, Out] double[] temp_j_array,
            nuint n_reactors,
            double dt
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_batch_step_detailed(
            in CstrSeriesParallelParamsFFI parameters,
            [In, Out] double[] ca_array,
            [In, Out] double[] cb_array,
            [In, Out] double[] cc_array,
            [In, Out] double[] cd_array,
            [In, Out] double[] temp_array,
            [In, Out] double[] temp_j_array,
            nuint n_reactors,
            double dt,
            double runaway_threshold_k,
            [In, Out] CstrSeriesParallelStateFFI[] out_states
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_batch_simulate_trajectory(
            in CstrSeriesParallelParamsFFI parameters,
            [In] double[] ca0,
            [In] double[] cb0,
            [In] double[] cc0,
            [In] double[] cd0,
            [In] double[] temp0,
            [In] double[] temp_j0,
            nuint n_reactors,
            double dt,
            nuint steps,
            [Out] double[] out_time,
            [Out] double[] out_ca,
            [Out] double[] out_cb,
            [Out] double[] out_cc,
            [Out] double[] out_cd,
            [Out] double[] out_temp,
            [Out] double[] out_temp_j
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_step_ros3(
            in CstrParamsFFI parameters,
            ref double ca,
            ref double temp_k,
            double dt
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_step_ros3(
            in CstrSeriesParallelParamsFFI parameters,
            ref double ca,
            ref double cb,
            ref double cc,
            ref double cd,
            ref double temp,
            ref double temp_j,
            double dt
        );

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int reactor_cstr_series_parallel_step_ros3_detailed(
            in CstrSeriesParallelParamsFFI parameters,
            ref double ca,
            ref double cb,
            ref double cc,
            ref double cd,
            ref double temp,
            ref double temp_j,
            double dt,
            double rtol,
            double atol,
            double runaway_threshold_k,
            out CstrSeriesParallelStateFFI out_state
        );
    }
}
