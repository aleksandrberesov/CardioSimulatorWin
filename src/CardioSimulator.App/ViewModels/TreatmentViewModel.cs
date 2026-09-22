using System;
using System.Collections.Generic;
using CardioSimulator.App.Localization;
using CardioSimulator.Core.Domain.Treatment;
using Microsoft.UI.Dispatching;

namespace CardioSimulator.App.ViewModels;

/// <summary>Category of an event-log line (drives its colour in the journal).</summary>
public enum TreatmentLogKind { Info, Action, Outcome, Warning }

/// <summary>One timestamped line in the treatment event log («Журнал событий»).</summary>
public sealed record TreatmentLogEntry(string Time, string Message, TreatmentLogKind Kind);

/// <summary>
/// Orchestrates a treatment/resuscitation session for the «Лечение» mode: holds the current
/// <see cref="ClinicalRhythmState"/> and <see cref="TreatmentContext"/>, delegates the clinical logic to the
/// pure <see cref="TreatmentEngine"/>, records the event log, and applies delayed rhythm effects on an
/// accelerated (instructor-controlled) clock. The host wires <see cref="ShowRhythm"/> to actually display the
/// resulting rhythm on the monitor, keeping this view-model free of the rhythm/monitor plumbing.
/// </summary>
public sealed class TreatmentViewModel
{
    private readonly List<TreatmentLogEntry> _log = new();
    private readonly Random _rng = new();
    private readonly DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _effectTimer;
    private DispatcherQueueTimer? _countdownTimer;
    private ClinicalRhythmState? _pendingState;
    private string? _pendingTargetPathologyId;
    private string? _pendingTargetAcronym;
    private double _pendingTotalSeconds;
    private double _pendingRemainingSeconds;

    public TreatmentViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>The rhythm currently displayed on the monitor.</summary>
    public ClinicalRhythmState CurrentState { get; private set; } = ClinicalRhythmState.Sinus;

    /// <summary>The ID of the pathology currently active/seeded or targeted, if known.</summary>
    public string? CurrentPathologyId { get; private set; }

    /// <summary>The acronyms of the pathology currently active/seeded, if known.</summary>
    public IReadOnlyList<string>? CurrentAcronyms { get; private set; }

    /// <summary>The scenario context (CPR/O₂, failed shocks, doses) the rules read.</summary>
    public TreatmentContext Context { get; } = new();

    /// <summary>The instructor-authored transition table (from the editable «Протоколы лечения»). When set and
    /// non-empty, its rules govern the rhythm outcomes; anything it does not cover falls back to the engine's
    /// built-in logic. Null/empty ⇒ pure built-in behaviour.</summary>
    public AuthoredTreatmentTable? AuthoredTable { get; set; }

    public IReadOnlyList<TreatmentLogEntry> Log => _log;

    /// <summary>True while a delayed effect is scheduled but has not yet fired (a drug/therapy is "working").</summary>
    public bool HasPendingEffect => _pendingState is not null;

    /// <summary>The rhythm the pending delayed effect will resolve to, or null if nothing is pending.</summary>
    public ClinicalRhythmState? PendingState => _pendingState;

    /// <summary>Total simulated-delay duration (in real seconds) of the current pending effect.</summary>
    public double PendingTotalSeconds => _pendingTotalSeconds;

    /// <summary>Remaining time (in real seconds) before the current pending effect resolves.</summary>
    public double PendingRemainingSeconds => _pendingRemainingSeconds;

    /// <summary>Accelerated-clock factor: simulated seconds per real second. 60 = a 1-minute effect resolves
    /// in 1 s. Instructor-adjustable; clamped ≥ 1.</summary>
    public double SpeedFactor { get; set; } = 60;

    /// <summary>Raised when the state / context / pending-effect changes (re-render the panel + status).</summary>
    public event Action? StateChanged;

    /// <summary>Raised when a log line is added.</summary>
    public event Action? LogChanged;

    /// <summary>Host hook: display <paramref name="state"/> (and optional target pathology ID or acronym) on the monitor (resolve state → rhythm and call
    /// SelectRhythm / ShowFlatline). Set by the screen.</summary>
    public Action<ClinicalRhythmState, string?, string?>? ShowRhythm { get; set; }

    /// <summary>Pre-checks an action so the screen can block or confirm before applying (see
    /// <see cref="Apply"/>).</summary>
    public TreatmentValidation Validate(TreatmentAction action) =>
        TreatmentEngine.Validate(CurrentState, action, Context);

    /// <summary>
    /// Applies <paramref name="action"/>: runs the engine (updating the context), logs the action + any
    /// warning, and schedules the resulting rhythm change after the accelerated-clock delay (instant effects
    /// apply immediately). A blocked action logs the reason and leaves the rhythm unchanged. The screen
    /// should have already blocked/confirmed per <see cref="Validate"/>.
    /// </summary>
    public void Apply(TreatmentAction action)
    {
        var result = AuthoredTable is { IsEmpty: false }
            ? TreatmentEngine.Apply(CurrentState, action, Context, AuthoredTable, _rng.NextDouble, CurrentPathologyId, CurrentAcronyms)
            : TreatmentEngine.Apply(CurrentState, action, Context, _rng.NextDouble, CurrentPathologyId, CurrentAcronyms);
        AddLog(DescribeAction(action), TreatmentLogKind.Action);

        if (result.Blocked)
        {
            var blockMsg = AppStrings.TreatmentReasonText(result.Warning, action);
            AddLog(string.IsNullOrEmpty(blockMsg) ? AppStrings.TreatmentLogNoEffect : blockMsg, TreatmentLogKind.Warning);
            StateChanged?.Invoke();
            return;
        }
        if (result.Warning != TreatmentReason.None)
            AddLog(AppStrings.TreatmentReasonText(result.Warning, action), TreatmentLogKind.Warning);

        // No rhythm change (a toggle, priming, or a no-rule action) — just reflect context.
        if (result.NewState == CurrentState && result.TargetPathologyId is null && result.TargetAcronym is null)
        {
            StateChanged?.Invoke();
            return;
        }

        var realDelay = result.EffectSeconds / Math.Max(1.0, SpeedFactor);
        if (realDelay <= 0.05)
        {
            CommitState(result.NewState, result.TargetPathologyId, result.TargetAcronym);
        }
        else
        {
            ScheduleCommit(result.NewState, result.TargetPathologyId, result.TargetAcronym, realDelay);
            AddLog(AppStrings.TreatmentLogEffectPendingFormat(
                AppStrings.TreatmentStateName(result.NewState), FormatClinicalTime(result.EffectSeconds)),
                TreatmentLogKind.Info);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Resets the scenario bookkeeping (context, doses, log, pending effects). Does NOT change the
    /// displayed rhythm or the current state — the host re-seeds <see cref="CurrentState"/> from the rhythm
    /// currently on the monitor after calling this.</summary>
    public void Reset()
    {
        CancelPending();
        Context.Reset();
        _log.Clear();
        AddLog(AppStrings.TreatmentLogReset, TreatmentLogKind.Info);
        StateChanged?.Invoke();
    }

    /// <summary>Seeds <see cref="CurrentState"/> from the rhythm already shown on the monitor.
    /// Called when the treatment panel opens and whenever the displayed rhythm changes externally.</summary>
    public void SeedState(ClinicalRhythmState state, string? pathologyId = null, string? rhythmTitle = null, IReadOnlyList<string>? acronyms = null)
    {
        if (state == CurrentState && pathologyId == CurrentPathologyId) return;
        CancelPending();
        CurrentState = state;
        CurrentPathologyId = pathologyId;
        CurrentAcronyms = acronyms;
        if (!string.IsNullOrWhiteSpace(rhythmTitle))
        {
            AddLog(AppStrings.TreatmentLogInitialRhythmFormat(rhythmTitle), TreatmentLogKind.Info);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Screen teardown: cancel any pending delayed effect so a queued <see cref="DispatcherQueueTimer"/>
    /// Tick cannot fire after the screen has unloaded (it would touch the orphaned rhythm view-model).</summary>
    public void Stop() => CancelPending();

    /// <summary>«Применить»: commit any in-progress delayed effect immediately (skip the accelerated-clock wait).
    /// Returns false if nothing was pending.</summary>
    public bool CommitPendingNow()
    {
        if (_pendingState is not { } s) return false;
        var targetId = _pendingTargetPathologyId;
        var targetAcr = _pendingTargetAcronym;
        CommitState(s, targetId, targetAcr); // stops the timer, applies the rhythm, logs the outcome
        return true;
    }

    /// <summary>Appends a system note to the event log (e.g. a display-resolution warning raised by the screen).</summary>
    public void LogSystem(string message, TreatmentLogKind kind = TreatmentLogKind.Warning) => AddLog(message, kind);

    // ── internals ────────────────────────────────────────────────────────────

    private void ScheduleCommit(ClinicalRhythmState state, string? targetPathologyId, string? targetAcronym, double realSeconds)
    {
        CancelPending();
        _pendingState = state;
        _pendingTargetPathologyId = targetPathologyId;
        _pendingTargetAcronym = targetAcronym;
        _pendingTotalSeconds = realSeconds;
        _pendingRemainingSeconds = realSeconds;

        if (_dispatcher is null) { CommitState(state, targetPathologyId, targetAcronym); return; } // no UI thread (tests) → immediate
        _effectTimer = _dispatcher.CreateTimer();
        _effectTimer.Interval = TimeSpan.FromSeconds(realSeconds);
        _effectTimer.IsRepeating = false;
        // Guard against a stale tick: if this timer was superseded (no longer _effectTimer), ignore it so it
        // can't commit a newer pending state early. Stop() should dequeue it, but this is belt-and-suspenders.
        _effectTimer.Tick += (t, _) =>
        {
            t.Stop();
            if (!ReferenceEquals(t, _effectTimer)) return;
            if (_pendingState is { } s) CommitState(s, _pendingTargetPathologyId, _pendingTargetAcronym);
        };
        _effectTimer.Start();

        _countdownTimer = _dispatcher.CreateTimer();
        _countdownTimer.Interval = TimeSpan.FromMilliseconds(100);
        _countdownTimer.IsRepeating = true;
        _countdownTimer.Tick += (t, _) =>
        {
            if (!ReferenceEquals(t, _countdownTimer)) return;
            _pendingRemainingSeconds = Math.Max(0, _pendingRemainingSeconds - 0.1);
            StateChanged?.Invoke();
            if (_pendingRemainingSeconds <= 0) t.Stop();
        };
        _countdownTimer.Start();
    }

    private void CancelPending()
    {
        _effectTimer?.Stop();
        _effectTimer = null;
        _countdownTimer?.Stop();
        _countdownTimer = null;
        _pendingState = null;
        _pendingTargetPathologyId = null;
        _pendingTargetAcronym = null;
        _pendingTotalSeconds = 0;
        _pendingRemainingSeconds = 0;
    }

    private void CommitState(ClinicalRhythmState state, string? targetPathologyId, string? targetAcronym = null)
    {
        CancelPending();
        if (state == CurrentState && (targetPathologyId == null || targetPathologyId == CurrentPathologyId) && targetAcronym == null)
        {
            StateChanged?.Invoke();
            return;
        }
        CurrentState = state;
        CurrentPathologyId = targetPathologyId;
        ShowRhythm?.Invoke(state, targetPathologyId, targetAcronym);
        AddLog(AppStrings.TreatmentLogRhythmChangedFormat(AppStrings.TreatmentStateName(state)), TreatmentLogKind.Outcome);
        StateChanged?.Invoke();
    }

    private void AddLog(string message, TreatmentLogKind kind)
    {
        _log.Insert(0, new TreatmentLogEntry(DateTime.Now.ToString("HH:mm:ss"), message, kind));
        LogChanged?.Invoke();
    }

    private static string DescribeAction(TreatmentAction action) => action switch
    {
        TreatmentAction.Drug d => AppStrings.TreatmentLogDrugFormat(
            d.CustomName ?? AppStrings.TreatmentDrugName(d.Which), d.DoseMg),
        TreatmentAction.Defib s => s.Synchronized
            ? AppStrings.TreatmentLogCardioversionFormat(s.EnergyJoules)
            : AppStrings.TreatmentLogDefibFormat(s.EnergyJoules),
        TreatmentAction.Pacing p => AppStrings.TreatmentLogPacingFormat(p.RateBpm, p.CurrentMa),
        TreatmentAction.Vagal v => AppStrings.TreatmentLogVagalFormat(AppStrings.TreatmentVagalName(v.Maneuver)),
        TreatmentAction.Oxygen o => o.On ? AppStrings.TreatmentLogOxygenOn : AppStrings.TreatmentLogOxygenOff,
        TreatmentAction.Cpr c => c.On ? AppStrings.TreatmentLogCprOn : AppStrings.TreatmentLogCprOff,
        TreatmentAction.SetRhythm r => AppStrings.TreatmentLogSetRhythmFormat(AppStrings.TreatmentStateName(r.State)),
        _ => string.Empty,
    };

    /// <summary>Human-readable clinical effect time ("instant" / "N sec" / "N min") for the log.</summary>
    private static string FormatClinicalTime(double seconds) => seconds switch
    {
        <= 0 => AppStrings.TreatmentTimeInstant,
        < 60 => AppStrings.TreatmentTimeSecondsFormat((int)Math.Round(seconds)),
        _ => AppStrings.TreatmentTimeMinutesFormat((int)Math.Round(seconds / 60)),
    };
}
