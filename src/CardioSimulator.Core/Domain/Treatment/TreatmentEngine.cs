using System;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// The ACLS rhythm-transition + validation engine (from «логика перехода ритмов», customer 28-08-2026).
/// Pure and side-effect-free apart from updating the passed <see cref="TreatmentContext"/> bookkeeping
/// (doses, CPR/O₂ toggles, failed-defib count, adrenaline priming) — no IO — so it is fully unit-testable.
///
/// <para>Outcomes are <b>probabilistic</b> (per the customer's decision): the same action can succeed or
/// fail. Randomness is injected (<c>rng</c> returns a value in [0,1)) so tests are deterministic. Effect
/// times are returned in real clinical seconds; the treatment screen scales them by the accelerated-clock
/// speed before applying the rhythm change.</para>
/// </summary>
public static class TreatmentEngine
{
    // Effect timings (real clinical seconds) — the screen compresses these via the accelerated clock.
    private const double Instant = 0;
    private const double Sec30 = 30;
    private const double Min2 = 120;
    private const double Min3 = 180;
    private const double Min5 = 300;
    private const double Min10 = 600;
    private const double Min15 = 900;
    private const double Min45 = 2700;   // 30–60 min slow conversion (AFib + amiodarone)

    private static readonly Random Shared = new();

    private static bool IsShockable(ClinicalRhythmState s) =>
        s is ClinicalRhythmState.VentricularFibrillation or ClinicalRhythmState.PulselessVt;

    // ── Validation (the 8 rules) ─────────────────────────────────────────────

    /// <summary>
    /// Judges an action for the current state/context BEFORE it is applied: <see cref="TreatmentVerdict.Block"/>
    /// (contraindicated — don't perform), <see cref="TreatmentVerdict.Warn"/> (risky/ineffective — confirm
    /// first), or <see cref="TreatmentVerdict.Ok"/>. Messages are English seeds the UI localizes.
    /// </summary>
    public static TreatmentValidation Validate(ClinicalRhythmState state, TreatmentAction action, TreatmentContext ctx)
    {
        switch (action)
        {
            case TreatmentAction.Defib d:
                // Rule 1: defibrillation is not indicated for asystole.
                if (state == ClinicalRhythmState.Asystole)
                    return TreatmentValidation.Block("Defibrillation is not indicated for asystole");
                // Rule 2: an UNsynchronized shock on organized VT with a pulse risks R-on-T → VF.
                if (!d.Synchronized && state == ClinicalRhythmState.VentricularTachycardia)
                    return TreatmentValidation.Warn("R-on-T risk → VF: use synchronized cardioversion for pulsed VT");
                return TreatmentValidation.Ok;

            case TreatmentAction.Drug dr:
                // Rule 4: amiodarone 24 h max 2.2 g.
                // Rule 5: atropine max 3 mg.
                if (DrugCatalog.MaxDoseMg(dr.Which) is { } max && ctx.DoseGiven(dr.Which) + dr.DoseMg > max)
                    return TreatmentValidation.Warn($"Max dose exceeded for {dr.Which} (limit {max} mg)");
                // Rule 3: adrenaline in VF/asystole is only effective with CPR running.
                if (dr.Which == TreatmentDrug.Adrenaline && !ctx.CprActive &&
                    (IsShockable(state) || state == ClinicalRhythmState.Asystole))
                    return TreatmentValidation.Warn("Adrenaline needs CPR to be effective");
                return TreatmentValidation.Ok;

            case TreatmentAction.Pacing p:
                // Rule 6: pacing thresholds.
                if (p.CurrentMa < 30)
                    return TreatmentValidation.Warn("Output too low (<30 mA): no capture");
                if (p.CurrentMa > 150)
                    return TreatmentValidation.Warn("Output too high (>150 mA): fibrillation risk");
                return TreatmentValidation.Ok;

            default:
                return TreatmentValidation.Ok;
        }
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to <paramref name="state"/>, updating <paramref name="ctx"/>
    /// bookkeeping and returning the resulting rhythm + effect delay + any warning. A blocked action leaves
    /// the rhythm unchanged. <paramref name="rng"/> (defaults to a shared RNG) returns [0,1) for the
    /// probability draw — inject it for deterministic tests.
    /// </summary>
    public static TreatmentResult Apply(
        ClinicalRhythmState state, TreatmentAction action, TreatmentContext ctx, Func<double>? rng = null)
    {
        rng ??= Shared.NextDouble;

        var v = Validate(state, action, ctx);
        if (v.Verdict == TreatmentVerdict.Block)
            return TreatmentResult.Block(state, v.Message!);
        var warn = v.Message; // Warn proceeds (the UI confirms) but the message is surfaced.

        switch (action)
        {
            case TreatmentAction.SetRhythm sr:
                ResetArrestBookkeeping(ctx, sr.State);
                return new TreatmentResult(sr.State, Instant, warn, false);

            case TreatmentAction.Oxygen ox:
                ctx.OxygenOn = ox.On;
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentAction.Cpr cpr:
                ctx.CprActive = cpr.On;
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentAction.Drug dr:
                ctx.RecordDose(dr.Which, dr.DoseMg);
                return ApplyDrug(state, dr, ctx, rng, warn);

            case TreatmentAction.Defib d:
                return ApplyShock(state, d, ctx, rng, warn);

            case TreatmentAction.Pacing p:
                return ApplyPacing(state, p, warn);

            case TreatmentAction.Vagal vg:
                return ApplyVagal(state, vg, rng, warn);

            default:
                return new TreatmentResult(state, Instant, warn, false);
        }
    }

    // ── Transition rules ─────────────────────────────────────────────────────

    private static TreatmentResult ApplyShock(
        ClinicalRhythmState state, TreatmentAction.Defib d, TreatmentContext ctx, Func<double> rng, string? warn)
    {
        // Synchronized cardioversion — for organized rhythms with a pulse.
        if (d.Synchronized)
        {
            return state switch
            {
                ClinicalRhythmState.VentricularTachycardia =>
                    Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.90), (state, 0.10)),          // 85–90%
                ClinicalRhythmState.AtrialFibrillation =>
                    Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.75), (state, 0.25)),          // 70–80%
                ClinicalRhythmState.Svt =>
                    Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.95), (state, 0.05)),          // when unstable
                _ => new TreatmentResult(state, Instant, warn, false),                                   // no effect otherwise
            };
        }

        // Unsynchronized defibrillation.
        switch (state)
        {
            case ClinicalRhythmState.VentricularFibrillation:
            case ClinicalRhythmState.PulselessVt:
            {
                // Base 75%; adrenaline priming boosts; no CPR degrades with each failed cycle.
                var p = 0.75;
                if (ctx.AdrenalinePrimed) p += 0.10;
                if (!ctx.CprActive) p *= Math.Max(0.3, 1 - 0.15 * ctx.FailedDefibCount);
                p = Math.Clamp(p, 0.05, 0.95);

                if (rng() < p)
                {
                    ResetArrestBookkeeping(ctx, ClinicalRhythmState.Sinus);
                    return new TreatmentResult(ClinicalRhythmState.Sinus, Instant, warn, false);
                }
                // Failed shock: usually the shockable rhythm persists (repeat-shock loop), occasionally asystole.
                ctx.FailedDefibCount++;
                ctx.AdrenalinePrimed = false;
                var newState = rng() < 0.2 ? ClinicalRhythmState.Asystole : state;
                return new TreatmentResult(newState, Instant, warn, false);
            }
            case ClinicalRhythmState.Torsades:
                // Unstable Torsades: shock may organize or degenerate to VF.
                return Pick(rng, Instant, warn,
                    (ClinicalRhythmState.Sinus, 0.6), (ClinicalRhythmState.VentricularFibrillation, 0.4));

            case ClinicalRhythmState.VentricularTachycardia:
                // Async shock on pulsed VT (validator warned): R-on-T → VF.
                return Pick(rng, Instant, warn, (ClinicalRhythmState.VentricularFibrillation, 0.8), (state, 0.2));

            default:
                return new TreatmentResult(state, Instant, warn, false);
        }
    }

    private static TreatmentResult ApplyDrug(
        ClinicalRhythmState state, TreatmentAction.Drug dr, TreatmentContext ctx, Func<double> rng, string? warn)
    {
        switch (dr.Which)
        {
            case TreatmentDrug.Adrenaline:
                if (IsShockable(state))
                {
                    ctx.AdrenalinePrimed = true;                          // primes the next shock; no direct conversion
                    return new TreatmentResult(state, Min2, warn, false);
                }
                if (state == ClinicalRhythmState.Asystole)
                    return Pick(rng, Min3, warn,
                        (ClinicalRhythmState.VentricularFibrillation, 0.1), (state, 0.9)); // rarely re-starts electrical activity
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentDrug.Amiodarone:
                return state switch
                {
                    ClinicalRhythmState.VentricularFibrillation or ClinicalRhythmState.PulselessVt =>
                        Pick(rng, Min10, warn, (ClinicalRhythmState.VentricularTachycardia, 0.6), (ClinicalRhythmState.Sinus, 0.4)),
                    ClinicalRhythmState.VentricularTachycardia =>
                        Pick(rng, Min15, warn, (ClinicalRhythmState.Sinus, 0.85), (state, 0.15)),   // slow
                    ClinicalRhythmState.AtrialFibrillation =>
                        Pick(rng, Min45, warn, (ClinicalRhythmState.Sinus, 0.7), (state, 0.3)),      // 30–60 min
                    _ => new TreatmentResult(state, Instant, warn, false),
                };

            case TreatmentDrug.Atropine:
                if (state == ClinicalRhythmState.CompleteAvBlock)
                    return Pick(rng, Min10, warn, (ClinicalRhythmState.Sinus, 0.5), (state, 0.5)); // if functional block
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentDrug.MagnesiumSulfate:
                if (state == ClinicalRhythmState.Torsades)
                    return Pick(rng, Min15, warn, (ClinicalRhythmState.Sinus, 0.85), (state, 0.15)); // drug of choice
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentDrug.Adenosine:
                if (state == ClinicalRhythmState.Svt)
                    return Pick(rng, Sec30, warn, (ClinicalRhythmState.Sinus, 0.92), (state, 0.08)); // 90–95%, fast
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentDrug.Metoprolol:
                if (state == ClinicalRhythmState.AtrialFibrillation)
                    return new TreatmentResult(ClinicalRhythmState.AtrialFibrillationRateControlled, Min10, warn, false); // rate control, not conversion
                return new TreatmentResult(state, Instant, warn, false);

            // Calcium chloride, nitroglycerin, aspirin: supportive / ACS — no rhythm transition modeled.
            default:
                return new TreatmentResult(state, Instant, warn, false);
        }
    }

    private static TreatmentResult ApplyPacing(ClinicalRhythmState state, TreatmentAction.Pacing p, string? warn)
    {
        // Below the capture threshold nothing changes (the validator already warned).
        if (p.CurrentMa < 30) return new TreatmentResult(state, Instant, warn, false);
        // Complete AV block captures into an artificial paced rhythm.
        if (state == ClinicalRhythmState.CompleteAvBlock)
            return new TreatmentResult(ClinicalRhythmState.Paced, Instant, warn, false);
        return new TreatmentResult(state, Instant, warn, false);
    }

    private static TreatmentResult ApplyVagal(
        ClinicalRhythmState state, TreatmentAction.Vagal vg, Func<double> rng, string? warn)
    {
        if (state == ClinicalRhythmState.Svt)
            return Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.22), (state, 0.78)); // 20–25%
        return new TreatmentResult(state, Instant, warn, false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Probability-weighted pick among outcomes (weights need not sum to exactly 1; the last is the
    /// fallback). Returns the effect delay + warning with the drawn state.</summary>
    private static TreatmentResult Pick(
        Func<double> rng, double effectSeconds, string? warn,
        params (ClinicalRhythmState State, double Weight)[] outcomes)
    {
        var r = rng();
        var cumulative = 0.0;
        for (var i = 0; i < outcomes.Length; i++)
        {
            cumulative += outcomes[i].Weight;
            if (r < cumulative || i == outcomes.Length - 1)
                return new TreatmentResult(outcomes[i].State, effectSeconds, warn, false);
        }
        return new TreatmentResult(outcomes[^1].State, effectSeconds, warn, false);
    }

    /// <summary>On leaving an arrest into a perfusing/organized rhythm, clears the shock counters.</summary>
    private static void ResetArrestBookkeeping(TreatmentContext ctx, ClinicalRhythmState newState)
    {
        if (newState is ClinicalRhythmState.VentricularFibrillation or ClinicalRhythmState.PulselessVt
            or ClinicalRhythmState.Asystole) return;
        ctx.FailedDefibCount = 0;
        ctx.AdrenalinePrimed = false;
    }
}
