using System.Collections.Generic;
using System.Linq;
using CardioSimulator.Core.Domain.Treatment;

namespace CardioSimulator.App.Data;

/// <summary>
/// Projects the instructor-editable <see cref="TreatmentProtocolSet"/> onto the engine-facing
/// <see cref="AuthoredTreatmentTable"/> that drives the Лечение (treatment) panel. Only transitions with an
/// engine binding participate (a <see cref="TransitionProtocol.FromState"/> and a non-<see
/// cref="TransitionTrigger.None"/> <see cref="TransitionProtocol.Trigger"/>); display-only rows (sequences,
/// contraindicated actions) are ignored here but still shown in the reference table/panel.
///
/// <para>Rows that share the same (rhythm, action, drug — standard or custom) are MERGED into one rule — the display table lists
/// one outcome per row ("→ Sinus, success 75%" / "→ Asystole, failure 25%") whereas the engine needs a single
/// weighted outcome set for that state+action. Each result's <see cref="ResultItem.Weight"/> is a success
/// fraction; if a rule's weights sum to less than 1 the remainder becomes a "no change" outcome (the patient
/// stays in the current rhythm), so "success 90%" naturally yields a 10% no-effect chance.</para>
/// </summary>
public static class TreatmentProtocolBridge
{
    public static AuthoredTreatmentTable BuildTable(TreatmentProtocolSet? set)
    {
        if (set is null) return new AuthoredTreatmentTable(null);

        var groups = new Dictionary<(ClinicalRhythmState, string?, string?, AuthoredTrigger, TreatmentDrug?, string?), Group>();

        foreach (var row in set.Transitions)
        {
            var from = row.FromState;
            if (from is null && !string.IsNullOrEmpty(row.FromAcronym))
                from = TreatmentRhythmMap.ClassifyByAcronyms(new[] { row.FromAcronym });
            if (from is not { } fromState) continue;

            var trigger = MapTrigger(row.Trigger);
            if (trigger is not { } trig) continue;
            // A drug row binds EITHER an authored custom drug (by its stable id) or a catalog drug — never both.
            var customDrugId = trig == AuthoredTrigger.Drug && !string.IsNullOrWhiteSpace(row.TriggerCustomDrugId)
                ? row.TriggerCustomDrugId
                : null;
            var drug = trig == AuthoredTrigger.Drug && customDrugId is null ? row.TriggerDrug : null;
            if (trig == AuthoredTrigger.Drug && drug is null && customDrugId is null) continue;

            var key = (fromState, row.FromPathologyId, row.FromAcronym, trig, drug, customDrugId);
            if (!groups.TryGetValue(key, out var g))
            {
                g = new Group
                {
                    From = fromState,
                    FromPathologyId = row.FromPathologyId,
                    FromAcronym = row.FromAcronym,
                    Trigger = trig,
                    Drug = drug,
                    CustomDrugId = customDrugId,
                    EffectSeconds = row.EffectSeconds
                };
                groups[key] = g;
            }
            // First bound row in the group sets the effect timing (rows for the same action share it).
            if (g.EffectSeconds == 0 && row.EffectSeconds != 0) g.EffectSeconds = row.EffectSeconds;

            foreach (var res in row.Results)
            {
                var rState = res.State;
                if (rState is null && !string.IsNullOrEmpty(res.TargetAcronym))
                    rState = TreatmentRhythmMap.ClassifyByAcronyms(new[] { res.TargetAcronym });
                if (rState is { } rs && res.Weight > 0)
                    g.Outcomes.Add(new AuthoredOutcome(rs, res.Weight, res.TargetPathologyId, res.TargetAcronym));
            }
        }

        var transitions = new List<AuthoredTransition>();
        foreach (var g in groups.Values)
        {
            if (g.Outcomes.Count == 0) continue;
            var sum = g.Outcomes.Sum(o => o.Weight);
            var outcomes = new List<AuthoredOutcome>(g.Outcomes);
            // Any shortfall below 1.0 is a "no change" residual — the patient stays in the current rhythm.
            if (sum < 1.0)
                outcomes.Add(new AuthoredOutcome(g.From, 1.0 - sum, g.FromPathologyId, g.FromAcronym));
            transitions.Add(new AuthoredTransition(g.From, g.Trigger, g.Drug, outcomes, g.EffectSeconds, g.FromPathologyId, g.CustomDrugId, g.FromAcronym));
        }

        return new AuthoredTreatmentTable(transitions);
    }

    private static AuthoredTrigger? MapTrigger(TransitionTrigger t) => t switch
    {
        TransitionTrigger.Defibrillation => AuthoredTrigger.Defibrillation,
        TransitionTrigger.SyncCardioversion => AuthoredTrigger.SyncCardioversion,
        TransitionTrigger.Drug => AuthoredTrigger.Drug,
        TransitionTrigger.Pacing => AuthoredTrigger.Pacing,
        TransitionTrigger.Vagal => AuthoredTrigger.Vagal,
        _ => null,
    };

    private sealed class Group
    {
        public ClinicalRhythmState From;
        public string? FromPathologyId;
        public string? FromAcronym;
        public AuthoredTrigger Trigger;
        public TreatmentDrug? Drug;
        public string? CustomDrugId;
        public double EffectSeconds;
        public readonly List<AuthoredOutcome> Outcomes = new();
    }
}
