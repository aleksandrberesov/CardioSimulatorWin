using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// A single authored rhythm-transition rule — the structured, engine-facing projection of one (or several
/// merged) rows of the editable «Протоколы лечения» table. It says: when the patient is in <see cref="From"/>
/// and the clinician applies the action identified by <see cref="Trigger"/> (a specific <see cref="Drug"/>
/// when the trigger is <see cref="AuthoredTrigger.Drug"/>), the rhythm becomes one of <see cref="Outcomes"/>
/// (weighted probability draw) after <see cref="EffectSeconds"/> of real clinical time.
/// </summary>
public sealed record AuthoredTransition(
    ClinicalRhythmState From,
    AuthoredTrigger Trigger,
    TreatmentDrug? Drug,
    IReadOnlyList<AuthoredOutcome> Outcomes,
    double EffectSeconds,
    string? FromPathologyId = null);

/// <summary>One weighted result of an <see cref="AuthoredTransition"/>. Weights need not sum to 1 — the table
/// builder normalises and adds a "no change" residual (staying in <c>From</c>) for any shortfall.</summary>
public sealed record AuthoredOutcome(
    ClinicalRhythmState State,
    double Weight,
    string? TargetPathologyId = null);

/// <summary>The kind of action that triggers an <see cref="AuthoredTransition"/> — the engine-relevant
/// subset of <see cref="TreatmentAction"/> (toggles like O₂/CPR and the instructor SetRhythm never carry a
/// rhythm transition, so they have no trigger).</summary>
public enum AuthoredTrigger
{
    /// <summary>Unsynchronized defibrillation.</summary>
    Defibrillation,
    /// <summary>Synchronized cardioversion.</summary>
    SyncCardioversion,
    /// <summary>A specific drug (see <see cref="AuthoredTransition.Drug"/>).</summary>
    Drug,
    /// <summary>Transcutaneous/transvenous pacing.</summary>
    Pacing,
    /// <summary>A vagal maneuver.</summary>
    Vagal,
}

/// <summary>
/// The authored transition table the treatment engine consults before its built-in rules. Built by the App
/// layer from the instructor-editable protocol set and handed to <see cref="TreatmentEngine.Apply(ClinicalRhythmState, TreatmentAction, TreatmentContext, AuthoredTreatmentTable, System.Func{double})"/>.
/// Pure/data-only. When empty (no authored bindings), the engine keeps its built-in behaviour.
/// </summary>
public sealed class AuthoredTreatmentTable
{
    private readonly List<AuthoredTransition> _all;

    public AuthoredTreatmentTable(IEnumerable<AuthoredTransition>? transitions)
    {
        _all = transitions?.Where(t => t is not null && t.Outcomes is { Count: > 0 }).ToList()
               ?? new List<AuthoredTransition>();
    }

    /// <summary>No authored rules → the engine falls back entirely to its built-in logic.</summary>
    public bool IsEmpty => _all.Count == 0;

    public IReadOnlyList<AuthoredTransition> All => _all;

    /// <summary>Maps an action to the trigger it fires (and, for a drug, which drug), or null when the action
    /// carries no rhythm transition (O₂/CPR toggles, instructor SetRhythm).</summary>
    public static AuthoredTrigger? TriggerFor(TreatmentAction action, out TreatmentDrug? drug)
    {
        drug = null;
        switch (action)
        {
            case TreatmentAction.Defib d:
                return d.Synchronized ? AuthoredTrigger.SyncCardioversion : AuthoredTrigger.Defibrillation;
            case TreatmentAction.Drug dr:
                drug = dr.Which;
                return AuthoredTrigger.Drug;
            case TreatmentAction.Pacing:
                return AuthoredTrigger.Pacing;
            case TreatmentAction.Vagal:
                return AuthoredTrigger.Vagal;
            default:
                return null;
        }
    }

    /// <summary>The authored transition for <paramref name="state"/> + <paramref name="action"/>, or null when
    /// nothing is authored for that pair (the engine then uses its built-in rule).</summary>
    public AuthoredTransition? Match(ClinicalRhythmState state, TreatmentAction action, string? currentPathologyId = null)
    {
        var trigger = TriggerFor(action, out var drug);
        if (trigger is null) return null;

        // Specific pathology match takes precedence over generic state match
        if (!string.IsNullOrEmpty(currentPathologyId))
        {
            foreach (var t in _all)
            {
                if (string.Equals(t.FromPathologyId, currentPathologyId, System.StringComparison.OrdinalIgnoreCase)
                    && t.Trigger == trigger.Value
                    && (trigger.Value != AuthoredTrigger.Drug || t.Drug == drug))
                    return t;
            }
        }

        foreach (var t in _all)
            if (string.IsNullOrEmpty(t.FromPathologyId) && t.From == state && t.Trigger == trigger.Value &&
                (trigger.Value != AuthoredTrigger.Drug || t.Drug == drug))
                return t;
        return null;
    }
}
