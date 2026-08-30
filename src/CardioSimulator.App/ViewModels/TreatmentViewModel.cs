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
            AddLog(result.Warning ?? AppStrings.TreatmentLogNoEffect, TreatmentLogKind.Warning);
            StateChanged?.Invoke();
            return;
        }
        if (result.Warning is { } w)
            AddLog(w, TreatmentLogKind.Warning);

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

    /// <summary>Resets the scenario to a fresh sinus-rhythm patient (clears context, log, pending effects).</summary>
    public void Reset()
    {
        CancelPending();
        Context.Reset();
        CurrentState = ClinicalRhythmState.Sinus;
        _log.Clear();
        AddLog(AppStrings.TreatmentLogReset, TreatmentLogKind.Info);
        ShowRhythm?.Invoke(CurrentState);
        StateChanged?.Invoke();
    }

    /// <summary>Initializes the displayed rhythm (called once when the screen opens).</summary>
    public void ShowCurrent() => ShowRhythm?.Invoke(CurrentState);

    // ── internals ────────────────────────────────────────────────────────────

    private void ScheduleCommit(ClinicalRhythmState state, double realSeconds)
    {
        CancelPending();
        _pendingState = state;
        if (_dispatcher is null) { CommitState(state); return; } // no UI thread (tests) → immediate
        _effectTimer = _dispatcher.CreateTimer();
        _effectTimer.Interval = TimeSpan.FromSeconds(realSeconds);
        _effectTimer.IsRepeating = false;
        _effectTimer.Tick += (t, _) => { t.Stop(); if (_pendingState is { } s) CommitState(s); };
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
