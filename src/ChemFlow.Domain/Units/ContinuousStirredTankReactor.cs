using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Continuous Stirred-Tank Reactor (CSTR) executing coupled non-linear mass and energy balances
/// with Arrhenius kinetics, integrated via 4th-Order Runge-Kutta (RK4).
/// Powered by the native C11 numerical engine ReactorCore.C via high-performance C-ABI P/Invoke.
/// </summary>
public sealed class ContinuousStirredTankReactor : UnitOperationBase
{
    public override string UnitType => "ContinuousStirredTankReactor";
    public MaterialStream InletStream => InletStreams[0];
    public MaterialStream OutletStream => OutletStreams[0];

    public string ReactantCompoundId { get; }
    public string ProductCompoundId { get; }
    public double ReactorVolumeLiters { get; }
    public double CoolantTemperatureKelvin { get; }
    public double HeatTransferUaWattsPerKelvin { get; }
    public double PreExponentialFactorPerMin { get; }
    public double ActivationEnergyOverR { get; }
    public double ReactionEnthalpyJPerMol { get; }
    public double FluidDensityKgPerM3 { get; }
    public double FluidHeatCapacityJPerKgK { get; }
    public double MaxSafeTemperatureKelvin { get; }

    // Evaluated simulation metrics
    public double FractionalConversion { get; private set; }
    public double OutletConcentrationMolPerL { get; private set; }
    public double ReactionRateMolPerLMin { get; private set; }
    public bool IsThermalRunaway { get; private set; }
    public bool UseAdaptiveSolver { get; init; } = true;
    public CstrAdaptiveStatsFFI LastAdaptiveStats { get; private set; }
    public bool IsUsingNativeCore => NativeReactorCoreInterop.IsNativeAvailable;
    public string CoreVersion => NativeReactorCoreInterop.VersionString;

    public ContinuousStirredTankReactor(
        string id,
        string name,
        MaterialStream inletStream,
        MaterialStream outletStream,
        string reactantCompoundId,
        string productCompoundId,
        double reactorVolumeLiters = 100.0,
        double coolantTemperatureKelvin = 300.0,
        double heatTransferUaWattsPerKelvin = 666.67, // ~40000 J/(min*K)
        double preExponentialFactorPerMin = 7.08e10,
        double activationEnergyOverR = 8330.1,
        double reactionEnthalpyJPerMol = 200000.0,
        double fluidDensityKgPerM3 = 1000.0,
        double fluidHeatCapacityJPerKgK = 4000.0,
        double maxSafeTemperatureKelvin = 400.0,
        bool useAdaptiveSolver = true)
        : base(id, name, [inletStream], [outletStream])
    {
        UseAdaptiveSolver = useAdaptiveSolver;
        if (string.IsNullOrWhiteSpace(reactantCompoundId))
            throw new ArgumentException("Reactant compound ID cannot be empty.", nameof(reactantCompoundId));
        if (string.IsNullOrWhiteSpace(productCompoundId))
            throw new ArgumentException("Product compound ID cannot be empty.", nameof(productCompoundId));
        if (reactorVolumeLiters <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(reactorVolumeLiters), "Reactor volume must be > 0 L.");
        if (coolantTemperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(coolantTemperatureKelvin), "Coolant temperature must be > 0 K.");

        ReactantCompoundId = reactantCompoundId;
        ProductCompoundId = productCompoundId;
        ReactorVolumeLiters = reactorVolumeLiters;
        CoolantTemperatureKelvin = coolantTemperatureKelvin;
        HeatTransferUaWattsPerKelvin = heatTransferUaWattsPerKelvin;
        PreExponentialFactorPerMin = preExponentialFactorPerMin;
        ActivationEnergyOverR = activationEnergyOverR;
        ReactionEnthalpyJPerMol = reactionEnthalpyJPerMol;
        FluidDensityKgPerM3 = fluidDensityKgPerM3;
        FluidHeatCapacityJPerKgK = fluidHeatCapacityJPerKgK;
        MaxSafeTemperatureKelvin = maxSafeTemperatureKelvin;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = InletStream.Compounds;
        int n = compounds.Count;

        int reactantIdx = -1;
        int productIdx = -1;
        for (int i = 0; i < n; i++)
        {
            if (compounds[i].Id == ReactantCompoundId) reactantIdx = i;
            if (compounds[i].Id == ProductCompoundId) productIdx = i;
        }

        if (reactantIdx < 0)
            throw new InvalidOperationException($"Reactant compound '{ReactantCompoundId}' is not found in inlet stream compounds.");
        if (productIdx < 0)
            throw new InvalidOperationException($"Product compound '{ProductCompoundId}' is not found in inlet stream compounds.");

        // Compute volumetric flow rate q (L/min) from molar flow and fluid density
        double molarFlowIn = InletStream.MolarFlowRateMolPerSec; // mol/s
        double avgMolarMassKgPerMol = InletStream.AverageMolarMassGPerMol / 1000.0;
        double massFlowKgPerSec = molarFlowIn * avgMolarMassKgPerMol;
        double volumetricFlowM3PerSec = FluidDensityKgPerM3 > 0 ? massFlowKgPerSec / FluidDensityKgPerM3 : 0.001;
        double volumetricFlowLPerMin = volumetricFlowM3PerSec * 1000.0 * 60.0; // L/min

        // Feed concentration CA0 (mol/L)
        double reactantMolarFlowIn = molarFlowIn * InletStream.CompositionZ[reactantIdx]; // mol/s
        double caFeedMolPerL = volumetricFlowM3PerSec > 0
            ? (reactantMolarFlowIn / (volumetricFlowM3PerSec * 1000.0))
            : 1.0;

        // UA in J/(min*K) = Watts/K * 60 s/min
        double uaJPerMinK = HeatTransferUaWattsPerKelvin * 60.0;
        double rhoCpJPerLK = (FluidDensityKgPerM3 * FluidHeatCapacityJPerKgK) / 1000.0; // J/(L*K)

        var parameters = new CstrParamsFFI
        {
            VolumetricFlowLPerMin = Math.Max(1.0, volumetricFlowLPerMin),
            VolumeLiters = ReactorVolumeLiters,
            K0PerMin = PreExponentialFactorPerMin,
            EaOverRKelvin = ActivationEnergyOverR,
            DeltaHJPerMol = ReactionEnthalpyJPerMol,
            RhoCpJPerLK = rhoCpJPerLK,
            UaJPerMinK = uaJPerMinK,
            CaFeedMolPerL = Math.Max(1e-4, caFeedMolPerL),
            TFeedKelvin = InletStream.TemperatureKelvin,
            TCoolantKelvin = CoolantTemperatureKelvin
        };

        // Advance CSTR dynamic state over residence time tau = V / q (min)
        double residenceTimeMin = parameters.VolumeLiters / parameters.VolumetricFlowLPerMin;
        double curCa = parameters.CaFeedMolPerL;
        double curTemp = parameters.TFeedKelvin;

        if (UseAdaptiveSolver)
        {
            NativeReactorCoreInterop.StepDetailedAdaptive(
                in parameters,
                ref curCa,
                ref curTemp,
                residenceTimeMin,
                MaxSafeTemperatureKelvin,
                rtol: 1e-5,
                atol: 1e-7,
                out CstrStateFFI state,
                out CstrAdaptiveStatsFFI stats
            );

            OutletConcentrationMolPerL = state.CaMolPerL;
            ReactionRateMolPerLMin = state.ReactionRateMolPerLMin;
            FractionalConversion = state.ConversionPct / 100.0;
            IsThermalRunaway = state.IsRunaway;
            LastAdaptiveStats = stats;
        }
        else
        {
            double dtMin = Math.Min(0.005, residenceTimeMin / 20.0);
            int steps = Math.Max(20, (int)Math.Ceiling(residenceTimeMin / dtMin));
            dtMin = residenceTimeMin / steps;

            for (int s = 0; s < steps; s++)
            {
                NativeReactorCoreInterop.StepDetailed(
                    in parameters,
                    ref curCa,
                    ref curTemp,
                    dtMin,
                    MaxSafeTemperatureKelvin,
                    out CstrStateFFI state
                );

                if (s == steps - 1)
                {
                    OutletConcentrationMolPerL = state.CaMolPerL;
                    ReactionRateMolPerLMin = state.ReactionRateMolPerLMin;
                    FractionalConversion = state.ConversionPct / 100.0;
                    IsThermalRunaway = state.IsRunaway;
                }
            }
        }

        // Clamp fractional conversion in [0, 1]
        FractionalConversion = Math.Max(0.0, Math.Min(1.0, FractionalConversion));

        // Update outlet stream composition (A -> B isomerization / equimolar reaction)
        double[] zOut = new double[n];
        for (int i = 0; i < n; i++)
        {
            if (i == reactantIdx)
            {
                zOut[i] = InletStream.CompositionZ[i] * (1.0 - FractionalConversion);
            }
            else if (i == productIdx)
            {
                zOut[i] = InletStream.CompositionZ[i] + InletStream.CompositionZ[reactantIdx] * FractionalConversion;
            }
            else
            {
                zOut[i] = InletStream.CompositionZ[i];
            }
        }

        // Normalize mole fractions
        double sumZ = 0.0;
        for (int i = 0; i < n; i++) sumZ += zOut[i];
        for (int i = 0; i < n; i++) zOut[i] /= sumZ;

        OutletStream.UpdateState(
            molarFlowRateMolPerSec: molarFlowIn,
            temperatureKelvin: curTemp,
            pressureKpa: InletStream.PressureKpa,
            compositionZ: zOut,
            thermo: thermo
        );

        // Net energy duty Q_dot added to the fluid in Watts (J/s) to satisfy First Law:
        // H_dot_in + Q_dot = H_dot_out  =>  Q_dot = H_dot_out - H_dot_in
        EnergyDutyWatts = OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts;
    }
}
