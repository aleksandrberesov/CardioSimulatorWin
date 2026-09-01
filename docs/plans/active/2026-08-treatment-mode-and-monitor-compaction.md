# Plan: Treatment/Resuscitation Mode («Лечение») + Monitor Compaction

**Created:** 2026-08-28
**Status:** IN PROGRESS (kicked off 28-08). **Done & verified:** Part 2 compaction (renderer edge-pack); Part 1 Core rules engine + 21 tests; state→rhythm map (+ asystole flat-line synthesis, torsades→PVT approx); `OperatingMode.Treatment` + strings; `TreatmentViewModel` (accelerated clock + event log); `TreatmentScreen` (full panel); MainScreen routing. Build clean, 499 Core tests pass, runtime-verified (mode renders, rhythm resolves+displays, actions apply, VF/CHB transitions + log confirmed).
**Polish pass 1 (29-08, build clean, runtime-verified):** (1) pick chips now restyle **in place** — no full-panel rebuild → no scroll-jump/flicker (each card's pick group is independent); (2) IV dose auto-fills the standard dose on drug select; (3) **localized units** (мг/Дж/уд-мин/мА — were hardcoded Russian) via new `tx_unit_*` keys (EN+RU); (4) O₂/CPR toggle **desync fixed** — a declined confirm / reset snaps the toggle back to real context state (re-entrancy-guarded `SyncToggles`); (5) pending-effect indicator shows the **target rhythm**; (6) rhythm-change combo defaults to the **current** state.
**Polish pass 2 (29-08, build clean, runtime-verified):**
- **Cardiac-arrest CPR banner** — new Core classifier `TreatmentRhythmMap.IsArrestRhythm` (VF/pulseless-VT/asystole; +9 tests → **508 Core tests green**). Panel shows a red alert pill in the status header only during arrest: bright «⚠ Остановка кровообращения — начните СЛР» when CPR is off, softened «…СЛР идёт» once compressions are on. ACLS teaching cue.
- **Reset-crash fixed (regression found & closed):** Reset drove a panel rebuild that re-parented the persistent header/log/banner field elements → XAML `0xc000027b` APPCRASH. Reworked to reset **in place** (no rebuild ever); `RebuildPanel` removed, `BuildPanel` now runs exactly once. Verified: set-VF → arrest banner → CPR toggle → Reset all survive, app stays up. See memory `winui-persistent-element-reparent-crash`.
**Adversarial review + fixes (29-08, 10-agent workflow: 5 lenses × verify; build clean, 519 Core tests green, runtime-verified):**
- **Lifecycle:** pending-effect `DispatcherQueueTimer` now cancelled on Unloaded (`TreatmentViewModel.Stop()`) + `ShowRhythm` nulled, so a queued Tick can't fire after teardown (MainScreen recreates the monitor/rhythm VMs per mode, so the leak was orphan-VM hygiene, not next-screen corruption — but fixed regardless); stale-tick `ReferenceEquals` guard on the timer.
- **Dose input:** `NumberBox.Minimum=0` + blank/≤0 falls back to the standard dose; engine ignores a ≤0 dose (no `RecordDose`) so a negative dose can't corrupt cumulative tracking or defeat the cap.
- **Localization of clinical guidance (was hardcoded English):** engine now returns language-neutral `TreatmentReason` codes; the App localizes them (EN+RU) via `AppStrings.TreatmentReasonText`, inlining the drug name + limit for the dose-cap message. Verified: RU dialog shows «Несинхронизированный разряд…».
- **Clinical fidelity:** unsync shock on an organized/perfusing rhythm now **warns** (was a silent no-op); `SetRhythm` and any arrest→organized conversion clear `FailedDefibCount`/`AdrenalinePrimed` (no stale-counter leak); rate-controlled AFib is now cardiovertible + amiodarone-convertible (was a dead-end); amiodarone in VF/pVT is an **adjunct that primes the next shock** (no drug-only defibrillation); atropine in complete AV block dropped to ~10% + a "pace instead" warning (was 50%).
- **Robustness:** `ShowRhythm` logs a warning when no acronym resolves (only on a reduced pak) instead of silently leaving a contradicting trace.
- 11 new engine tests (41 Treatment tests total). 1 finding rejected (enum-index cast — safe today).

**Header «Отмена/Применить» (29-08, build clean, runtime-verified):** implemented the mockup's header — «Лечение» title + **Отмена** + **Применить** (reusing the already-5-lang `common_cancel` / `seg_apply` labels). **Отмена** = reset-all with a «Сбросить все действия?» confirmation and a full reset (selections, dose, rhythm, **instrument sliders** energy/pace/sync, engine/context, log — all in place, no rebuild). **Применить** (customer decision) = **fast-forward** any in-progress delayed effect (`TreatmentViewModel.CommitPendingNow`); enabled only while an effect is pending, dimmed otherwise. Verified end-to-end: header renders, Применить dims/lights with pending state, a Metoprolol→rate-controlled effect committed the same second via Применить, and Отмена's confirm→full-reset works. 2 new EN+RU keys (`tx_confirm_reset_all`, `tx_no_pending`) added to the translation worklist (now 88 strings). Translation worklist: `docs/i18n/treatment-mode-translation.tsv` (+ README).

**Remaining:** author real asystole/torsades .dat + acronyms (needs the tagging pipeline, like C1); **tx_* strings not translated into ZH/ES/HI** (fall back to English — consistent with those dicts being broadly partial; deferred: machine-translating clinical terms is risky, needs a translator); further polish (mockup Отмена/Применить commit-staging, scenario save?); Android sync. Not committed.
**Platform:** Windows-first (then Android sync)
**Sources (customer, 28-08-2026):**
- `Docs/дизайн панели лечение.html` — UI mockup of the treatment panel.
- `Docs/логика перехода ритмов.html` — spec: rhythm-transition table, validation rules, ACLS flow, reference JS engine, drug dosages.
- `Docs/уплотним.png` — annotation asking to compact the 12-lead monitor's dead vertical space.

These are **two independent deliverables**: a large new **Treatment mode** (Parts 1) and a small **monitor layout fix** (Part 2).

---

## Part 1 — «Лечение»: treatment / resuscitation simulation  — **XL** (multi-phase)

A new therapeutic layer over the ECG monitor: the user applies treatments (IV drugs, defibrillation, pills, pacing, vagal maneuvers, O₂/CPR, direct rhythm change) and the **displayed rhythm transitions** per an ACLS rules engine — with probabilities, preconditions, timing delays, and validation warnings. An event log records everything.

### What the mockups define

**UI** (`дизайн панели лечение.html`): header (Лечение · Отмена/Применить) + two columns.
- **Действия** (7 action cards): В/В лекарство (Адреналин/Амиодарон/Атропин/Магния сульфат/Кальция хлорид + dose + Ввести); Разряд ДФБ (energy 50–360 Дж slider + РАЗРЯД); Таблетка (Нитро/Аспирин/Метопролол); ЭКС (rate + current sliders + Старт); Вагусные пробы (Вальсальва/Массаж каротидного синуса); Кислород/ИВЛ (toggle); СЛР (toggle).
- **Ритм и журнал**: rhythm dropdown + «Установить ритм» + a live ECG mini-strip; **Журнал событий** (timestamped log).

**Logic** (`логика перехода ритмов.html`): a transition table (current rhythm × action → outcome rhythm(s) with probabilities / effect-time / conditions), 8 validation rules, an ACLS flowchart, a reference `rhythmTransitions` + `applyAction()` engine, and a standard-dosage table.

### 1a. Core — rhythm-transition engine (pure, testable)  — **L**
New in `src/CardioSimulator.Core/Domain/` (e.g. `Treatment/`):
- **`ClinicalRhythmState`** enum — the abstract states the engine reasons over: `Sinus`, `SinusTachy`, `AFib`, `SVT`, `VT` (pulsed), `VFib`, `Torsades`, `Asystole`, `CompleteAVBlock`, `Paced`, … (superset of the mockup's dropdown + the transition table).
- **`TreatmentAction`** — drug (+dose), defib (+energy, sync/async), synchronized cardioversion, pacing (+rate/current), vagal maneuver, O₂, CPR toggle, direct rhythm-set.
- **`TreatmentContext`** — mutable scenario state the rules read: `CprActive`, `OxygenOn`, `FailedDefibCount`, cumulative doses per drug (for max-dose limits), elapsed sim time.
- **`RhythmTransitions`** — the table from the spec as data (per state → per action → `{ outcomes[], probabilities[], effectTime, conditions, warning, effect }`). Port `applyAction(current, action, ctx)` → `TransitionResult { NewState, DelaySeconds, Warning?, Blocked? }` with probability-weighted outcome selection (inject the RNG for deterministic tests) and condition checks (CPR required, min failed-defibs, dose caps).
- **`TreatmentValidator`** — the 8 rules (defib-on-asystole blocked; async-defib-on-pulsed-VT → R-on-T confirm→VF; adrenaline needs CPR else −50%; amiodarone ≤2.2 g/day; atropine ≤3 mg; pacing current 30–150 mA + rate > intrinsic; CPR mandatory in VF/asystole; O₂ gate). Returns block / warn / ok.
- **`DrugCatalog`** — the dosage table (indications, standard dose, route, repeat/max) — drives dose defaults + limit checks.
- **Fully unit-tested** — this is the highest-value, lowest-risk piece and mirrors the spec's own JS 1:1.

### 1b. Clinical-state ↔ pathology mapping  — **M** *(has real gaps)*
The engine works on abstract states; the monitor shows a concrete `PathologyEntry`. Need a curated **state → representative rhythm** resolver (via taxonomy acronyms):
- Maps cleanly: `VFib`→`VFIB`, `VT`→`PVT`, `AFib`→`AFIB`, `SVT`→`SVT`/`AVNRT`/`AVRT`, `SinusTachy`→`ST`, `CompleteAVBlock`→`3AVB`, `Paced`→`APACE`, `Sinus`→a base sinus rhythm.
- **Gaps → CORRECTED (28-08 via scoping).** The waveforms **already exist** in the dataset (`Asystole` = `ecg00010`, `Torsades de pointes` = `ecg00051`) — what's missing is **acronym wiring**: asystole is untagged, and torsades auto-tags to `PVT` (indistinguishable from plain VT). Two ways to close it: **(a)** add `ASYS` + `TDP` rows to `Taxonomy.tsv` + rules to `Tools/taxonomy-build/build_rhythm_acronyms.py` (TDP must beat the generic `ventricular tachycardia`→PVT) and re-tag the pak (needs the tagging pipeline, like C1); **(b) pragmatic, no pipeline:** the state→pathology resolver **direct-maps** Asystole→`ecg00010` and Torsades→`ecg00051` by id (verify both ids are in the shipped subset pak), while all other states resolve by acronym. **Recommend (b)** to avoid the data-pipeline dependency; add the acronyms later when the full DB is re-tagged (see C1).
- Where multiple pathologies match a state, pick a representative (or keep the current one if it already matches the target state, to avoid a jarring waveform swap).

### 1c. Treatment mode + panel UI  — **L**
**DECIDED (28-08): a dedicated `OperatingMode.Treatment` («Лечение»)** — monitor + treatment panel side-by-side, mirroring how `ExaminationScreen`/`OSKEScreen` host the monitor beside their panels. Steps:
- Append `Treatment` to `OperatingMode` (last, for Android parity — see the enum's ordering note), add `mode_treatment` title, route it in `MainScreen.xaml.cs` and the mode list.
- New `TreatmentScreen` + `TreatmentViewModel`: the live 12-lead on one side, the action cards (drugs/defib/pills/pacing/vagal/O₂/CPR) + rhythm control + event log on the other — native WinUI, `AppStrings`, theme-aware (dark/light), reusing the shared `MonitorView`/`_monitorVm`/`_rhythmVm` (as the exam screens do).
- Cards, sliders, toggles, drug pickers, dose inputs, event log — all bound to the engine/context. The mockup's `applyAll/resetAll` map to commit/reset; the real 12-lead replaces the mockup's mini-strip.

### 1d. Integration — apply outcomes to the live monitor  — **M**
- On an action: run `TreatmentValidator` → (block+toast) or (warn+confirm) or ok; then `applyAction` → `TransitionResult`.
- Apply the new state after `DelaySeconds` (a `DispatcherQueueTimer`; instant effects = 0) via the state→pathology resolver + `RhythmViewModel.SelectRhythm(id, persist:false)` — the existing rhythm-set path. Show a "pending effect" indicator during the delay.
- Update `TreatmentContext` (CPR/O₂/defib count/doses/time); append to the **event log**.
- **Timing model → DECIDED (28-08): accelerated / instructor-controlled clock** — compress effect delays (a 10-min effect resolves in seconds) with an instructor speed control; the engine returns realistic `DelaySeconds` and the sim scales them by the speed factor.
- **Outcomes → DECIDED (28-08): probabilistic** — keep the spec's probabilities (defib 75/25, adenosine 90–95%, …) so the same action can succeed or fail; the injected RNG stays for realism (a deterministic instructor toggle can be added later if wanted).

### Suggested phasing for Part 1
1. **Core engine + tests** (1a) — self-contained, no UI risk; the bulk of the clinical value.
2. **State↔pathology resolver** (1b) + author asystole/torsades rhythms.
3. **Panel UI** (1c) wired to the engine (dry-run: log only, no monitor change).
4. **Live integration** (1d) — apply to the monitor with timing + context + log.
5. Polish: validation toasts, "pending effect" UI, reset/apply, persistence of a session/scenario if wanted.

---

## Part 2 — «уплотним»: compact the 12-lead monitor layout  — **S**

**ROOT CAUSE (corrected 28-08 via scoping — NOT a host-layout issue).** The canvas already fills the card edge-to-edge (verified: grid runs to the bottom bar). The band is `EcgRenderer.Render` **centering each lead in its cell**: `cellH = height/rows` (`EcgRenderer.cs:149`), `baselineY = cellY + cellH/2` (`:192`) — so the first baseline sits ½-cell below the top and the last ½-cell above the bottom, leaving an empty half-cell (~64px) of grid below aVF/V6 that reads as a dead band. A host change fixes nothing.

**Change (in the renderer):** edge-pack the baselines so the rows spread from a small top pad to a small bottom pad instead of each centering in a full cell — after `cellH`, compute `vPad = cellH*RowEdgePad` (≈0.30), `vStep = rows>1 ? (height-2*vPad)/(rows-1) : 0`, and set `baselineY = rows>1 ? vPad + row*vStep : height/2`, `cellY = baselineY - cellH/2` (keeps clips centered on the baseline). Also mirror the same packing in `PaneIndexAt` (`:400-416`) so compare-mode pane taps stay aligned. Verify across 1/2/grid schemes (no tall-deflection clipping — tune `RowEdgePad`); labels/calibration/EOS/pQRSt overlays follow `baselineY` automatically. **Parity:** `EcgSvgRenderer.cs:261` and Android use the same centered layout — mirror there if lecture figures should match. Low risk, self-contained; ship independently.

---

## Decisions & remaining questions

**Resolved (customer, 28-08):**
1. **Placement** → dedicated `OperatingMode.Treatment`.
2. **Timing** → accelerated / instructor-controlled clock.
3. **Asystole & Torsades** → author dedicated `.dat` rhythms (+ taxonomy acronyms).
5. **Randomness** → probabilistic outcomes (RNG kept).

**Still open (can start Part 1 Core without these):**
4. **Rhythm scope** — confirm the full state set for launch (the transition table covers ~10; the mockup dropdown lists 6). Reasonable default: implement every state the transition table references.
6. **Session/scenario** — is the event log ephemeral, or saved like an exam result (and reviewable later / part of a graded scenario)? Affects whether Treatment needs a results store.
7. **Who drives it** — instructor-led teaching vs student self-practice (affects edition/visibility gating and the security guard, like the exam modes).

---

## Effort & sequencing

- **Part 2 (compaction)** — small, independent → ship first.
- **Part 1** — XL, phased: Core engine (+tests) → state mapping (+authored rhythms) → panel UI → live integration. Each phase is independently reviewable; the Core engine is the safe, high-value starting point.
- **Android sync** — Windows-first; parity plan under `Android/docs/plans/sync/` once each Windows phase lands.
