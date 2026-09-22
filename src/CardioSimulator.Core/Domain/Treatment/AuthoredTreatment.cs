using System.Collections.Generic;
using System.Linq;

namespace CardioSimulator.Core.Domain.Treatment;

/// <summary>
/// A single authored rhythm-transition rule — the structured, engine-facing projection of one (or several
/// merged) rows of the editable «Протоколы лечения» table. It says: when the patient is in <see cref="From"/>
/// and the clinician applies the action identified by <see cref="Trigger"/> (a specific <see cref="Drug"/>
/// when the trigger is <see cref="AuthoredTrigger.Drug"/>), the rhythm becomes one of <see cref="Outcomes"/>
/// (weighted probability draw) after <see cref="EffectSeconds"/> of real clinical time.
/// <para>A drug trigger binds EITHER to a catalog <see cref="Drug"/> or, when <see cref="CustomDrugId"/> is
/// set, to an instructor-authored custom drug — the id is the stable identity, so renaming the drug keeps the
/// rule. The two are mutually exclusive: a rule with a <see cref="CustomDrugId"/> never matches a catalog
/// drug, and vice versa.</para>
/// </summary>
public sealed record AuthoredTransition(
    ClinicalRhythmState From,
    AuthoredTrigger Trigger,
    TreatmentDrug? Drug,
    IReadOnlyList<AuthoredOutcome> Outcomes,
    double EffectSeconds,
    string? FromPathologyId = null,
    string? CustomDrugId = null,
    string? FromAcronym = null);

/// <summary>One weighted result of an <see cref="AuthoredTransition"/>. Weights need not sum to 1 — the table
/// builder normalises and adds a "no change" residual (staying in <c>From</c>) for any shortfall.</summary>
public sealed record AuthoredOutcome(
    ClinicalRhythmState State,
    double Weight,
    string? TargetPathologyId = null,
    string? TargetAcronym = null);

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
    public static AuthoredTrigger? TriggerFor(TreatmentAction action, out TreatmentDrug? drug) =>
        TriggerFor(action, out drug, out _);

    /// <summary>As <see cref="TriggerFor(TreatmentAction, out TreatmentDrug?)"/>, but also reports the
    /// authored custom-drug id when the action gave a custom drug (then <paramref name="drug"/> is null — the
    /// enum slot a custom drug fills is meaningless to the table).</summary>
    public static AuthoredTrigger? TriggerFor(TreatmentAction action, out TreatmentDrug? drug, out string? customDrugId)
    {
        drug = null;
        customDrugId = null;
        switch (action)
        {
            case TreatmentAction.Defib d:
                return d.Synchronized ? AuthoredTrigger.SyncCardioversion : AuthoredTrigger.Defibrillation;
            case TreatmentAction.Drug dr:
                if (dr.IsCustom) customDrugId = dr.CustomDrugId;
                else drug = dr.Which;
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
    public AuthoredTransition? Match(
        ClinicalRhythmState state,
        TreatmentAction action,
        string? currentPathologyId = null,
        IReadOnlyList<string>? currentAcronyms = null)
    {
        var trigger = TriggerFor(action, out var drug, out var customDrugId);
        if (trigger is null) return null;

        // 1. Specific pathology match takes highest precedence
        if (!string.IsNullOrEmpty(currentPathologyId))
        {
            foreach (var t in _all)
            {
                if (string.Equals(t.FromPathologyId, currentPathologyId, System.StringComparison.OrdinalIgnoreCase)
                    && t.Trigger == trigger.Value
                    && DrugMatches(t, trigger.Value, drug, customDrugId))
                    return t;
            }
        }

        // 2. Acronym match takes second precedence
        if (currentAcronyms is { Count: > 0 })
        {
            foreach (var t in _all)
            {
                if (!string.IsNullOrEmpty(t.FromAcronym)
                    && currentAcronyms.Any(a => string.Equals(a, t.FromAcronym, System.StringComparison.OrdinalIgnoreCase))
                    && t.Trigger == trigger.Value
                    && DrugMatches(t, trigger.Value, drug, customDrugId))
                    return t;
            }
        }

        // 3. Generic state match
        foreach (var t in _all)
            if (string.IsNullOrEmpty(t.FromPathologyId) && string.IsNullOrEmpty(t.FromAcronym) && t.From == state && t.Trigger == trigger.Value &&
                DrugMatches(t, trigger.Value, drug, customDrugId))
                return t;
        return null;
    }

    /// <summary>Whether a drug-triggered rule binds the drug that was actually given. A custom drug matches
    /// only a rule authored for that same custom-drug id; a catalog drug matches only a rule with no custom
    /// id and the same enum value. Non-drug triggers are always a match (there is nothing to compare).</summary>
    private static bool DrugMatches(AuthoredTransition t, AuthoredTrigger trigger, TreatmentDrug? drug, string? customDrugId)
    {
        if (trigger != AuthoredTrigger.Drug) return true;
        if (!string.IsNullOrEmpty(customDrugId))
            return string.Equals(t.CustomDrugId, customDrugId, System.StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrEmpty(t.CustomDrugId) && t.Drug == drug;
    }
}
