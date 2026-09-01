using System.Collections.Generic;
using CardioSimulator.Core.Domain.Treatment;
using Xunit;

namespace CardioSimulator.Core.Tests;

using S = ClinicalRhythmState;

public class TreatmentEngineTests
{
    // A deterministic RNG that dequeues the given values (defaults to 0 when exhausted), so a probabilistic
    // outcome is forced to a specific branch.
    private static System.Func<double> Seq(params double[] values)
    {
        var q = new Queue<double>(values);
        return () => q.Count > 0 ? q.Dequeue() : 0.0;
    }

    private static TreatmentAction Drug(TreatmentDrug d, double mg) => new TreatmentAction.Drug(d, mg);
    private static TreatmentAction Shock(int j, bool sync) => new TreatmentAction.Defib(j, sync);

    // ── Validation ───────────────────────────────────────────────────────────

    [Fact]
    public void Defib_OnAsystole_IsBlocked()
    {
        var ctx = new TreatmentContext();
        var r = TreatmentEngine.Apply(S.Asystole, Shock(200, false), ctx);
        Assert.True(r.Blocked);
        Assert.Equal(S.Asystole, r.NewState);
        Assert.NotEqual(TreatmentReason.None, r.Warning);
    }

    [Fact]
    public void AsyncDefib_OnPulsedVt_Warns_And_CanDegenerateToVf()
    {
        var v = TreatmentEngine.Validate(S.VentricularTachycardia, Shock(200, false), new TreatmentContext());
        Assert.Equal(TreatmentVerdict.Warn, v.Verdict);

        var r = TreatmentEngine.Apply(S.VentricularTachycardia, Shock(200, false), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.VentricularFibrillation, r.NewState); // R-on-T
        Assert.NotEqual(TreatmentReason.None, r.Warning);
    }

    [Fact]
    public void Adrenaline_InVf_WithoutCpr_Warns()
    {
        var v = TreatmentEngine.Validate(S.VentricularFibrillation, Drug(TreatmentDrug.Adrenaline, 1), new TreatmentContext());
        Assert.Equal(TreatmentVerdict.Warn, v.Verdict);
    }

    [Fact]
    public void Atropine_OverMax_Warns()
    {
        var ctx = new TreatmentContext();
        ctx.RecordDose(TreatmentDrug.Atropine, 3.0); // already at the 3 mg cap
        var v = TreatmentEngine.Validate(S.CompleteAvBlock, Drug(TreatmentDrug.Atropine, 0.5), ctx);
        Assert.Equal(TreatmentVerdict.Warn, v.Verdict);
    }

    [Theory]
    [InlineData(20, TreatmentVerdict.Warn)]   // no capture
    [InlineData(70, TreatmentVerdict.Ok)]
    [InlineData(180, TreatmentVerdict.Warn)]  // fibrillation risk
    public void Pacing_CurrentThresholds(int mA, TreatmentVerdict expected)
    {
        var v = TreatmentEngine.Validate(S.CompleteAvBlock, new TreatmentAction.Pacing(70, mA), new TreatmentContext());
        Assert.Equal(expected, v.Verdict);
    }

    // ── Transitions ──────────────────────────────────────────────────────────

    [Fact]
    public void Defib_OnVf_Success_ConvertsToSinus_AndResetsCounters()
    {
        var ctx = new TreatmentContext { FailedDefibCount = 2, CprActive = true };
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), ctx, Seq(0.0)); // draw < success prob
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(0, r.EffectSeconds);
        Assert.Equal(0, ctx.FailedDefibCount);
    }

    [Fact]
    public void Defib_OnVf_Failure_PersistsAndIncrementsFailedCount()
    {
        var ctx = new TreatmentContext { CprActive = true };
        // First draw high → miss the success branch; second draw high → persist VF (not asystole).
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), ctx, Seq(0.99, 0.99));
        Assert.Equal(S.VentricularFibrillation, r.NewState);
        Assert.Equal(1, ctx.FailedDefibCount);
    }

    [Fact]
    public void Adrenaline_InVf_Primes_NextShock()
    {
        var ctx = new TreatmentContext { CprActive = true };
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Drug(TreatmentDrug.Adrenaline, 1), ctx);
        Assert.Equal(S.VentricularFibrillation, r.NewState); // no direct conversion
        Assert.True(ctx.AdrenalinePrimed);
        Assert.Equal(1.0, ctx.DoseGiven(TreatmentDrug.Adrenaline));
    }

    [Fact]
    public void SyncCardioversion_OnPulsedVt_ConvertsToSinus()
    {
        var r = TreatmentEngine.Apply(S.VentricularTachycardia, Shock(100, true), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
    }

    [Fact]
    public void Adenosine_OnSvt_ConvertsToSinus_Fast()
    {
        var r = TreatmentEngine.Apply(S.Svt, Drug(TreatmentDrug.Adenosine, 6), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(30, r.EffectSeconds);
    }

    [Fact]
    public void Magnesium_OnTorsades_ConvertsToSinus()
    {
        var r = TreatmentEngine.Apply(S.Torsades, Drug(TreatmentDrug.MagnesiumSulfate, 2000), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
    }

    [Fact]
    public void Atropine_OnCompleteBlock_CanConvertToSinus()
    {
        var r = TreatmentEngine.Apply(S.CompleteAvBlock, Drug(TreatmentDrug.Atropine, 0.5), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
    }

    [Fact]
    public void Pacing_OnCompleteBlock_CapturesToPaced()
    {
        var r = TreatmentEngine.Apply(S.CompleteAvBlock, new TreatmentAction.Pacing(70, 50), new TreatmentContext());
        Assert.Equal(S.Paced, r.NewState);
    }

    [Fact]
    public void Metoprolol_OnAFib_RateControls_NotConverts()
    {
        var r = TreatmentEngine.Apply(S.AtrialFibrillation, Drug(TreatmentDrug.Metoprolol, 5), new TreatmentContext());
        Assert.Equal(S.AtrialFibrillationRateControlled, r.NewState);
    }

    [Fact]
    public void Amiodarone_OnAFib_SlowConversionToSinus()
    {
        var r = TreatmentEngine.Apply(S.AtrialFibrillation, Drug(TreatmentDrug.Amiodarone, 300), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(2700, r.EffectSeconds); // 30–60 min
    }

    [Fact]
    public void Vagal_OnSvt_SometimesConverts()
    {
        var success = TreatmentEngine.Apply(S.Svt, new TreatmentAction.Vagal(VagalManeuver.Valsalva), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, success.NewState);
        var fail = TreatmentEngine.Apply(S.Svt, new TreatmentAction.Vagal(VagalManeuver.Valsalva), new TreatmentContext(), Seq(0.99));
        Assert.Equal(S.Svt, fail.NewState);
    }

    [Fact]
    public void Toggles_UpdateContext()
    {
        var ctx = new TreatmentContext();
        TreatmentEngine.Apply(S.Asystole, new TreatmentAction.Cpr(true), ctx);
        TreatmentEngine.Apply(S.Asystole, new TreatmentAction.Oxygen(true), ctx);
        Assert.True(ctx.CprActive);
        Assert.True(ctx.OxygenOn);
    }

    [Fact]
    public void SetRhythm_OverridesState()
    {
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, new TreatmentAction.SetRhythm(S.Asystole), new TreatmentContext());
        Assert.Equal(S.Asystole, r.NewState);
    }

    [Fact]
    public void Drug_WithNoRuleForState_LeavesRhythmUnchanged()
    {
        var r = TreatmentEngine.Apply(S.Sinus, Drug(TreatmentDrug.Adenosine, 6), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
    }

    // ── Bookkeeping, adjuncts & dose guards (review fixes 29-08) ───────────────

    [Fact]
    public void SetRhythm_ClearsArrestBookkeeping()
    {
        // A manual rhythm override must not leak the previous rhythm's shock priming / failed-shock count.
        var ctx = new TreatmentContext { FailedDefibCount = 3, AdrenalinePrimed = true };
        TreatmentEngine.Apply(S.VentricularFibrillation, new TreatmentAction.SetRhythm(S.VentricularFibrillation), ctx);
        Assert.Equal(0, ctx.FailedDefibCount);
        Assert.False(ctx.AdrenalinePrimed);
    }

    [Fact]
    public void Amiodarone_InVf_PrimesNextShock_NoDirectConversion()
    {
        var ctx = new TreatmentContext { CprActive = true };
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Drug(TreatmentDrug.Amiodarone, 300), ctx, Seq(0.0));
        Assert.Equal(S.VentricularFibrillation, r.NewState); // adjunct — no conversion without a shock
        Assert.True(ctx.AdrenalinePrimed);                   // primes the next defibrillation
    }

    [Fact]
    public void DefibSuccess_FromVf_ClearsArrestBookkeeping()
    {
        var ctx = new TreatmentContext { FailedDefibCount = 2, AdrenalinePrimed = true, CprActive = true };
        var r = TreatmentEngine.Apply(S.VentricularFibrillation, Shock(200, false), ctx, Seq(0.0));
        Assert.Equal(S.Sinus, r.NewState);
        Assert.Equal(0, ctx.FailedDefibCount);
        Assert.False(ctx.AdrenalinePrimed);
    }

    [Theory]
    [InlineData(S.Sinus)]
    [InlineData(S.SinusTachycardia)]
    [InlineData(S.AtrialFibrillation)]
    [InlineData(S.Svt)]
    [InlineData(S.Paced)]
    public void UnsyncShock_OnOrganizedRhythm_Warns(S state)
    {
        var v = TreatmentEngine.Validate(state, Shock(200, false), new TreatmentContext());
        Assert.Equal(TreatmentVerdict.Warn, v.Verdict);
        Assert.Equal(TreatmentReason.UnsyncShockOrganizedRhythm, v.Reason);
    }

    [Fact]
    public void RateControlledAFib_IsCardiovertible_AndAmiodaroneConverts()
    {
        var cv = TreatmentEngine.Apply(S.AtrialFibrillationRateControlled, Shock(150, true), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, cv.NewState);
        var amio = TreatmentEngine.Apply(S.AtrialFibrillationRateControlled, Drug(TreatmentDrug.Amiodarone, 300), new TreatmentContext(), Seq(0.0));
        Assert.Equal(S.Sinus, amio.NewState);
    }

    [Fact]
    public void Atropine_OnCompleteBlock_Warns_AndIsUsuallyIneffective()
    {
        var v = TreatmentEngine.Validate(S.CompleteAvBlock, Drug(TreatmentDrug.Atropine, 0.5), new TreatmentContext());
        Assert.Equal(TreatmentVerdict.Warn, v.Verdict);
        Assert.Equal(TreatmentReason.AtropineIneffectiveHighBlock, v.Reason);
        var r = TreatmentEngine.Apply(S.CompleteAvBlock, Drug(TreatmentDrug.Atropine, 0.5), new TreatmentContext(), Seq(0.5)); // > 0.1
        Assert.Equal(S.CompleteAvBlock, r.NewState); // low efficacy — stays blocked
    }

    [Fact]
    public void ZeroOrNegativeDose_IsIgnored()
    {
        var ctx = new TreatmentContext();
        var zero = TreatmentEngine.Apply(S.Svt, Drug(TreatmentDrug.Adenosine, 0), ctx, Seq(0.0));
        Assert.Equal(S.Svt, zero.NewState);                          // a 0 mg dose does nothing
        Assert.Equal(0.0, ctx.DoseGiven(TreatmentDrug.Adenosine));   // and is not recorded
        TreatmentEngine.Apply(S.CompleteAvBlock, Drug(TreatmentDrug.Atropine, -3), ctx, Seq(0.0));
        Assert.Equal(0.0, ctx.DoseGiven(TreatmentDrug.Atropine));    // a negative dose cannot corrupt the total
    }

    // ── Arrest classification (drives the panel's CPR prompt) ──────────────────

    [Theory]
    [InlineData(S.VentricularFibrillation)]
    [InlineData(S.PulselessVt)]
    [InlineData(S.Asystole)]
    public void ArrestRhythms_AreClassifiedAsArrest(S state) =>
        Assert.True(TreatmentRhythmMap.IsArrestRhythm(state));

    [Theory]
    [InlineData(S.Sinus)]
    [InlineData(S.VentricularTachycardia)] // pulsed VT — unstable but not pulseless arrest
    [InlineData(S.Torsades)]
    [InlineData(S.Svt)]
    [InlineData(S.CompleteAvBlock)]
    [InlineData(S.Paced)]
    public void PerfusingOrPulsedRhythms_AreNotArrest(S state) =>
        Assert.False(TreatmentRhythmMap.IsArrestRhythm(state));
}
