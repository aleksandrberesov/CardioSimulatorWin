using System.Collections.Generic;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>Route of administration for a drug.</summary>
public enum DrugRoute
{
    IvBolus,
    IvSlow,
    Sublingual,
    Oral,
}

/// <summary>
/// Reference data for one drug: the standard first dose the panel pre-fills, the 24 h maximum the validator
/// enforces (null = no hard cap modeled), and its route. Mirrors the «Стандартные дозировки» table in the
/// spec (customer 28-08-2026). Purely data — display names/indications are localized in the UI.
/// </summary>
public sealed record DrugInfo(
    TreatmentDrug Drug,
    double StandardDoseMg,
    double? MaxDoseMg,
    DrugRoute Route);

/// <summary>The standard-dose / max-dose catalog used by the treatment panel (defaults) and the
/// <see cref="TreatmentEngine"/> validator (dose caps).</summary>
public static class DrugCatalog
{
    private static readonly IReadOnlyDictionary<TreatmentDrug, DrugInfo> Table = new Dictionary<TreatmentDrug, DrugInfo>
    {
        [TreatmentDrug.Adrenaline]      = new(TreatmentDrug.Adrenaline,      1.0,  null,  DrugRoute.IvBolus),   // every 3-5 min
        [TreatmentDrug.Amiodarone]      = new(TreatmentDrug.Amiodarone,      300,  2200,  DrugRoute.IvBolus),   // 300 then 150; max 2.2 g/day
        [TreatmentDrug.Atropine]        = new(TreatmentDrug.Atropine,        0.5,  3.0,   DrugRoute.IvBolus),   // every 3-5 min, max 3 mg
        [TreatmentDrug.MagnesiumSulfate]= new(TreatmentDrug.MagnesiumSulfate,2000, null,  DrugRoute.IvBolus),   // 1-2 g, once (Torsades)
        [TreatmentDrug.CalciumChloride] = new(TreatmentDrug.CalciumChloride, 1000, null,  DrugRoute.IvBolus),
        [TreatmentDrug.Adenosine]       = new(TreatmentDrug.Adenosine,       6.0,  18.0,  DrugRoute.IvBolus),   // 6 then 12; max 2 doses
        [TreatmentDrug.Metoprolol]      = new(TreatmentDrug.Metoprolol,      5.0,  15.0,  DrugRoute.IvSlow),    // every 5 min, max 15 mg
        [TreatmentDrug.Nitroglycerin]   = new(TreatmentDrug.Nitroglycerin,   0.4,  1.2,   DrugRoute.Sublingual),// every 5 min, max 3 doses
        [TreatmentDrug.Aspirin]         = new(TreatmentDrug.Aspirin,         300,  325,   DrugRoute.Oral),      // once
    };

    public static DrugInfo Info(TreatmentDrug drug) => Table[drug];

    public static double StandardDoseMg(TreatmentDrug drug) => Table[drug].StandardDoseMg;

    /// <summary>The 24 h max dose (mg), or null when no hard cap is modeled.</summary>
    public static double? MaxDoseMg(TreatmentDrug drug) => Table[drug].MaxDoseMg;
}
