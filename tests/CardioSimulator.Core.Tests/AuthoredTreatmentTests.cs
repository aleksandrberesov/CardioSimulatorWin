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
}
