using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;
using ChemFlow.Domain.Transport;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Models an industrial battery/train of N parallel Continuous Stirred-Tank Reactors (CSTRs).
/// Simulates all N reactors simultaneously using SIMD AVX2 and FMA acceleration from ReactorCore.C via P/Invoke.
/// Rigorously evaluates transport properties (Chung / Ely-Hanley) for each reactor in the battery.
/// </summary>
public sealed class ContinuousStirredTankReactorBattery : UnitOperationBase
{
    public override string UnitType => "ContinuousStirredTankReactorBattery";

    public MaterialStream InletStream => InletStreams[0];
    public MaterialStream OutletStream => OutletStreams[0];

    public int ReactorCount { get; }
    public string ReactantCompoundId { get; }
    public string ProductCompoundId { get; }

    public double ReactorVolumeLiters { get; }
    public double CoolantTemperatureKelvin { get; }
    public double NominalHeatTransferUaWattsPerKelvin { get; }
    public double PreExponentialFactorPerMin { get; }
    public double ActivationEnergyOverR { get; }
    public double ReactionEnthalpyJPerMol { get; }
    public double FluidDensityKgPerM3 { get; }
    public double FluidHeatCapacityJPerKgK { get; }
    public double MaxSafeTemperatureKelvin { get; }

    public ITransportPropertiesPackage TransportPackage { get; init; }

    // Evaluated simulation metrics
    public IReadOnlyList<CstrStateFFI> ReactorStates { get; private set; } = Array.Empty<CstrStateFFI>();
    public IReadOnlyList<TransportPropertyResult> ReactorTransportProperties { get; private set; } = Array.Empty<TransportPropertyResult>();
    public double MeanConversion { get; private set; }
    public double MeanTemperatureKelvin { get; private set; }
    public double MeanOutletConcentrationMolPerL { get; private set; }
    public bool HasThermalRunaway { get; private set; }
    public bool IsUsingNativeSimd => NativeReactorCoreInterop.HasNativeSimdSupport;

    public ContinuousStirredTankReactorBattery(
        string id,
        string name,
        MaterialStream inletStream,
        MaterialStream outletStream,
        int reactorCount,
        string reactantCompoundId,
        string productCompoundId,
        double reactorVolumeLiters = 100.0,
        double coolantTemperatureKelvin = 300.0,
        double nominalHeatTransferUaWattsPerKelvin = 666.67,
        double preExponentialFactorPerMin = 7.08e10,
        double activationEnergyOverR = 8330.1,
        double reactionEnthalpyJPerMol = 200000.0,
        double fluidDensityKgPerM3 = 1000.0,
        double fluidHeatCapacityJPerKgK = 4000.0,
        double maxSafeTemperatureKelvin = 400.0,
        ITransportPropertiesPackage? transportPackage = null)
        : base(id, name, [inletStream], [outletStream])
    {
        if (reactorCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(reactorCount), "Reactor count must be at least 1.");
        if (string.IsNullOrWhiteSpace(reactantCompoundId))
            throw new ArgumentException("Reactant compound ID cannot be empty.", nameof(reactantCompoundId));
        if (string.IsNullOrWhiteSpace(productCompoundId))
            throw new ArgumentException("Product compound ID cannot be empty.", nameof(productCompoundId));

        ReactorCount = reactorCount;
        ReactantCompoundId = reactantCompoundId.Trim();
        ProductCompoundId = productCompoundId.Trim();
        ReactorVolumeLiters = reactorVolumeLiters;
        CoolantTemperatureKelvin = coolantTemperatureKelvin;
        NominalHeatTransferUaWattsPerKelvin = nominalHeatTransferUaWattsPerKelvin;
        PreExponentialFactorPerMin = preExponentialFactorPerMin;
        ActivationEnergyOverR = activationEnergyOverR;
        ReactionEnthalpyJPerMol = reactionEnthalpyJPerMol;
        FluidDensityKgPerM3 = fluidDensityKgPerM3;
        FluidHeatCapacityJPerKgK = fluidHeatCapacityJPerKgK;
        MaxSafeTemperatureKelvin = maxSafeTemperatureKelvin;
        TransportPackage = transportPackage ?? new ChungTransportPackage();
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = InletStream.Compounds;
        int nCompounds = compounds.Count;

        int reactantIdx = -1;
        int productIdx = -1;
        for (int i = 0; i < nCompounds; i++)
        {
            if (compounds[i].Id == ReactantCompoundId) reactantIdx = i;
            if (compounds[i].Id == ProductCompoundId) productIdx = i;
        }

        if (reactantIdx < 0)
            throw new InvalidOperationException($"Reactant '{ReactantCompoundId}' not found in inlet compounds.");
        if (productIdx < 0)
            throw new InvalidOperationException($"Product '{ProductCompoundId}' not found in inlet compounds.");

        // Mass flow and volumetric flow split across N parallel reactors
        double totalInletFlow = InletStream.MolarFlowRateMolPerSec;
        double totalMassFlowKgPerSec = InletStream.MassFlowRateKgPerSec;
        double massFlowPerReactor = totalMassFlowKgPerSec / ReactorCount;
        double qPerReactorLPerMin = (massFlowPerReactor / FluidDensityKgPerM3) * 60000.0; // L/min
        if (qPerReactorLPerMin <= 0.0) qPerReactorLPerMin = 100.0;

        // Feed concentration: normalized to benchmark kinetics (1.0 mol/L pure basis)
        double caInlet = Math.Max(0.01, InletStream.CompositionZ[reactantIdx] * 1.0);

        // Populate C-ABI parameters
        double rhoCp = FluidDensityKgPerM3 * FluidHeatCapacityJPerKgK * 1e-3; // J/(L·K)
        double uaPerMin = NominalHeatTransferUaWattsPerKelvin * 60.0; // J/(min·K)
        double tauMin = ReactorVolumeLiters / qPerReactorLPerMin;
        if (tauMin <= 0.0) tauMin = 1.0;

        var parameters = new CstrParamsFFI
        {
            VolumetricFlowLPerMin = qPerReactorLPerMin,
            VolumeLiters = ReactorVolumeLiters,
            K0PerMin = PreExponentialFactorPerMin,
            EaOverRKelvin = ActivationEnergyOverR,
            DeltaHJPerMol = ReactionEnthalpyJPerMol,
            RhoCpJPerLK = rhoCp,
            UaJPerMinK = uaPerMin,
            CaFeedMolPerL = caInlet,
            TFeedKelvin = InletStream.TemperatureKelvin,
            TCoolantKelvin = CoolantTemperatureKelvin
        };

        // Prepare initial state vectors for all N reactors in parallel
        double[] caArray = new double[ReactorCount];
        double[] tempArray = new double[ReactorCount];
        for (int i = 0; i < ReactorCount; i++)
        {
            caArray[i] = caInlet;
            tempArray[i] = InletStream.TemperatureKelvin;
        }

        // SIMD AVX2 Batch Integration Step over reactor residence time tau
        double dtMin = Math.Min(0.005, tauMin / 200.0);
        int subSteps = (int)Math.Ceiling(tauMin / dtMin);
        if (subSteps < 10) subSteps = 10;
        dtMin = tauMin / subSteps;

        for (int step = 0; step < subSteps; step++)
        {
            NativeReactorCoreInterop.StepBatchRk4(in parameters, caArray, tempArray, dtMin);
        }

        var states = new CstrStateFFI[ReactorCount];
        NativeReactorCoreInterop.StepBatchDetailed(in parameters, caArray, tempArray, dtMin, MaxSafeTemperatureKelvin, states);
        ReactorStates = states;

        // Evaluate transport properties for each reactor in the battery
        var transportResults = new List<TransportPropertyResult>(ReactorCount);
        double sumConv = 0.0;
        double sumT = 0.0;
        double sumCa = 0.0;
        bool anyRunaway = false;

        for (int i = 0; i < ReactorCount; i++)
        {
            sumConv += states[i].ConversionPct;
            sumT += states[i].TempKelvin;
            sumCa += states[i].CaMolPerL;
            if (states[i].IsRunaway) anyRunaway = true;

            // Reacting mixture composition estimation
            double[] reactorComp = (double[])InletStream.CompositionZ.ToArray().Clone();
            double convFrac = states[i].ConversionPct / 100.0;
            double reactedMoles = reactorComp[reactantIdx] * convFrac;
            reactorComp[reactantIdx] -= reactedMoles;
            reactorComp[productIdx] += reactedMoles;

            var prop = TransportPackage.CalculateLiquidProperties(
                compounds, reactorComp, states[i].TempKelvin, InletStream.PressureKpa);
            transportResults.Add(prop);
        }

        ReactorTransportProperties = transportResults;
        MeanConversion = sumConv / ReactorCount;
        MeanTemperatureKelvin = sumT / ReactorCount;
        MeanOutletConcentrationMolPerL = sumCa / ReactorCount;
        HasThermalRunaway = anyRunaway;

        // Blended outlet composition
        double[] blendedComp = (double[])InletStream.CompositionZ.ToArray().Clone();
        double meanConvFrac = MeanConversion / 100.0;
        double deltaMoles = blendedComp[reactantIdx] * meanConvFrac;
        blendedComp[reactantIdx] -= deltaMoles;
        blendedComp[productIdx] += deltaMoles;

        // Mass-conserving outlet molar flow rate: \dot{n}_out = \dot{m}_in / M_avg,out
        double avgOutletMw = 0.0;
        for (int i = 0; i < nCompounds; i++)
        {
            avgOutletMw += blendedComp[i] * compounds[i].MolarMass;
        }
        double outletMolarFlowRate = avgOutletMw > 0.0
            ? (totalMassFlowKgPerSec / avgOutletMw) * 1000.0
            : totalInletFlow;

        OutletStream.UpdateState(
            molarFlowRateMolPerSec: outletMolarFlowRate,
            temperatureKelvin: MeanTemperatureKelvin,
            pressureKpa: InletStream.PressureKpa,
            compositionZ: blendedComp,
            thermo: thermo
        );

        // Heat exchange duty Q = \dot{m} Cp (T_eff - T_feed) - \Delta H * r_tot
        EnergyDutyWatts = OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts;
    }
}
