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
/// <para>Rows that share the same (rhythm, action, drug) are MERGED into one rule — the display table lists
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

        var groups = new Dictionary<(ClinicalRhythmState, string?, AuthoredTrigger, TreatmentDrug?), Group>();

        foreach (var row in set.Transitions)
        {
            if (row.FromState is not { } from) continue;
            var trigger = MapTrigger(row.Trigger);
            if (trigger is not { } trig) continue;
            var drug = trig == AuthoredTrigger.Drug ? row.TriggerDrug : null;
            if (trig == AuthoredTrigger.Drug && drug is null) continue;

            var key = (from, row.FromPathologyId, trig, drug);
            if (!groups.TryGetValue(key, out var g))
            {
                g = new Group { From = from, FromPathologyId = row.FromPathologyId, Trigger = trig, Drug = drug, EffectSeconds = row.EffectSeconds };
                groups[key] = g;
            }
            // First bound row in the group sets the effect timing (rows for the same action share it).
            if (g.EffectSeconds == 0 && row.EffectSeconds != 0) g.EffectSeconds = row.EffectSeconds;

            foreach (var res in row.Results)
                if (res.State is { } rs && res.Weight > 0)
                    g.Outcomes.Add(new AuthoredOutcome(rs, res.Weight, res.TargetPathologyId));
        }

        var transitions = new List<AuthoredTransition>();
        foreach (var g in groups.Values)
        {
            if (g.Outcomes.Count == 0) continue;
            var sum = g.Outcomes.Sum(o => o.Weight);
            var outcomes = new List<AuthoredOutcome>(g.Outcomes);
            // Any shortfall below 1.0 is a "no change" residual — the patient stays in the current rhythm.
            if (sum < 1.0)
                outcomes.Add(new AuthoredOutcome(g.From, 1.0 - sum, g.FromPathologyId));
            transitions.Add(new AuthoredTransition(g.From, g.Trigger, g.Drug, outcomes, g.EffectSeconds, g.FromPathologyId));
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
        public AuthoredTrigger Trigger;
        public TreatmentDrug? Drug;
        public double EffectSeconds;
        public readonly List<AuthoredOutcome> Outcomes = new();
    }
}
