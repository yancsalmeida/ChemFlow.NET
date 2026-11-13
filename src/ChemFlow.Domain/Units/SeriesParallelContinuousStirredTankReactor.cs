using ChemFlow.Domain.Entities;
using ChemFlow.Domain.Native;
using ChemFlow.Domain.Streams;
using ChemFlow.Domain.Thermodynamics;

namespace ChemFlow.Domain.Units;

/// <summary>
/// Non-isothermal Continuous Stirred Tank Reactor (CSTR) unit operation modeling the
/// series-parallel reaction network A -> B -> C and A -> D coupled with a dynamic
/// cooling jacket energy balance, powered by the native ReactorCore.C C11 solver
/// with automatic managed C# fallback.
/// </summary>
public sealed class SeriesParallelContinuousStirredTankReactor : UnitOperationBase
{
    public override string UnitType => "SeriesParallelContinuousStirredTankReactor";

    public MaterialStream InletStream => InletStreams[0];
    public MaterialStream OutletStream => OutletStreams[0];

    public string ReactantACompoundId { get; }
    public string ProductBCompoundId { get; }
    public string ByproductCCompoundId { get; }
    public string ByproductDCompoundId { get; }

    public double ReactorVolumeLiters { get; }
    public double LiquidDensityKgPerL { get; }
    public double RhoCpJPerLK { get; }

    public double K0_1PerMin { get; }
    public double Ea1OverRKelvin { get; }
    public double DeltaH1JPerMol { get; }

    public double K0_2PerMin { get; }
    public double Ea2OverRKelvin { get; }
    public double DeltaH2JPerMol { get; }

    public double K0_3PerMin { get; }
    public double Ea3OverRKelvin { get; }
    public double DeltaH3JPerMol { get; }

    public double JacketVolumeLiters { get; }
    public double JacketFlowLPerMin { get; }
    public double JacketRhoCpJPerLK { get; }
    public double JacketInletTemperatureKelvin { get; }
    public double CoolingUaWattsPerKelvin { get; }
    public double UaJPerMinK => CoolingUaWattsPerKelvin * 60.0;

    public double MaxSafeTemperatureKelvin { get; }
    public double PressureDropKpa { get; }
    public double IntegrationResidenceTimes { get; }
    public int MaxIntegrationSteps { get; }

    public double VolumetricFlowLPerMin { get; private set; }
    public double ResidenceTimeMinutes { get; private set; }
    public double InletCaMolPerL { get; private set; }
    public double InletCbMolPerL { get; private set; }
    public double InletCcMolPerL { get; private set; }
    public double InletCdMolPerL { get; private set; }

    public double OutletCaMolPerL { get; private set; }
    public double OutletCbMolPerL { get; private set; }
    public double OutletCcMolPerL { get; private set; }
    public double OutletCdMolPerL { get; private set; }

    public double OutletTemperatureKelvin { get; private set; }
    public double JacketOutletTemperatureKelvin { get; private set; }
    public double ConversionAPercent { get; private set; }
    public double YieldBPercent { get; private set; }
    public double SelectivityBPercent { get; private set; }
    public double RateR1MolPerLMin { get; private set; }
    public double RateR2MolPerLMin { get; private set; }
    public double RateR3MolPerLMin { get; private set; }
    public double HeatGeneratedWatts { get; private set; }
    public double HeatRemovedWatts { get; private set; }
    public bool IsThermalRunaway { get; private set; }
    public bool IsUsingNativeCore => NativeReactorCoreInterop.IsNativeAvailable;

    public SeriesParallelContinuousStirredTankReactor(
        string id,
        string name,
        MaterialStream inletStream,
        MaterialStream outletStream,
        string reactantACompoundId,
        string productBCompoundId,
        string byproductCCompoundId,
        string byproductDCompoundId,
        double? reactorVolumeLiters = null,
        double liquidDensityKgPerL = 0.85,
        double? rhoCpJPerLK = null,
        double? k0_1PerMin = null,
        double? ea1OverRKelvin = null,
        double? deltaH1JPerMol = null,
        double? k0_2PerMin = null,
        double? ea2OverRKelvin = null,
        double? deltaH2JPerMol = null,
        double? k0_3PerMin = null,
        double? ea3OverRKelvin = null,
        double? deltaH3JPerMol = null,
        double? jacketVolumeLiters = null,
        double? jacketFlowLPerMin = null,
        double? jacketRhoCpJPerLK = null,
        double? jacketInletTemperatureKelvin = null,
        double? coolingUaWattsPerKelvin = null,
        double maxSafeTemperatureKelvin = 450.0,
        double pressureDropKpa = 0.0,
        double integrationResidenceTimes = 6.0,
        int maxIntegrationSteps = 2000)
        : base(id, name, [inletStream], [outletStream])
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reactantACompoundId);
        ArgumentException.ThrowIfNullOrWhiteSpace(productBCompoundId);
        ArgumentException.ThrowIfNullOrWhiteSpace(byproductCCompoundId);
        ArgumentException.ThrowIfNullOrWhiteSpace(byproductDCompoundId);

        var distinctIds = new HashSet<string>(StringComparer.Ordinal)
        {
            reactantACompoundId,
            productBCompoundId,
            byproductCCompoundId,
            byproductDCompoundId
        };
        if (distinctIds.Count != 4)
            throw new ArgumentException("Reactant A, Product B, Byproduct C, and Byproduct D must reference four distinct compound IDs.");

        CstrSeriesParallelParamsFFI defaults = NativeReactorCoreInterop.GetSeriesParallelDefaultParameters();

        double resolvedVolume = reactorVolumeLiters ?? defaults.VolumeLiters;
        double resolvedRhoCp = rhoCpJPerLK ?? defaults.RhoCpJPerLK;
        double resolvedJacketVol = jacketVolumeLiters ?? defaults.JacketVolumeLiters;
        double resolvedJacketFlow = jacketFlowLPerMin ?? defaults.JacketFlowLPerMin;
        double resolvedJacketRhoCp = jacketRhoCpJPerLK ?? defaults.JacketRhoCpJPerLK;
        double resolvedJacketTIn = jacketInletTemperatureKelvin ?? defaults.JacketTInKelvin;
        double resolvedUaWattsPerK = coolingUaWattsPerKelvin ?? (defaults.UaJPerMinK / 60.0);

        if (resolvedVolume <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(reactorVolumeLiters), "Reactor volume must be strictly positive.");
        if (liquidDensityKgPerL <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(liquidDensityKgPerL), "Liquid density must be strictly positive.");
        if (resolvedRhoCp <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(rhoCpJPerLK), "Reactor heat capacity density must be strictly positive.");
        if (resolvedJacketVol <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(jacketVolumeLiters), "Jacket volume must be strictly positive.");
        if (resolvedJacketFlow < 0.0)
            throw new ArgumentOutOfRangeException(nameof(jacketFlowLPerMin), "Jacket flow rate cannot be negative.");
        if (resolvedJacketRhoCp <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(jacketRhoCpJPerLK), "Jacket coolant heat capacity density must be strictly positive.");
        if (resolvedJacketTIn <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(jacketInletTemperatureKelvin), "Jacket inlet temperature must be positive in Kelvin.");
        if (resolvedUaWattsPerK < 0.0)
            throw new ArgumentOutOfRangeException(nameof(coolingUaWattsPerKelvin), "Cooling UA cannot be negative.");
        if (maxSafeTemperatureKelvin <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(maxSafeTemperatureKelvin), "Max safe temperature must be positive in Kelvin.");
        if (pressureDropKpa < 0.0)
            throw new ArgumentOutOfRangeException(nameof(pressureDropKpa), "Pressure drop cannot be negative.");
        if (integrationResidenceTimes <= 0.0)
            throw new ArgumentOutOfRangeException(nameof(integrationResidenceTimes), "Integration horizon must be strictly positive.");
        if (maxIntegrationSteps < 1)
            throw new ArgumentOutOfRangeException(nameof(maxIntegrationSteps), "Max integration steps must be at least 1.");

        ReactantACompoundId = reactantACompoundId;
        ProductBCompoundId = productBCompoundId;
        ByproductCCompoundId = byproductCCompoundId;
        ByproductDCompoundId = byproductDCompoundId;

        ReactorVolumeLiters = resolvedVolume;
        LiquidDensityKgPerL = liquidDensityKgPerL;
        RhoCpJPerLK = resolvedRhoCp;

        K0_1PerMin = k0_1PerMin ?? defaults.K0_1PerMin;
        Ea1OverRKelvin = ea1OverRKelvin ?? defaults.Ea1OverRKelvin;
        DeltaH1JPerMol = deltaH1JPerMol ?? defaults.DeltaH1JPerMol;

        K0_2PerMin = k0_2PerMin ?? defaults.K0_2PerMin;
        Ea2OverRKelvin = ea2OverRKelvin ?? defaults.Ea2OverRKelvin;
        DeltaH2JPerMol = deltaH2JPerMol ?? defaults.DeltaH2JPerMol;

        K0_3PerMin = k0_3PerMin ?? defaults.K0_3PerMin;
        Ea3OverRKelvin = ea3OverRKelvin ?? defaults.Ea3OverRKelvin;
        DeltaH3JPerMol = deltaH3JPerMol ?? defaults.DeltaH3JPerMol;

        JacketVolumeLiters = resolvedJacketVol;
        JacketFlowLPerMin = resolvedJacketFlow;
        JacketRhoCpJPerLK = resolvedJacketRhoCp;
        JacketInletTemperatureKelvin = resolvedJacketTIn;
        CoolingUaWattsPerKelvin = resolvedUaWattsPerK;
        MaxSafeTemperatureKelvin = maxSafeTemperatureKelvin;
        PressureDropKpa = pressureDropKpa;
        IntegrationResidenceTimes = integrationResidenceTimes;
        MaxIntegrationSteps = maxIntegrationSteps;
    }

    protected override void SolveInternal(IThermodynamicPackage thermo)
    {
        IReadOnlyList<ChemicalCompound> compounds = InletStream.Compounds;
        int idxA = FindCompoundIndex(compounds, ReactantACompoundId);
        int idxB = FindCompoundIndex(compounds, ProductBCompoundId);
        int idxC = FindCompoundIndex(compounds, ByproductCCompoundId);
        int idxD = FindCompoundIndex(compounds, ByproductDCompoundId);

        double pOut = Math.Max(1.0, InletStream.PressureKpa - PressureDropKpa);
        double totalMassFlowKgPerSec = InletStream.MassFlowRateKgPerSec;
        double qLPerMin = (totalMassFlowKgPerSec / LiquidDensityKgPerL) * 60.0;

        if (qLPerMin <= 1e-12 || InletStream.MolarFlowRateMolPerSec <= 1e-12)
        {
            VolumetricFlowLPerMin = 0.0;
            ResidenceTimeMinutes = 0.0;
            InletCaMolPerL = 0.0;
            InletCbMolPerL = 0.0;
            InletCcMolPerL = 0.0;
            InletCdMolPerL = 0.0;
            OutletCaMolPerL = 0.0;
            OutletCbMolPerL = 0.0;
            OutletCcMolPerL = 0.0;
            OutletCdMolPerL = 0.0;
            OutletTemperatureKelvin = InletStream.TemperatureKelvin;
            JacketOutletTemperatureKelvin = JacketInletTemperatureKelvin;
            ConversionAPercent = 0.0;
            YieldBPercent = 0.0;
            SelectivityBPercent = 0.0;
            RateR1MolPerLMin = 0.0;
            RateR2MolPerLMin = 0.0;
            RateR3MolPerLMin = 0.0;
            HeatGeneratedWatts = 0.0;
            HeatRemovedWatts = 0.0;
            IsThermalRunaway = InletStream.TemperatureKelvin >= MaxSafeTemperatureKelvin;

            OutletStream.UpdateState(
                InletStream.MolarFlowRateMolPerSec,
                InletStream.TemperatureKelvin,
                pOut,
                [.. InletStream.CompositionZ],
                thermo);
            EnergyDutyWatts = OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts;
            return;
        }

        IReadOnlyList<double> zIn = InletStream.CompositionZ;
        double totalConcMolPerL = (InletStream.MolarFlowRateMolPerSec * 60.0) / qLPerMin;

        double caFeed = zIn[idxA] * totalConcMolPerL;
        double cbFeed = zIn[idxB] * totalConcMolPerL;
        double ccFeed = zIn[idxC] * totalConcMolPerL;
        double cdFeed = zIn[idxD] * totalConcMolPerL;

        VolumetricFlowLPerMin = qLPerMin;
        ResidenceTimeMinutes = ReactorVolumeLiters / qLPerMin;
        InletCaMolPerL = caFeed;
        InletCbMolPerL = cbFeed;
        InletCcMolPerL = ccFeed;
        InletCdMolPerL = cdFeed;

        var parameters = new CstrSeriesParallelParamsFFI
        {
            VolumetricFlowLPerMin = qLPerMin,
            VolumeLiters = ReactorVolumeLiters,
            RhoCpJPerLK = RhoCpJPerLK,
            K0_1PerMin = K0_1PerMin,
            Ea1OverRKelvin = Ea1OverRKelvin,
            DeltaH1JPerMol = DeltaH1JPerMol,
            K0_2PerMin = K0_2PerMin,
            Ea2OverRKelvin = Ea2OverRKelvin,
            DeltaH2JPerMol = DeltaH2JPerMol,
            K0_3PerMin = K0_3PerMin,
            Ea3OverRKelvin = Ea3OverRKelvin,
            DeltaH3JPerMol = DeltaH3JPerMol,
            CaFeedMolPerL = caFeed,
            CbFeedMolPerL = cbFeed,
            CcFeedMolPerL = ccFeed,
            CdFeedMolPerL = cdFeed,
            TFeedKelvin = InletStream.TemperatureKelvin,
            JacketVolumeLiters = JacketVolumeLiters,
            JacketFlowLPerMin = JacketFlowLPerMin,
            JacketRhoCpJPerLK = JacketRhoCpJPerLK,
            JacketTInKelvin = JacketInletTemperatureKelvin,
            UaJPerMinK = UaJPerMinK
        };

        double horizonMinutes = ResidenceTimeMinutes * IntegrationResidenceTimes;
        double maxMacroDt = Math.Min(0.05, horizonMinutes / 20.0);
        double invHydraulicAndThermalTau =
            (qLPerMin / ReactorVolumeLiters) +
            (UaJPerMinK / (ReactorVolumeLiters * RhoCpJPerLK)) +
            (JacketFlowLPerMin / JacketVolumeLiters) +
            (UaJPerMinK / (JacketVolumeLiters * JacketRhoCpJPerLK));

        double ca = caFeed;
        double cb = cbFeed;
        double cc = ccFeed;
        double cd = cdFeed;
        double temp = InletStream.TemperatureKelvin;
        double tempJ = JacketInletTemperatureKelvin;
        bool runawayObserved = temp >= MaxSafeTemperatureKelvin;
        CstrSeriesParallelStateFFI lastState = default;

        double elapsed = 0.0;
        int stepLimit = Math.Max(MaxIntegrationSteps, 200) * 10;
        int stepCount = 0;

        while (elapsed < horizonMinutes - 1e-12 && stepCount < stepLimit)
        {
            double remaining = horizonMinutes - elapsed;
            NativeReactorCoreInterop.SeriesParallelRates(in parameters, 1.0, 1.0, temp, out double k1, out double k2, out double k3);
            double maxCharacteristicRate = Math.Max(k1 + k3, Math.Max(k2, invHydraulicAndThermalTau));
            double safeDt = 0.25 / Math.Max(1e-3, maxCharacteristicRate);
            double dt = Math.Min(remaining, Math.Clamp(safeDt, 1e-5, maxMacroDt));

            int status = NativeReactorCoreInterop.StepSeriesParallelDetailed(
                in parameters,
                ref ca,
                ref cb,
                ref cc,
                ref cd,
                ref temp,
                ref tempJ,
                dt,
                MaxSafeTemperatureKelvin,
                out lastState);

            if (status != 0 || double.IsNaN(ca) || double.IsNaN(temp) || double.IsNaN(tempJ))
                throw new InvalidOperationException($"Series-parallel CSTR integration failed on unit '{Id}' (status={status}).");

            if (lastState.IsThermalRunaway || temp >= MaxSafeTemperatureKelvin)
                runawayObserved = true;

            if (temp >= 1500.0)
            {
                temp = 1500.0;
                break;
            }

            elapsed += dt;
            stepCount++;
        }

        OutletCaMolPerL = Math.Max(0.0, ca);
        OutletCbMolPerL = Math.Max(0.0, cb);
        OutletCcMolPerL = Math.Max(0.0, cc);
        OutletCdMolPerL = Math.Max(0.0, cd);

        double clampedTOut = Math.Clamp(temp, 150.0, 1500.0);
        OutletTemperatureKelvin = clampedTOut;
        JacketOutletTemperatureKelvin = Math.Clamp(tempJ, 150.0, 1500.0);

        ConversionAPercent = lastState.ConversionAPct;
        YieldBPercent = lastState.YieldBPct;
        SelectivityBPercent = lastState.SelectivityBPct;
        RateR1MolPerLMin = lastState.RateR1MolPerLMin;
        RateR2MolPerLMin = lastState.RateR2MolPerLMin;
        RateR3MolPerLMin = lastState.RateR3MolPerLMin;
        HeatGeneratedWatts = lastState.HeatGeneratedWatts;
        HeatRemovedWatts = lastState.HeatRemovedWatts;
        IsThermalRunaway = runawayObserved;

        double[] zOut = [.. zIn];
        double cReactOut = OutletCaMolPerL + OutletCbMolPerL + OutletCcMolPerL + OutletCdMolPerL;
        double zReactIn = zIn[idxA] + zIn[idxB] + zIn[idxC] + zIn[idxD];

        if (cReactOut > 1e-12 && zReactIn > 1e-12)
        {
            zOut[idxA] = zReactIn * (OutletCaMolPerL / cReactOut);
            zOut[idxB] = zReactIn * (OutletCbMolPerL / cReactOut);
            zOut[idxC] = zReactIn * (OutletCcMolPerL / cReactOut);
            zOut[idxD] = zReactIn * (OutletCdMolPerL / cReactOut);
        }

        NormalizeComposition(zOut);

        // Enforce exact total mass conservation regardless of differences in species molar mass
        double avgOutletMw = 0.0;
        for (int i = 0; i < compounds.Count; i++)
        {
            avgOutletMw += zOut[i] * compounds[i].MolarMass;
        }

        double outletMolarFlowMolPerSec = avgOutletMw > 1e-9
            ? (totalMassFlowKgPerSec / avgOutletMw) * 1000.0
            : InletStream.MolarFlowRateMolPerSec;

        OutletStream.UpdateState(outletMolarFlowMolPerSec, clampedTOut, pOut, zOut, thermo);

        // Overall First-Law enthalpy balance closure across the unit operation for ProcessFlowsheet reporting
        EnergyDutyWatts = OutletStream.TotalEnthalpyFlowWatts - InletStream.TotalEnthalpyFlowWatts;
    }

    private int FindCompoundIndex(IReadOnlyList<ChemicalCompound> compounds, string compoundId)
    {
        for (int i = 0; i < compounds.Count; i++)
        {
            if (string.Equals(compounds[i].Id, compoundId, StringComparison.Ordinal))
                return i;
        }

        throw new InvalidOperationException(
            $"Compound '{compoundId}' required by unit '{Id}' is not present in inlet stream '{InletStream.Id}'.");
    }

    private static void NormalizeComposition(double[] z)
    {
        double sum = 0.0;
        for (int i = 0; i < z.Length; i++)
        {
            z[i] = Math.Max(0.0, z[i]);
            sum += z[i];
        }

        if (sum > 0.0)
        {
            for (int i = 0; i < z.Length; i++)
            {
                z[i] /= sum;
            }
        }
    }
}
