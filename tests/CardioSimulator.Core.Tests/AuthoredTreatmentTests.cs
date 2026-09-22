using System.Collections.Generic;
using CardioSimulator.Core.Domain.Treatment;
using Xunit;

namespace CardioSimulator.Core.Tests;

using S = ClinicalRhythmState;

/// <summary>
/// The authored-transition path: when an <see cref="AuthoredTreatmentTable"/> has a rule for the current
/// state+action, its weighted outcome (and effect timing) govern the result instead of the built-in rule —
/// this is how the editable «Протоколы лечения» drive the Лечение panel. Validation and dose bookkeeping
/// still run; unmatched actions fall back to the built-in engine.
/// </summary>
public class AuthoredTreatmentTests
{
    private static System.Func<double> Seq(params double[] values)
    {
        var q = new Queue<double>(values);
        return () => q.Count > 0 ? q.Dequeue() : 0.0;
    }

    private static TreatmentAction Drug(TreatmentDrug d, double mg) => new TreatmentAction.Drug(d, mg);
    private static TreatmentAction Shock(int j, bool sync) => new TreatmentAction.Defib(j, sync);

    private static AuthoredTreatmentTable Table(params AuthoredTransition[] t) => new(t);

    private static AuthoredTransition Rule(S from, AuthoredTrigger trig, TreatmentDrug? drug, double effect,
        params (S state, double weight)[] outcomes)
    {
        var list = new List<AuthoredOutcome>();
        foreach (var o in outcomes) list.Add(new AuthoredOutcome(o.state, o.weight));
        return new AuthoredTransition(from, trig, drug, list, effect);
    }

    /// <summary>A rule bound to an instructor-authored custom drug (by its stable id) rather than a catalog drug.</summary>
    private static AuthoredTransition CustomRule(S from, string customDrugId, double effect,
        params (S state, double weight)[] outcomes)
    {
        var list = new List<AuthoredOutcome>();
        foreach (var o in outcomes) list.Add(new AuthoredOutcome(o.state, o.weight));
        return new AuthoredTransition(from, AuthoredTrigger.Drug, null, list, effect, FromPathologyId: null,
            CustomDrugId: customDrugId);
    }

    /// <summary>A custom drug as the treatment panel sends it: the enum slot is a placeholder, the id is the
    /// identity.</summary>
    private static TreatmentAction CustomDrug(string id, double mg, TreatmentDrug slot = TreatmentDrug.Adrenaline) =>
        new TreatmentAction.Drug(slot, mg, CustomName: "Custom " + id, CustomDrugId: id);

    [Fact]
    public void Authored_Rule_Overrides_BuiltIn_Outcome()
    {
        // Built-in VF+defib is probabilistic; an authored rule that always yields sinus must win regardless of rng.
        var table = Table(Rule(S.VentricularFibrillation, AuthoredTrigger.Defibrillation, null, 0, (S.Sinus, 1.0)));
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), new TreatmentContext(), table, Seq(0.99));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.False(r.Blocked);
    }

    [Fact]
    public void Authored_Weighted_Outcomes_PickByDraw()
    {
        // (VF, defib) → sinus .75 / asystole .25 (as the default set merges rows 1+2).
        var table = Table(Rule(S.VentricularFibrillation, AuthoredTrigger.Defibrillation, null, 0,
            (S.Sinus, 0.75), (S.Asystole, 0.25)));
        var success = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.Sinus, success.NewState);
        var failure = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), new TreatmentContext(), table, Seq(0.99));
        Assert.Equal(S.Asystole, failure.NewState);
    }

    [Fact]
    public void Authored_EffectSeconds_Are_Returned()
    {
        var table = Table(Rule(S.AtrialFibrillation, AuthoredTrigger.Drug, TreatmentDrug.Amiodarone, 2700, (S.Sinus, 0.7)));
        var r = TreatmentEngine.Apply(S.AtrialFibrillation, Drug(TreatmentDrug.Amiodarone, 300), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(2700, r.EffectSeconds);
    }

    [Fact]
    public void Drug_Trigger_Matches_The_Specific_Drug_Else_FallsBack()
    {
        // Authored rule only for amiodarone on VF. Amiodarone → authored VT; adrenaline → built-in (primes, no change).
        var table = Table(Rule(S.VentricularFibrillation, AuthoredTrigger.Drug, TreatmentDrug.Amiodarone, 0, (S.VentricularTachycardia, 1.0)));
        var amio = TreatmentEngine.Apply(S.VentricularFibrillation, Drug(TreatmentDrug.Amiodarone, 300), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.VentricularTachycardia, amio.NewState);

        var adr = TreatmentEngine.Apply(S.VentricularFibrillation, Drug(TreatmentDrug.Adrenaline, 1), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.VentricularFibrillation, adr.NewState); // built-in: adrenaline primes, no rhythm change
    }

    [Fact]
    public void Unmatched_Action_FallsBack_To_BuiltIn()
    {
        // Table only covers VT+cardioversion; adenosine on SVT is unmatched → built-in converts SVT → sinus.
        var table = Table(Rule(S.VentricularTachycardia, AuthoredTrigger.SyncCardioversion, null, 0, (S.Sinus, 1.0)));
        var r = TreatmentEngine.Apply(S.Svt, Drug(TreatmentDrug.Adenosine, 6), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
    }

    [Fact]
    public void Validation_Still_Blocks_Even_With_An_Authored_Rule()
    {
        // Even if someone authors an asystole+defib rule, the validator's contraindication block still wins.
        var table = Table(Rule(S.Asystole, AuthoredTrigger.Defibrillation, null, 0, (S.Sinus, 1.0)));
        var r = TreatmentEngine.Apply(S.Asystole, Shock(200, false), new TreatmentContext(), table, Seq(0.0));
        Assert.True(r.Blocked);
        Assert.Equal(S.Asystole, r.NewState);
    }

    [Fact]
    public void Empty_Table_Uses_BuiltIn()
    {
        var table = new AuthoredTreatmentTable(null);
        Assert.True(table.IsEmpty);
        var r = TreatmentEngine.Apply(S.Svt, new TreatmentAction.Vagal(VagalManeuver.Valsalva), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState); // built-in vagal success
    }

    [Fact]
    public void Authored_Rule_Matches_Specific_PathologyId_And_Returns_TargetPathologyId()
    {
        // Issue 1: "В протоколах лечения добавить протокол, который меняет синусовый ритм 100 на искусственный ЭКС 26 по пробе Вальсальвы"
        var rule = new AuthoredTransition(
            From: S.Sinus,
            Trigger: AuthoredTrigger.Vagal,
            Drug: null,
            Outcomes: new[] { new AuthoredOutcome(S.Paced, 1.0, TargetPathologyId: "26") },
            EffectSeconds: 0,
            FromPathologyId: "100");
        var table = Table(rule);

        // When current pathology is "100" -> rule matches, returns Paced and TargetPathologyId = "26"
        var r = TreatmentEngine.Apply(S.Sinus, new TreatmentAction.Vagal(VagalManeuver.Valsalva), new TreatmentContext(), table, Seq(0.0), currentPathologyId: "100");
        Assert.Equal(S.Paced, r.NewState);
        Assert.Equal("26", r.TargetPathologyId);

        // When current pathology is "101" -> specific rule for "100" does NOT match; falls back to built-in (sinus + vagal = sinus)
        var fallback = TreatmentEngine.Apply(S.Sinus, new TreatmentAction.Vagal(VagalManeuver.Valsalva), new TreatmentContext(), table, Seq(0.0), currentPathologyId: "101");
        Assert.Equal(S.Sinus, fallback.NewState);
        Assert.Null(fallback.TargetPathologyId);
    }

    [Fact]
    public void SyntheticAsystole_And_Torsades_Have_Valid_Titles_And_Classify_Correctly()
    {
        // Issue 2: "Нет названия у ритма Асистолия"
        var asystole = CardioSimulator.Core.Domain.PathologyEntry.SyntheticAsystole;
        Assert.NotNull(asystole);
        Assert.Equal("asystole", asystole.Id);
        Assert.False(string.IsNullOrWhiteSpace(asystole.TitleEn));
        Assert.False(string.IsNullOrWhiteSpace(asystole.NameRu));
        Assert.Equal(ClinicalRhythmState.Asystole, TreatmentRhythmMap.ClassifyByAcronyms(asystole.AcronymList));

        var torsades = CardioSimulator.Core.Domain.PathologyEntry.SyntheticTorsades;
        Assert.NotNull(torsades);
        Assert.Equal("torsades", torsades.Id);
        Assert.False(string.IsNullOrWhiteSpace(torsades.TitleEn));
        Assert.False(string.IsNullOrWhiteSpace(torsades.NameRu));
        Assert.Equal(ClinicalRhythmState.Torsades, TreatmentRhythmMap.ClassifyByAcronyms(torsades.AcronymList));
    }
    // ── Custom drugs ─────────────────────────────────────────────────────────
    // A custom drug fills the action's TreatmentDrug slot with an arbitrary catalog value; only its
    // CustomDrugId identifies it, so the authored table must match on the id and the built-in catalog rules
    // (dose caps, adrenaline priming) must not apply to it.

    [Fact]
    public void Custom_Drug_Fires_Its_Own_Authored_Rule()
    {
        // "Custom drug X on VF → sinus" — the panel sends the adrenaline slot, the id picks the rule.
        var table = Table(CustomRule(S.VentricularFibrillation, "drug-x", 120, (S.Sinus, 1.0)));
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, CustomDrug("drug-x", 5), new TreatmentContext(),
            table, Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(120, r.EffectSeconds);
    }

    [Fact]
    public void Custom_Drug_Does_Not_Match_A_Rule_For_The_Enum_Slot_It_Borrows()
    {
        // The enum slot is adrenaline, but an adrenaline rule must NOT fire for a custom drug…
        var table = Table(Rule(S.VentricularFibrillation, AuthoredTrigger.Drug, TreatmentDrug.Adrenaline, 0,
            (S.Asystole, 1.0)));
        var custom = TreatmentEngine.Apply(S.VentricularFibrillation, CustomDrug("drug-x", 5), new TreatmentContext(),
            table, Seq(0.0));
        Assert.Equal(S.VentricularFibrillation, custom.NewState);

        // …while real adrenaline still does.
        var real = TreatmentEngine.Apply(S.VentricularFibrillation, Drug(TreatmentDrug.Adrenaline, 1),
            new TreatmentContext { CprActive = true }, table, Seq(0.0));
        Assert.Equal(S.Asystole, real.NewState);
    }

    [Fact]
    public void Standard_Drug_Does_Not_Match_A_Custom_Drug_Rule()
    {
        // The mirror case: a custom-drug rule must be invisible to the catalog drug in the same slot.
        var table = Table(CustomRule(S.Svt, "drug-x", 0, (S.Sinus, 1.0)));
        var r = TreatmentEngine.Apply(S.Svt, Drug(TreatmentDrug.Adrenaline, 1), new TreatmentContext(), table, Seq(0.0));
        Assert.Equal(S.Svt, r.NewState); // built-in adrenaline on SVT: no change
    }

    [Fact]
    public void Custom_Drugs_Are_Told_Apart_By_Their_Id()
    {
        var table = Table(
            CustomRule(S.VentricularFibrillation, "drug-x", 0, (S.Sinus, 1.0)),
            CustomRule(S.VentricularFibrillation, "drug-y", 0, (S.Asystole, 1.0)));
        Assert.Equal(S.Sinus, TreatmentEngine.Apply(S.VentricularFibrillation, CustomDrug("drug-x", 5),
            new TreatmentContext(), table, Seq(0.0)).NewState);
        Assert.Equal(S.Asystole, TreatmentEngine.Apply(S.VentricularFibrillation, CustomDrug("drug-y", 5),
            new TreatmentContext(), table, Seq(0.0)).NewState);
    }

    [Fact]
    public void Unbound_Custom_Drug_Changes_Nothing_And_Skips_The_BuiltIn_Drug_Rules()
    {
        // No authored rule for this id: nothing happens. In particular the adrenaline slot must not prime the
        // next shock, must not record a dose against adrenaline, and must not raise the "needs CPR" warning.
        var ctx = new TreatmentContext();
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, CustomDrug("unbound", 5), ctx,
            new AuthoredTreatmentTable(null), Seq(0.0));

        Assert.Equal(S.VentricularFibrillation, r.NewState);
        Assert.False(r.Blocked);
        Assert.Equal(TreatmentReason.None, r.Warning);
        Assert.False(ctx.AdrenalinePrimed);
        Assert.Equal(0, ctx.DoseGiven(TreatmentDrug.Adrenaline));
    }

    [Fact]
    public void Custom_Drug_Is_Not_Capped_By_The_Catalog_Max_Dose()
    {
        // Amiodarone's 2.2 g cap belongs to amiodarone, not to a custom drug parked in its slot.
        var ctx = new TreatmentContext();
        var action = CustomDrug("drug-x", 5000, TreatmentDrug.Amiodarone);
        Assert.Equal(TreatmentVerdict.Ok, TreatmentEngine.Validate(S.Sinus, action, ctx).Verdict);
        Assert.Equal(TreatmentVerdict.Warn,
            TreatmentEngine.Validate(S.Sinus, Drug(TreatmentDrug.Amiodarone, 5000), ctx).Verdict);
    }

    [Fact]
    public void Custom_Drug_Rule_Honours_A_Specific_Source_Pathology()
    {
        var rule = new AuthoredTransition(
            From: S.Sinus,
            Trigger: AuthoredTrigger.Drug,
            Drug: null,
            Outcomes: new[] { new AuthoredOutcome(S.Paced, 1.0, TargetPathologyId: "26") },
            EffectSeconds: 0,
            FromPathologyId: "100",
            CustomDrugId: "drug-x");
        var table = Table(rule);

        var hit = TreatmentEngine.Apply(S.Sinus, CustomDrug("drug-x", 5), new TreatmentContext(), table, Seq(0.0),
            currentPathologyId: "100");
        Assert.Equal(S.Paced, hit.NewState);
        Assert.Equal("26", hit.TargetPathologyId);

        var miss = TreatmentEngine.Apply(S.Sinus, CustomDrug("drug-x", 5), new TreatmentContext(), table, Seq(0.0),
            currentPathologyId: "101");
        Assert.Equal(S.Sinus, miss.NewState);
        Assert.Null(miss.TargetPathologyId);
    }

    [Fact]
    public void Authored_Rule_Matches_By_FromAcronym_And_Returns_TargetAcronym()
    {
        var rule = new AuthoredTransition(
            From: S.Svt,
            Trigger: AuthoredTrigger.Drug,
            Drug: TreatmentDrug.Adenosine,
            Outcomes: new[] { new AuthoredOutcome(S.Sinus, 1.0, TargetAcronym: "SR") },
            EffectSeconds: 10,
            FromAcronym: "WPW");

        var table = Table(rule);

        // Matching acronym
        var hit = TreatmentEngine.Apply(
            S.Svt,
            Drug(TreatmentDrug.Adenosine, 6),
            new TreatmentContext(),
            table,
            Seq(0.0),
            currentAcronyms: new[] { "WPW", "SVT" });

        Assert.Equal(S.Sinus, hit.NewState);
        Assert.Equal("SR", hit.TargetAcronym);
        Assert.Equal(10, hit.EffectSeconds);

        // Non-matching acronym falls back to built-in or no-op
        var miss = TreatmentEngine.Apply(
            S.Svt,
            Drug(TreatmentDrug.Adenosine, 6),
            new TreatmentContext(),
            table,
            Seq(0.0),
            currentAcronyms: new[] { "AFIB" });

        // built-in Adenosine on SVT doesn't produce TargetAcronym "SR" (it has TargetAcronym null)
        Assert.Null(miss.TargetAcronym);
    }

    [Fact]
    public void Authored_Rule_Priority_Pathology_Beats_Acronym_Beats_State()
    {
        var stateRule = new AuthoredTransition(
            From: S.VentricularFibrillation,
            Trigger: AuthoredTrigger.Defibrillation,
            Drug: null,
            Outcomes: new[] { new AuthoredOutcome(S.Sinus, 1.0, TargetAcronym: "FROM_STATE") },
            EffectSeconds: 0);

        var acronymRule = new AuthoredTransition(
            From: S.VentricularFibrillation,
            Trigger: AuthoredTrigger.Defibrillation,
            Drug: null,
            Outcomes: new[] { new AuthoredOutcome(S.Sinus, 1.0, TargetAcronym: "FROM_ACRONYM") },
            EffectSeconds: 0,
            FromAcronym: "VFIB");

        var pathologyRule = new AuthoredTransition(
            From: S.VentricularFibrillation,
            Trigger: AuthoredTrigger.Defibrillation,
            Drug: null,
            Outcomes: new[] { new AuthoredOutcome(S.Sinus, 1.0, TargetAcronym: "FROM_PATHOLOGY") },
            EffectSeconds: 0,
            FromPathologyId: "100",
            FromAcronym: "VFIB");

        var table = Table(stateRule, acronymRule, pathologyRule);

        // 1. Only state matches
        var r1 = TreatmentEngine.Apply(
            S.VentricularFibrillation,
            Shock(200, false),
            new TreatmentContext(),
            table,
            Seq(0.0),
            currentAcronyms: new[] { "OTHER" });
        Assert.Equal("FROM_STATE", r1.TargetAcronym);

        // 2. Acronym matches
        var r2 = TreatmentEngine.Apply(
            S.VentricularFibrillation,
            Shock(200, false),
            new TreatmentContext(),
            table,
            Seq(0.0),
            currentAcronyms: new[] { "VFIB" });
        Assert.Equal("FROM_ACRONYM", r2.TargetAcronym);

        // 3. Pathology ID matches (wins over acronym)
        var r3 = TreatmentEngine.Apply(
            S.VentricularFibrillation,
            Shock(200, false),
            new TreatmentContext(),
            table,
            Seq(0.0),
            currentPathologyId: "100",
            currentAcronyms: new[] { "VFIB" });
        Assert.Equal("FROM_PATHOLOGY", r3.TargetAcronym);
    }
}

