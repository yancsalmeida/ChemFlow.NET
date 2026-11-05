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
    [MarshalAs(UnmanagedType.I1)]
    public bool IsRunaway;
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
    public static string VersionString { get; }

    static NativeReactorCoreInterop()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeReactorCoreInterop).Assembly, ResolveDllImport);

        try
        {
            IntPtr ptr = NativeMethods.reactor_core_version();
            VersionString = Marshal.PtrToStringAnsi(ptr) ?? "Unknown";
            IsNativeAvailable = VersionString.Contains("ReactorCore.C", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            IsNativeAvailable = false;
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
    }
}
