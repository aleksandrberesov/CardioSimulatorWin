using System;
using System.Collections.Generic;
using CardioSimulator.Core.Domain.Treatment;

namespace CardioSimulator.App.Data;

/// <summary>Which treatment action fires a transition (the engine-facing binding of a display row). <see
/// cref="None"/> = display-only (the row is shown in the table/panel but does not drive the simulator).</summary>
public enum TransitionTrigger { None, Defibrillation, SyncCardioversion, Drug, Pacing, Vagal }

// Serializable model behind the editable «Протоколы лечения» (Treatment Protocols) screen. Every
// author-visible text is a bilingual EN/RU pair (see AppViewModel role/localization); the display picks
// the active language and falls back to English. Enums are written as strings (see the store's
// JsonStringEnumConverter) so the file survives reordering and is human-diffable. Each row carries a
// stable Id so edit/delete/reorder can target it. This lives in the App layer — it is UI-facing
// reference content, and the kinds/categories are display classes, not a Core domain concept.

/// <summary>Bilingual text. <see cref="Ru"/> falls back to <see cref="En"/> when empty.</summary>
public sealed class LocText
{
    public string En { get; set; } = string.Empty;
    public string Ru { get; set; } = string.Empty;

    public LocText() { }
    public LocText(string en, string ru) { En = en; Ru = ru; }

    /// <summary>The text for the active language, falling back to English when the Russian is blank.</summary>
    public string Pick(bool ru) => ru && !string.IsNullOrWhiteSpace(Ru) ? Ru : En;

    public LocText Clone() => new(En, Ru);
}

/// <summary>Rhythm severity class. <see cref="Danger"/> is emphasised in the table (critical-rhythm accent bar,
/// semibold result); the other kinds display plainly.</summary>
public enum RhythmKind { Normal, Danger, Warning }

/// <summary>Treatment category of an action (shown as the action's tooltip).</summary>
public enum ActionCategory { Med, Elec, Mech, Vagal }

/// <summary>ACLS flow-node class (<see cref="Critical"/> gets the alert accent; the others display plainly).</summary>
public enum AclsNodeKind { Default, Critical, Normal }

public sealed class ActionItem
{
    public ActionCategory Category { get; set; } = ActionCategory.Med;
    public LocText Text { get; set; } = new();

    public ActionItem Clone() => new() { Category = Category, Text = Text.Clone() };
}

public sealed class ResultItem
{
    public RhythmKind Kind { get; set; } = RhythmKind.Normal;
    public LocText Text { get; set; } = new();

    /// <summary>Engine binding: the resulting clinical rhythm this outcome maps to (null = display-only, not
    /// used by the simulator).</summary>
    public ClinicalRhythmState? State { get; set; }

    /// <summary>Engine binding: relative likelihood of this outcome. Weights across all outcomes for one
    /// (rhythm, action) need not sum to 1 — the bridge normalises and gives any shortfall to "no change".</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Engine binding: specific concrete pathology ID to switch to (null = map from State via taxonomy).</summary>
    public string? TargetPathologyId { get; set; }

    /// <summary>Engine binding: taxonomy acronym to switch to (e.g. "SR", "ASYSTOLE").</summary>
    public string? TargetAcronym { get; set; }

    public ResultItem Clone() => new()
    {
        Kind = Kind,
        Text = Text.Clone(),
        State = State,
        Weight = Weight,
        TargetPathologyId = TargetPathologyId,
        TargetAcronym = TargetAcronym,
    };
}

public sealed class TransitionProtocol
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public RhythmKind CurrentKind { get; set; } = RhythmKind.Warning;
    public LocText Current { get; set; } = new();
    public List<ActionItem> Actions { get; set; } = new();
    public List<ResultItem> Results { get; set; } = new();
    public LocText Time { get; set; } = new();
    public LocText Conditions { get; set; } = new();

    /// <summary>Engine binding: the clinical rhythm this row applies to (null = display-only, does not drive
    /// the simulator).</summary>
    public ClinicalRhythmState? FromState { get; set; }

    /// <summary>Engine binding: specific concrete pathology ID this row applies to (null = applies to any in FromState).</summary>
    public string? FromPathologyId { get; set; }

    /// <summary>Engine binding: taxonomy acronym this row applies to (e.g. "VFIB", "SR").</summary>
    public string? FromAcronym { get; set; }

    /// <summary>Engine binding: which action fires this transition in the Лечение panel.</summary>
    public TransitionTrigger Trigger { get; set; } = TransitionTrigger.None;

    /// <summary>Engine binding: the specific drug when <see cref="Trigger"/> is <see cref="TransitionTrigger.Drug"/>.
    /// Ignored when <see cref="TriggerCustomDrugId"/> is set.</summary>
    public TreatmentDrug? TriggerDrug { get; set; }

    /// <summary>Engine binding: the <see cref="CustomDrugItem.Id"/> of the authored custom drug that fires this
    /// transition (null = a standard <see cref="TriggerDrug"/>). The id is the stable identity, so renaming the
    /// drug keeps the binding.</summary>
    public string? TriggerCustomDrugId { get; set; }

    /// <summary>Engine binding: real clinical seconds before the effect resolves (the panel compresses this by
    /// the accelerated clock). 0 = instant.</summary>
    public int EffectSeconds { get; set; }

    public TransitionProtocol Clone() => new()
    {
        Id = Id,
        CurrentKind = CurrentKind,
        Current = Current.Clone(),
        Actions = Actions.ConvertAll(a => a.Clone()),
        Results = Results.ConvertAll(r => r.Clone()),
        Time = Time.Clone(),
        Conditions = Conditions.Clone(),
        FromState = FromState,
        FromPathologyId = FromPathologyId,
        FromAcronym = FromAcronym,
        Trigger = Trigger,
        TriggerDrug = TriggerDrug,
        TriggerCustomDrugId = TriggerCustomDrugId,
        EffectSeconds = EffectSeconds,
    };
}

public sealed class ValidationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LocText Lead { get; set; } = new();
    public LocText Body { get; set; } = new();

    public ValidationRule Clone() => new() { Id = Id, Lead = Lead.Clone(), Body = Body.Clone() };
}

public sealed class AclsStep
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AclsNodeKind Kind { get; set; } = AclsNodeKind.Default;
    public LocText Title { get; set; } = new();
    public LocText Subtitle { get; set; } = new();

    public AclsStep Clone() => new() { Id = Id, Kind = Kind, Title = Title.Clone(), Subtitle = Subtitle.Clone() };
}

public sealed class TimingLine
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LocText Text { get; set; } = new();

    public TimingLine Clone() => new() { Id = Id, Text = Text.Clone() };
}

public sealed class DosageEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LocText Drug { get; set; } = new();
    public LocText Indication { get; set; } = new();
    public LocText Dose { get; set; } = new();
    public LocText Route { get; set; } = new();
    public LocText Repeat { get; set; } = new();

    public DosageEntry Clone() => new()
    {
        Id = Id,
        Drug = Drug.Clone(),
        Indication = Indication.Clone(),
        Dose = Dose.Clone(),
        Route = Route.Clone(),
        Repeat = Repeat.Clone(),
    };
}

/// <summary>An authored custom or extended drug defined within a protocol.</summary>
public sealed class CustomDrugItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public LocText Name { get; set; } = new();
    public bool IsIv { get; set; } = true;
    public double DefaultDoseMg { get; set; } = 1.0;

    /// <summary>Dose unit as the author typed it. Empty = unspecified, and the UI shows the localized default
    /// ("mg" / "мг") — a hardcoded literal here would leak one language into every other locale.</summary>
    public string Unit { get; set; } = string.Empty;
    public double? MaxDoseMg { get; set; }

    public CustomDrugItem Clone() => new()
    {
        Id = Id,
        Name = Name.Clone(),
        IsIv = IsIv,
        DefaultDoseMg = DefaultDoseMg,
        Unit = Unit,
        MaxDoseMg = MaxDoseMg,
    };
}

/// <summary>The whole editable protocol set (one JSON document or preset body).</summary>
public sealed class TreatmentProtocolSet
{
    public List<TransitionProtocol> Transitions { get; set; } = new();
    public List<ValidationRule> Rules { get; set; } = new();
    public List<AclsStep> AclsSteps { get; set; } = new();
    public List<TimingLine> Timings { get; set; } = new();
    public List<DosageEntry> Dosages { get; set; } = new();
    public List<CustomDrugItem> CustomDrugs { get; set; } = new();

    public TreatmentProtocolSet Clone() => new()
    {
        Transitions = Transitions.ConvertAll(t => t.Clone()),
        Rules = Rules.ConvertAll(r => r.Clone()),
        AclsSteps = AclsSteps.ConvertAll(s => s.Clone()),
        Timings = Timings.ConvertAll(t => t.Clone()),
        Dosages = Dosages.ConvertAll(d => d.Clone()),
        CustomDrugs = CustomDrugs.ConvertAll(c => c.Clone()),
    };
}

/// <summary>A named treatment protocol preset (e.g. standard ACLS, Moscow Order №2345, regional protocols).</summary>
public sealed class TreatmentProtocolPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsBuiltIn { get; set; }
    public TreatmentProtocolSet ProtocolSet { get; set; } = new();

    public TreatmentProtocolPreset Clone() => new()
    {
        Id = Id,
        Name = Name,
        Description = Description,
        IsBuiltIn = IsBuiltIn,
        ProtocolSet = ProtocolSet.Clone(),
    };
}

/// <summary>Container persisting all saved presets and tracking the active one.</summary>
public sealed class TreatmentPresetContainer
{
    public string ActivePresetId { get; set; } = "default";
    public List<TreatmentProtocolPreset> Presets { get; set; } = new();
}
