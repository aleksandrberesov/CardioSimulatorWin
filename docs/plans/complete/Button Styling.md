# Unify Button Styling and Localization in Monitor Control Panel

This plan outlines the changes to make all buttons in the monitor control panel consistent in typography (font size, style, and colour) and ensure all their titles are localized.

## Proposed Changes

### Localization System

#### [MODIFY] [AppStrings.cs](file:///e:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.App/Localization/AppStrings.cs)
- Add new properties and language dictionary entries for the following localized keys:
  - `monitor_filters` ("Filters")
  - `monitor_filter_none` ("Filter: None")
  - `monitor_filter_lp` ("Filter: LP")
  - `monitor_filter_hp` ("Filter: HP")
  - `monitor_filter_bp` ("Filter: BP")
  - `monitor_filter_name_none` ("None")
  - `monitor_filter_name_lp` ("Lowpass (40Hz)")
  - `monitor_filter_name_hp` ("Highpass (0.5Hz)")
  - `monitor_filter_name_bp` ("Bandpass (0.5-40Hz)")
  - `monitor_pqrst` ("pQRSt")
  - `monitor_quality_excellent` ("Excellent")
  - `monitor_quality_acceptable` ("Acceptable")
  - `monitor_quality_barely_acceptable` ("Barely acceptable")
  - `monitor_quality_barely_acceptable_acceptable` ("Barely acceptable/Acceptable")
  - `monitor_quality_unacceptable` ("Unacceptable")

---

### Monitor Control Panel Component

#### [MODIFY] [MonitorControlPanel.xaml](file:///e:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.App/Controls/MonitorControlPanel.xaml)
- Add `x:Name="Heart3DText"` to the 3D button's TextBlock to set its text programmatically.
- Update 3D TextBlock styling: remove `FontWeight="Bold"` (so it uses `Normal` weight) and keep `FontSize="13"`.
- Update `PqrstText` styling: remove `FontWeight="Bold"`, change `FontSize` from `15` to `13`.
- Update `EosText` styling: remove `FontWeight="Bold"`, change `FontSize` from `20` to `13`, change `Foreground="Red"` to `Foreground="{StaticResource TextPrimaryBrush}"`.
- Add `PointerEntered="OnPqrstPointerEntered"` and `PointerExited="OnPqrstPointerExited"` to `PqrstButton` for hover consistency.

#### [MODIFY] [MonitorControlPanel.xaml.cs](file:///e:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.App/Controls/MonitorControlPanel.xaml.cs)
- Programmatically assign localized texts in `SetStaticLabels()`:
  - `FiltersTab.Text = AppStrings.MonitorFilters;`
  - `Heart3DText.Text = AppStrings.Monitor3D;`
  - `PqrstText.Text = AppStrings.MonitorPqrst;`
- Update dynamic text formatting in `UpdateTexts()` to use localized strings for active filters:
  - `"Filter: None"` -> `AppStrings.MonitorFilterNone`
  - `"Filter: LP"` -> `AppStrings.MonitorFilterLp`
  - `"Filter: HP"` -> `AppStrings.MonitorFilterHp`
  - `"Filter: BP"` -> `AppStrings.MonitorFilterBp`
- Update filters dropdown initialization in `OnFiltersClick()`:
  - Build menu header using `AppStrings.MonitorFilters` instead of `"Filters"`.
  - Add filter rows with `AppStrings.MonitorFilterNameNone`, `AppStrings.MonitorFilterNameLp`, `AppStrings.MonitorFilterNameHp`, and `AppStrings.MonitorFilterNameBp`.
- Implement `OnPqrstPointerEntered` and `OnPqrstPointerExited` to support hover visual feedback on the pQRSt toggle button.
- Localize the signal quality indicator labels inside `BuildSqiBadge()`.

---

## Verification Plan

### Automated Tests
- Build the project using `build.ps1` to ensure there are no compilation errors.

### Manual Verification
- Run the application (`build-and-run.ps1` or similar launcher) and open the Monitor.
- Verify that all buttons in the monitor control panel have matching typography (font size 13, normal style, consistent colors).
- Verify that all button text translates correctly when changing the application language.
- Verify that the pointer hover effects work consistently on the pQRSt button.
