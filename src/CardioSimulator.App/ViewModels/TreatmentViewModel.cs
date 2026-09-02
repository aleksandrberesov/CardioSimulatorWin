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
    private ClinicalRhythmState? _pendingState;

    public TreatmentViewModel()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>The rhythm currently displayed on the monitor.</summary>
    public ClinicalRhythmState CurrentState { get; private set; } = ClinicalRhythmState.Sinus;

    /// <summary>The scenario context (CPR/O₂, failed shocks, doses) the rules read.</summary>
    public TreatmentContext Context { get; } = new();

    public IReadOnlyList<TreatmentLogEntry> Log => _log;

    /// <summary>True while a delayed effect is scheduled but has not yet fired (a drug/therapy is "working").</summary>
    public bool HasPendingEffect => _pendingState is not null;

    /// <summary>The rhythm the pending delayed effect will resolve to, or null if nothing is pending.</summary>
    public ClinicalRhythmState? PendingState => _pendingState;

    /// <summary>Accelerated-clock factor: simulated seconds per real second. 60 = a 1-minute effect resolves
    /// in 1 s. Instructor-adjustable; clamped ≥ 1.</summary>
    public double SpeedFactor { get; set; } = 60;

    /// <summary>Raised when the state / context / pending-effect changes (re-render the panel + status).</summary>
    public event Action? StateChanged;

    /// <summary>Raised when a log line is added.</summary>
    public event Action? LogChanged;

    /// <summary>Host hook: display <paramref name="state"/> on the monitor (resolve state → rhythm and call
    /// SelectRhythm / ShowFlatline). Set by the screen.</summary>
    public Action<ClinicalRhythmState>? ShowRhythm { get; set; }

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
        var result = TreatmentEngine.Apply(CurrentState, action, Context, _rng.NextDouble);
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
        if (result.NewState == CurrentState)
        {
            StateChanged?.Invoke();
            return;
        }

        var realDelay = result.EffectSeconds / Math.Max(1.0, SpeedFactor);
        if (realDelay <= 0.05)
        {
            CommitState(result.NewState);
        }
        else
        {
            ScheduleCommit(result.NewState, realDelay);
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

    /// <summary>Seeds <see cref="CurrentState"/> from the rhythm already shown on the monitor — no rhythm change
    /// and no log entry. Called when the treatment panel opens and whenever the displayed rhythm changes
    /// externally (the user picks a different Teaching rhythm), so an intervention transitions from the REAL
    /// displayed rhythm. A change out from under a pending effect cancels that effect.</summary>
    public void SeedState(ClinicalRhythmState state)
    {
        if (state == CurrentState) return;
        CancelPending();
        CurrentState = state;
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
        CommitState(s); // stops the timer, applies the rhythm, logs the outcome
        return true;
    }

    /// <summary>Appends a system note to the event log (e.g. a display-resolution warning raised by the screen).</summary>
    public void LogSystem(string message, TreatmentLogKind kind = TreatmentLogKind.Warning) => AddLog(message, kind);

    // ── internals ────────────────────────────────────────────────────────────

    private void ScheduleCommit(ClinicalRhythmState state, double realSeconds)
    {
        CancelPending();
        _pendingState = state;
        if (_dispatcher is null) { CommitState(state); return; } // no UI thread (tests) → immediate
        _effectTimer = _dispatcher.CreateTimer();
        _effectTimer.Interval = TimeSpan.FromSeconds(realSeconds);
        _effectTimer.IsRepeating = false;
        // Guard against a stale tick: if this timer was superseded (no longer _effectTimer), ignore it so it
        // can't commit a newer pending state early. Stop() should dequeue it, but this is belt-and-suspenders.
        _effectTimer.Tick += (t, _) => { t.Stop(); if (!ReferenceEquals(t, _effectTimer)) return; if (_pendingState is { } s) CommitState(s); };
        _effectTimer.Start();
    }

    private void CancelPending()
    {
        _effectTimer?.Stop();
        _effectTimer = null;
        _pendingState = null;
    }

    private void CommitState(ClinicalRhythmState state)
    {
        CancelPending();
        if (state == CurrentState) { StateChanged?.Invoke(); return; }
        CurrentState = state;
        ShowRhythm?.Invoke(state);
        AddLog(AppStrings.TreatmentLogRhythmFormat(AppStrings.TreatmentStateName(state)), TreatmentLogKind.Outcome);
        StateChanged?.Invoke();
    }

    private void AddLog(string message, TreatmentLogKind kind)
    {
        _log.Insert(0, new TreatmentLogEntry(DateTime.Now.ToString("HH:mm:ss"), message, kind));
        LogChanged?.Invoke();
    }

    private static string DescribeAction(TreatmentAction action) => action switch
    {
        TreatmentAction.Drug d => AppStrings.TreatmentLogDrugFormat(AppStrings.TreatmentDrugName(d.Which), d.DoseMg),
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
