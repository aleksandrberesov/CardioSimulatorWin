using System;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// The ACLS rhythm-transition + validation engine (from «логика перехода ритмов», customer 28-08-2026).
/// Pure and side-effect-free apart from updating the passed <see cref="TreatmentContext"/> bookkeeping
/// (doses, CPR/O₂ toggles, failed-defib count, shock priming) — no IO — so it is fully unit-testable.
///
/// <para>Outcomes are <b>probabilistic</b> (per the customer's decision): the same action can succeed or
/// fail. Randomness is injected (<c>rng</c> returns a value in [0,1)) so tests are deterministic. Effect
/// times are returned in real clinical seconds; the treatment screen scales them by the accelerated-clock
/// speed before applying the rhythm change. Validation/warning text is returned as a language-neutral
/// <see cref="TreatmentReason"/> the App localizes.</para>
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

    /// <summary>Probability that amiodarone provokes Torsades de pointes when given for an organized
    /// tachyarrhythmia — QT-prolongation proarrhythmia. Rare, but the teaching point (recognise + rescue with
    /// magnesium) is the reason it is modelled at all.</summary>
    private const double AmiodaroneTorsadesRisk = 0.05;

    private static readonly Random Shared = new();

    private static bool IsShockable(ClinicalRhythmState s) =>
        s is ClinicalRhythmState.VentricularFibrillation or ClinicalRhythmState.PulselessVt;

    // Organized/perfusing rhythms for which an UNsynchronized shock is contraindicated (R-on-T risk) —
    // i.e. everything that is neither shockable (VF/pVT/Torsades), pulsed VT (its own R-on-T rule), nor
    // asystole (blocked). An unsync shock here should warn, not silently no-op.
    private static bool IsOrganizedNonShockable(ClinicalRhythmState s) =>
        s is ClinicalRhythmState.Sinus
          or ClinicalRhythmState.SinusTachycardia
          or ClinicalRhythmState.AtrialFibrillation
          or ClinicalRhythmState.AtrialFibrillationRateControlled
          or ClinicalRhythmState.Svt
          or ClinicalRhythmState.CompleteAvBlock
          or ClinicalRhythmState.Paced;

    // ── Validation (the rules) ───────────────────────────────────────────────

    /// <summary>
    /// Judges an action for the current state/context BEFORE it is applied: <see cref="TreatmentVerdict.Block"/>
    /// (contraindicated — don't perform), <see cref="TreatmentVerdict.Warn"/> (risky/ineffective — confirm
    /// first), or <see cref="TreatmentVerdict.Ok"/>. The reason is a language-neutral <see cref="TreatmentReason"/>.
    /// </summary>
    public static TreatmentValidation Validate(ClinicalRhythmState state, TreatmentAction action, TreatmentContext ctx)
    {
        switch (action)
        {
            case TreatmentAction.Defib d:
                // Rule: defibrillation is not indicated for asystole (sync or unsync).
                if (state == ClinicalRhythmState.Asystole)
                    return TreatmentValidation.Block(TreatmentReason.DefibNotIndicatedAsystole);
                // Rule: an UNsynchronized shock on organized VT with a pulse risks R-on-T → VF.
                if (!d.Synchronized && state == ClinicalRhythmState.VentricularTachycardia)
                    return TreatmentValidation.Warn(TreatmentReason.RonTUseSyncCardioversion);
                // Rule: an UNsynchronized shock on any organized/perfusing rhythm risks R-on-T → VF.
                if (!d.Synchronized && IsOrganizedNonShockable(state))
                    return TreatmentValidation.Warn(TreatmentReason.UnsyncShockOrganizedRhythm);
                return TreatmentValidation.Ok;

            case TreatmentAction.Drug dr:
                // Rule: 24 h dose caps (amiodarone 2.2 g, atropine 3 mg, …).
                if (DrugCatalog.MaxDoseMg(dr.Which) is { } max && ctx.DoseGiven(dr.Which) + dr.DoseMg > max)
                    return TreatmentValidation.Warn(TreatmentReason.MaxDoseExceeded);
                // Rule: adrenaline in VF/asystole is only effective with CPR running.
                if (dr.Which == TreatmentDrug.Adrenaline && !ctx.CprActive &&
                    (IsShockable(state) || state == ClinicalRhythmState.Asystole))
                    return TreatmentValidation.Warn(TreatmentReason.AdrenalineNeedsCpr);
                // Rule: atropine is generally ineffective in complete (high-degree) AV block — pace instead.
                if (dr.Which == TreatmentDrug.Atropine && state == ClinicalRhythmState.CompleteAvBlock)
                    return TreatmentValidation.Warn(TreatmentReason.AtropineIneffectiveHighBlock);
                return TreatmentValidation.Ok;

            case TreatmentAction.Pacing p:
                // Rule: pacing capture thresholds.
                if (p.CurrentMa < 30)
                    return TreatmentValidation.Warn(TreatmentReason.PacingOutputTooLow);
                if (p.CurrentMa > 150)
                    return TreatmentValidation.Warn(TreatmentReason.PacingOutputTooHigh);
                return TreatmentValidation.Ok;

            default:
                return TreatmentValidation.Ok;
        }
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies <paramref name="action"/> to <paramref name="state"/>, updating <paramref name="ctx"/>
    /// bookkeeping and returning the resulting rhythm + effect delay + any warning reason. A blocked action
    /// leaves the rhythm unchanged. <paramref name="rng"/> (defaults to a shared RNG) returns [0,1) for the
    /// probability draw — inject it for deterministic tests.
    /// </summary>
    public static TreatmentResult Apply(
        ClinicalRhythmState state, TreatmentAction action, TreatmentContext ctx, Func<double>? rng = null)
    {
        rng ??= Shared.NextDouble;

        var v = Validate(state, action, ctx);
        if (v.Verdict == TreatmentVerdict.Block)
            return TreatmentResult.Block(state, v.Reason);
        var warn = v.Reason; // Warn proceeds (the UI confirms) but the reason is surfaced.

        TreatmentResult result;
        switch (action)
        {
            case TreatmentAction.SetRhythm sr:
                // Instructor override: a manual rhythm change starts a fresh scenario for the new rhythm —
                // never leak the previous rhythm's shock priming / failed-shock count into it.
                ctx.FailedDefibCount = 0;
                ctx.AdrenalinePrimed = false;
                return new TreatmentResult(sr.State, Instant, warn, false);

            case TreatmentAction.Oxygen ox:
                ctx.OxygenOn = ox.On;
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentAction.Cpr cpr:
                ctx.CprActive = cpr.On;
                return new TreatmentResult(state, Instant, warn, false);

            case TreatmentAction.Drug dr:
                // A blank/zero/negative dose administers nothing (guards the dose NumberBox and cumulative
                // bookkeeping — a negative dose must not decrement the running total or defeat the cap).
                if (dr.DoseMg <= 0)
                    return new TreatmentResult(state, Instant, warn, false);
                ctx.RecordDose(dr.Which, dr.DoseMg);
                result = ApplyDrug(state, dr, ctx, rng, warn);
                break;

            case TreatmentAction.Defib d:
                result = ApplyShock(state, d, ctx, rng, warn);
                break;

            case TreatmentAction.Pacing p:
                result = ApplyPacing(state, p, warn);
                break;

            case TreatmentAction.Vagal vg:
                result = ApplyVagal(state, vg, rng, warn);
                break;

            default:
                return new TreatmentResult(state, Instant, warn, false);
        }

        // Whenever therapy moves the patient OUT of a pulseless-arrest rhythm into an organized one, clear the
        // arrest bookkeeping so a later arrest starts fresh (covers defib success and any drug conversion).
        if (TreatmentRhythmMap.IsArrestRhythm(state) && !TreatmentRhythmMap.IsArrestRhythm(result.NewState))
        {
            ctx.FailedDefibCount = 0;
            ctx.AdrenalinePrimed = false;
        }
        return result;
    }

    // ── Transition rules ─────────────────────────────────────────────────────

    private static TreatmentResult ApplyShock(
        ClinicalRhythmState state, TreatmentAction.Defib d, TreatmentContext ctx, Func<double> rng, TreatmentReason warn)
    {
        // Synchronized cardioversion — for organized rhythms with a pulse.
        if (d.Synchronized)
        {
            return state switch
            {
                ClinicalRhythmState.VentricularTachycardia =>
                    Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.90), (state, 0.10)),          // 85–90%
                ClinicalRhythmState.AtrialFibrillation or ClinicalRhythmState.AtrialFibrillationRateControlled =>
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
                // Base 75%; shock priming (adrenaline/amiodarone) boosts; no CPR degrades with each failed cycle.
                var p = 0.75;
                if (ctx.AdrenalinePrimed) p += 0.10;
                if (!ctx.CprActive) p *= Math.Max(0.3, 1 - 0.15 * ctx.FailedDefibCount);
                p = Math.Clamp(p, 0.05, 0.95);

                if (rng() < p)
                {
                    // Success → perfusing sinus; Apply() clears the arrest bookkeeping on the arrest→organized exit.
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
                // Organized/perfusing rhythm (validator warned): no modeled rhythm change.
                return new TreatmentResult(state, Instant, warn, false);
        }
    }

    private static TreatmentResult ApplyDrug(
        ClinicalRhythmState state, TreatmentAction.Drug dr, TreatmentContext ctx, Func<double> rng, TreatmentReason warn)
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
                // Amiodarone is an antiarrhythmic ADJUNCT in shockable arrest: it improves the next shock's
                // success, it does not itself terminate VF/pulseless VT (mirrors adrenaline's priming).
                if (IsShockable(state))
                {
                    ctx.AdrenalinePrimed = true;
                    return new TreatmentResult(state, Instant, warn, false);
                }
                // Proarrhythmia: amiodarone prolongs the QT interval and can rarely provoke Torsades de pointes
                // when given for an organized tachyarrhythmia (drug-induced long-QT). Drawn before the
                // therapeutic effect; the `&&` short-circuits so no rng is consumed for other rhythms.
                if (state is ClinicalRhythmState.VentricularTachycardia
                           or ClinicalRhythmState.AtrialFibrillation
                           or ClinicalRhythmState.AtrialFibrillationRateControlled
                    && rng() < AmiodaroneTorsadesRisk)
                {
                    return new TreatmentResult(ClinicalRhythmState.Torsades, Min2, warn, false);
                }
                return state switch
                {
                    ClinicalRhythmState.VentricularTachycardia =>
                        Pick(rng, Min15, warn, (ClinicalRhythmState.Sinus, 0.85), (state, 0.15)),   // slow
                    ClinicalRhythmState.AtrialFibrillation or ClinicalRhythmState.AtrialFibrillationRateControlled =>
                        Pick(rng, Min45, warn, (ClinicalRhythmState.Sinus, 0.7), (state, 0.3)),      // 30–60 min
                    _ => new TreatmentResult(state, Instant, warn, false),
                };

            case TreatmentDrug.Atropine:
                // Atropine acts at the AV node; complete (typically infranodal) block responds poorly — model a
                // low success and let the validator's warning steer toward pacing.
                if (state == ClinicalRhythmState.CompleteAvBlock)
                    return Pick(rng, Min10, warn, (ClinicalRhythmState.Sinus, 0.1), (state, 0.9));
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
                if (state is ClinicalRhythmState.AtrialFibrillation)
                    return new TreatmentResult(ClinicalRhythmState.AtrialFibrillationRateControlled, Min10, warn, false); // rate control, not conversion
                return new TreatmentResult(state, Instant, warn, false);

            // Calcium chloride, nitroglycerin, aspirin: supportive / ACS — no rhythm transition modeled.
            default:
                return new TreatmentResult(state, Instant, warn, false);
        }
    }

    private static TreatmentResult ApplyPacing(ClinicalRhythmState state, TreatmentAction.Pacing p, TreatmentReason warn)
    {
        // Below the capture threshold nothing changes (the validator already warned).
        if (p.CurrentMa < 30) return new TreatmentResult(state, Instant, warn, false);
        // Complete AV block captures into an artificial paced rhythm.
        if (state == ClinicalRhythmState.CompleteAvBlock)
            return new TreatmentResult(ClinicalRhythmState.Paced, Instant, warn, false);
        return new TreatmentResult(state, Instant, warn, false);
    }

    private static TreatmentResult ApplyVagal(
        ClinicalRhythmState state, TreatmentAction.Vagal vg, Func<double> rng, TreatmentReason warn)
    {
        if (state == ClinicalRhythmState.Svt)
            return Pick(rng, Instant, warn, (ClinicalRhythmState.Sinus, 0.22), (state, 0.78)); // 20–25%
        return new TreatmentResult(state, Instant, warn, false);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Probability-weighted pick among outcomes (weights need not sum to exactly 1; the last is the
    /// fallback). Returns the effect delay + warning with the drawn state.</summary>
    private static TreatmentResult Pick(
        Func<double> rng, double effectSeconds, TreatmentReason warn,
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
}
