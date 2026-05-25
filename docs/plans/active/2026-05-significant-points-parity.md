# Plan: Significant Points / ECG-annotation parity (Windows port)

**Created:** 2026-05-24
**Goal:** Bring the WinUI 3 port to 1:1 parity with the Android app by implementing the
Significant Points / ECG-annotation subsystem (the one feature fully missing from the port),
plus a few secondary polish items.

Source of truth = the Android app at
`C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\` + `docs/`.
This is a faithful replica — match Android behavior/visuals, no redesign.

## Gap summary

Significant points (P/Q/R/S/T peaks + QRS/wave boundaries) are stored per-pathology and rendered
as a labeled overlay with interval/segment measurements (P, PR, QRS, ST, T, QT, R-R) in Teaching,
Testing, and the Editor. The Windows port has **none** of it. The shipped 56-file dataset has no
`markers:` lines yet, so this is additive (no current data loss), but it is required for editor parity.

At parity already (not gaps): Examination/OSKE (empty in Android too), CalibrationPulse, rhythm
search box, GridScheme options, Settings sections, Core manifest/sample parsing.

## Phases

### Phase 1 — Core domain + persistence (foundation, unit-testable)
- New `src/CardioSimulator.Core/Domain/SignificantPoint.cs`: `EcgPointType` enum (11 values:
  P_START, P_PEAK, P_END, QRS_START, Q_PEAK, R_PEAK, S_PEAK, QRS_END, T_START, T_PEAK, T_END;
  each with label + RU description) and `SignificantPoint(int Index, EcgPointType Type)` record.
- `Domain/Pathology.cs`: add `IReadOnlyList<SignificantPoint> SignificantPoints` to `PathologyFile`
  (default empty).
- `Domain/PathologyParser.cs`: parse the header `markers:` field (`index:TYPE,...`) in
  `ParsePathology`; serialize it (after `leads:`, before lead blocks) in `SerializePathology`.
  Mirror Android `parseMarkers` + serialize block.
- Tests in `tests/CardioSimulator.Core.Tests/PathologyParserTests.cs`: round-trip with markers,
  parse a `markers:` line, tolerate unknown/missing markers.

### Phase 2 — ViewModel state
- `App/ViewModels/RhythmViewModel.cs`: add `SignificantPoints` observable property; populate from
  the full `PathologyFile` in `SelectRhythm`.
- `App/ViewModels/EditorViewModel.cs`: add `SelectedIndex` + `SelectIndex/SelectNext/SelectPrevious`,
  `MoveSelectedUp/MoveSelectedDown`, `ToggleSignificantPoint(Lead, int, EcgPointType)`; reset
  `SelectedIndex` on `SelectLead`/`SelectPathology`.

### Phase 3 — Win2D overlay rendering
- `App/Rendering/EcgRenderer.cs`: draw markers (circle + white inner dot), peak labels (P/Q/R/S/T),
  boundary lines for `_START`/`_END`, and interval/segment brackets+labels (QRS, PR segment, ST,
  P, T durations, PR interval, QT interval, R-R). Mirror `SignificantPointOverlay.kt` geometry.
- Plumb significant points into `EcgMonitorControl` / `MonitorView` and `EditableLeadControl`.

### Phase 4 — Teaching/Testing wiring
- `App/Screens/TeachingScreen.cs`, `TestingScreen.cs`: feed `RhythmViewModel.SignificantPoints`
  to the renderer so the overlay shows in the monitor leads.

### Phase 5 — Editor UI
- `App/Controls/EditableLeadControl.cs`: tap-to-select sample (pointer X → nearest index → event),
  highlight selected sample, draw the overlay.
- New `SignificantPointPanel` (right, 150px): header, sample label, FilterChip groups
  (P wave / QRS complex / T wave) toggling points, R-R list when ≥2 R peaks.
- New `EditorControlPanel` (bottom row): point-nav (◀ time ▶), ADC adjust (▼ value ▲), speed (− value +);
  value cells open numeric input dialogs (time→index, set ADC, set speed).
- `App/Controls/Tab.xaml.cs`: add accelerating hold-to-repeat (mirror `repeatingClickable`: 600ms
  initial → 50ms min) for the editor's ◀▶▼▲−+ cells.
- Wire into `App/Screens/EditorScreen.cs` (right panel) and the Editor branch of
  `App/Screens/MainScreen.xaml.cs` bottom panel.

### Phase 6 — Localization
- `App/Localization/AppStrings.cs`: add ~25 keys (EN/RU/ZH/ES) from `res/values*/strings.xml`:
  `ecg_point_*` (11), `ecg_interval_p/pr/qrs/st/t/qt`, `ecg_rr_value_format`,
  `editor_significant_points`, `editor_sample_label`, `editor_p_wave/qrs_complex/t_wave`,
  `editor_select_point_hint`, `editor_set_time_title`, `editor_time_unit/format`,
  `editor_set_adc_title`, `editor_adc_label/format`, `monitor_speed_title/unit`,
  `editor_rename_ok/cancel`.

### Phase 7 — Polish (independent)
- Bundled default dataset (Android `AssetPathologySource` equivalent) for first-run parity.
- Settings: IP-regex validation + inline errors + 1s debounced auto-connect + error dialog +
  "Connecting" spinner.
- MonitorControlPanel: route hardcoded EN labels through `AppStrings`.

## Order / dependencies
1 → 2 → 3 → {4, 5} → 6. Phase 7 is independent.

## Verification
- Phases 1–2: `dotnet test` (Core tests).
- Phases 3–6: `dotnet build` clean, then visual run (`build.ps1` / RID `win-x64`).
