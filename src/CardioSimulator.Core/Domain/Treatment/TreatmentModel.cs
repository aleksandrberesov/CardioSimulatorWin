using System.Collections.Generic;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// The abstract clinical rhythm states the treatment (ACLS) engine reasons over — decoupled from the
/// concrete dataset pathologies (a separate resolver maps a state to a representative rhythm to display).
/// Superset of the treatment-panel dropdown and every state referenced by the transition table
/// («логика перехода ритмов», customer 28-08-2026).
/// </summary>
public enum ClinicalRhythmState
{
    /// <summary>Normal sinus rhythm — the "recovered" target state.</summary>
    Sinus,
    SinusTachycardia,
    AtrialFibrillation,
    /// <summary>AFib with a controlled ventricular rate («ФП с ЧЖС ↓») — rate control, not conversion.</summary>
    AtrialFibrillationRateControlled,
    /// <summary>Supraventricular tachycardia («СВТ»).</summary>
    Svt,
    /// <summary>Ventricular tachycardia WITH a pulse («ЖТ с пульсом») — cardiovert, don't defibrillate async.</summary>
    VentricularTachycardia,
    /// <summary>Pulseless VT («бЖТ») — shockable, grouped clinically with VF.</summary>
    PulselessVt,
    /// <summary>Ventricular fibrillation («ФЖ») — shockable.</summary>
    VentricularFibrillation,
    /// <summary>Torsades de pointes / polymorphic VT («пируэтная тахикардия»).</summary>
    Torsades,
    /// <summary>Asystole («асистолия») — non-shockable flatline.</summary>
    Asystole,
    /// <summary>Third-degree (complete) AV block («АВ-блокада III степени»).</summary>
    CompleteAvBlock,
    /// <summary>Artificial paced rhythm («искусственный ритм»/ЭКС) after successful capture.</summary>
    Paced,
}

/// <summary>The drugs the treatment panel can give (IV or per-os). Doses/limits live in <see cref="DrugCatalog"/>.</summary>
public enum TreatmentDrug
{
    Adrenaline,
    Amiodarone,
    Atropine,
    MagnesiumSulfate,
    CalciumChloride,
    Adenosine,
    Metoprolol,
    Nitroglycerin,
    Aspirin,
}

/// <summary>Vagal maneuvers offered for СВТ.</summary>
public enum VagalManeuver
{
    Valsalva,
    CarotidSinusMassage,
}

/// <summary>
/// A treatment action applied to the patient. A discriminated union (abstract record + cases) so the engine
/// pattern-matches on the concrete action and its parameters. Each case mirrors a card on the treatment panel.
/// </summary>
public abstract record TreatmentAction
{
    /// <summary>IV or per-os drug at a dose (mg). Route/indication metadata is in <see cref="DrugCatalog"/>.</summary>
    public sealed record Drug(TreatmentDrug Which, double DoseMg) : TreatmentAction;

    /// <summary>A shock. <paramref name="Synchronized"/> = synchronized cardioversion (safe for organized
    /// rhythms with a pulse); unsynchronized = defibrillation (correct for VF/pulseless VT, dangerous R-on-T
    /// for pulsed VT).</summary>
    public sealed record Defib(int EnergyJoules, bool Synchronized) : TreatmentAction;

    /// <summary>Transcutaneous/transvenous pacing at a rate (bpm) and output current (mA).</summary>
    public sealed record Pacing(int RateBpm, int CurrentMa) : TreatmentAction;

    /// <summary>A vagal maneuver (for СВТ).</summary>
    public sealed record Vagal(VagalManeuver Maneuver) : TreatmentAction;

    /// <summary>Toggle supplemental oxygen / ventilation.</summary>
    public sealed record Oxygen(bool On) : TreatmentAction;

    /// <summary>Toggle chest compressions (СЛР).</summary>
    public sealed record Cpr(bool On) : TreatmentAction;

    /// <summary>Instructor override: set the rhythm directly (the panel's «Установить ритм»).</summary>
    public sealed record SetRhythm(ClinicalRhythmState State) : TreatmentAction;
}

/// <summary>
/// Mutable scenario state the transition rules read (CPR/O₂ status, how many defibs have failed, cumulative
/// drug doses for max-dose checks, and elapsed sim time). The screen owns one instance and mutates it as
/// actions are applied; the engine only reads it.
/// </summary>
public sealed class TreatmentContext
{
    public bool CprActive { get; set; }
    public bool OxygenOn { get; set; }

    /// <summary>Consecutive defibrillations that did NOT convert to a perfusing rhythm — gates amiodarone
    /// (recommended after the 3rd failed shock) and degrades defib success when CPR is not running.</summary>
    public int FailedDefibCount { get; set; }

    /// <summary>Cumulative dose given per drug (mg), for the 24 h max-dose limits (amiodarone 2.2 g, atropine
    /// 3 mg, …). Reset per scenario.</summary>
    public Dictionary<TreatmentDrug, double> CumulativeDoseMg { get; } = new();

    /// <summary>True after adrenaline OR amiodarone is given in a shockable rhythm — "primes" the next shock
    /// (the spec's <c>increases_defib_success</c> effect: both drugs are adjuncts that improve defibrillation
    /// rather than converting VF/pVT themselves). Cleared on a successful conversion or a failed shock.</summary>
    public bool AdrenalinePrimed { get; set; }

    /// <summary>Elapsed simulated seconds (the accelerated clock), for effect-timing bookkeeping.</summary>
    public double ElapsedSeconds { get; set; }

    public double DoseGiven(TreatmentDrug drug) =>
        CumulativeDoseMg.TryGetValue(drug, out var mg) ? mg : 0;

    public void RecordDose(TreatmentDrug drug, double mg) =>
        CumulativeDoseMg[drug] = DoseGiven(drug) + mg;

    /// <summary>Clears the scenario back to a fresh patient (no CPR/O₂, no doses, no failed shocks).</summary>
    public void Reset()
    {
        CprActive = false;
        OxygenOn = false;
        FailedDefibCount = 0;
        AdrenalinePrimed = false;
        CumulativeDoseMg.Clear();
        ElapsedSeconds = 0;
    }
}

/// <summary>
/// A language-neutral reason code for a validation block/warning or an applied warning. The Core engine
/// returns these instead of English prose so the UI can localize them (the App maps each to an
/// <c>AppStrings</c> entry, inlining the drug name/limit for <see cref="MaxDoseExceeded"/> from the action).
/// <see cref="None"/> = no message.
/// </summary>
public enum TreatmentReason
{
    None = 0,
    /// <summary>Defibrillation is contraindicated in asystole.</summary>
    DefibNotIndicatedAsystole,
    /// <summary>Unsynchronized shock on pulsed VT risks R-on-T → VF; use synchronized cardioversion.</summary>
    RonTUseSyncCardioversion,
    /// <summary>Unsynchronized shock on an organized/perfusing rhythm risks R-on-T; use sync cardioversion.</summary>
    UnsyncShockOrganizedRhythm,
    /// <summary>The cumulative dose would exceed the drug's 24 h maximum.</summary>
    MaxDoseExceeded,
    /// <summary>Adrenaline in a shockable/arrest rhythm needs CPR to circulate to be effective.</summary>
    AdrenalineNeedsCpr,
    /// <summary>Pacing output below the capture threshold (&lt; 30 mA): no capture.</summary>
    PacingOutputTooLow,
    /// <summary>Pacing output above the safe threshold (&gt; 150 mA): fibrillation risk.</summary>
    PacingOutputTooHigh,
    /// <summary>Atropine is generally ineffective in high-degree/complete AV block — pace instead.</summary>
    AtropineIneffectiveHighBlock,
}

/// <summary>How an action was judged before it is applied: allowed, allowed-with-warning (needs a confirm),
/// or blocked (not performed).</summary>
public enum TreatmentVerdict
{
    Ok,
    /// <summary>Clinically risky/ineffective but permitted — surface <see cref="TreatmentValidation.Reason"/>
    /// and require confirmation (e.g. async shock on pulsed VT → R-on-T).</summary>
    Warn,
    /// <summary>Contraindicated — do not perform; show <see cref="TreatmentValidation.Reason"/> (e.g.
    /// defibrillation of asystole).</summary>
    Block,
}

/// <summary>The validator's judgement of an action for the current state/context. <see cref="Reason"/> carries
/// a language-neutral code the UI localizes (see <see cref="TreatmentReason"/>).</summary>
public readonly record struct TreatmentValidation(TreatmentVerdict Verdict, TreatmentReason Reason)
{
    public static readonly TreatmentValidation Ok = new(TreatmentVerdict.Ok, TreatmentReason.None);
    public static TreatmentValidation Warn(TreatmentReason reason) => new(TreatmentVerdict.Warn, reason);
    public static TreatmentValidation Block(TreatmentReason reason) => new(TreatmentVerdict.Block, reason);
}

/// <summary>
/// The outcome of applying an action: the resulting rhythm state, how long (in <em>real</em> clinical
/// seconds) before it takes effect (the screen scales this by the accelerated-clock speed; 0 = instant),
/// an optional warning reason to surface (<see cref="TreatmentReason.None"/> = none), and whether the action
/// was blocked (no change).
/// </summary>
public readonly record struct TreatmentResult(
    ClinicalRhythmState NewState,
    double EffectSeconds,
    TreatmentReason Warning,
    bool Blocked)
{
    /// <summary>A blocked action: the rhythm is unchanged and a reason explains why.</summary>
    public static TreatmentResult Block(ClinicalRhythmState current, TreatmentReason reason) =>
        new(current, 0, reason, true);
}
