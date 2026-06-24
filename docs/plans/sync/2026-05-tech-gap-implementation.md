# Plan: Tech Gap Implementation (Android to Windows Parity)

## Background & Motivation
The Windows version of CardioSimulator has fallen behind the Android version in several key feature areas introduced in late May 2026. To achieve full 1:1 parity, we need to port over three major feature epics:
1. **New Requirements 250526**: Editor derived lead calculations, Comparative view (Compare button), "Blank Sheet" mode, Editor playback controls, and general UI polish (including renaming to "ECG Constructor").
2. **Course Constructor**: A full educational authoring suite allowing users to create Markdown/KaTeX/ECG-embedded lectures and bundle them.
3. **ECG Photo Tracing**: Tools to overlay a reference ECG photo and manually trace it into editable samples.

## Scope & Impact
This is a cross-cutting implementation affecting almost every layer of the application:
- **Core (Data & Domain)**: New parsing and storage pipelines for Courses (ZIP bundles, Markdown parsing), new state models for Photo Tracing (transforms, opacities), and new domain models for Comparative View.
- **ViewModels**: Introduction of `CourseConstructorViewModel`, updates to `EditorViewModel` for tracing and lead calculations, and comparative state management.
- **UI & Rendering**: Win2D enhancements for rendering Markdown (or equivalent rich text), rendering photo underlays, rendering comparative grids, and new screens/dialogs for the Course Constructor.

## Proposed Solution: "Foundational First" Strategy
Based on user preference, we will adopt a **Foundational First** approach. We will implement the core domain models, parsers, and data repositories for *all* new features before moving on to the ViewModels and finally the UI layers. This ensures a stable, unified data layer before UI integration begins.

## Phased Implementation Plan

### Phase 1: Foundation (Domain & Data Layers)
*Objective: Build the base data structures and parsers without any UI dependencies.*
1. **Course Domain & Repository**:
   - Create `Course.cs`, `Lecture.cs`, and `CourseBlock` classes (Markdown, Formula, Image, EcgEmbed, EditableTable) in `CardioSimulator.Core/Domain`.
   - Implement `CourseParser.cs` for manifest and front-matter parsing.
   - Implement `CourseRepository.cs`, `CourseZipExtractor.cs`, and file source handlers in `CardioSimulator.Core/Data`.
2. **Photo Tracing State**:
   - Add tracing transform states (Offset, Scale, Rotation, Alpha) and ToolModes (Position, Trace, Select) to the core domain or shared state layer.
3. **Comparative View Models**:
   - Create data structures to hold multiple selected `LeadStream` references for the Comparative mode.
4. **New Requirements Data**:
   - Extend `DerivedLeads.cs` (if needed) for automated baseline subtraction/addition.
   - Update `DataSourcePrefs.cs` and localization structures for the new "ECG Constructor" branding.

### Phase 2: Application Logic (ViewModels)
*Objective: Wire the data layer to state management.*
1. **EditorViewModel Updates**:
   - Add `CalculateDerivedLeads()` logic.
   - Add Photo Tracing batch writing logic (e.g., `SetSampleRange`) and an undo/redo stack for traces.
   - Link playback controls to `MonitorViewModel`.
2. **Course ViewModels**:
   - Implement `CourseConstructorViewModel.cs` mirroring the Android logic (managing selected courses, markdown text, dirty states, and atomic saving).
   - Implement `CourseViewerViewModel.cs` for the Teaching Screen.
3. **Comparative View Logic**:
   - Add state management for selecting multiple rhythms and coordinating their display in the same lead.

### Phase 3: User Interface & Rendering
*Objective: Build the visual layer.*
1. **Course Constructor UI**:
   - Create `CourseConstructorScreen.xaml` with split view (Markdown input vs. rendered preview).
   - Implement a Markdown renderer in WinUI 3 (leveraging existing controls or WebView2 for KaTeX/HTML blocks).
2. **Photo Tracing UI**:
   - Update `EcgRenderer.cs` and `EditableLeadControl.cs` to support an image underlay with pan/zoom/rotate transformations.
   - Implement freehand tracing pointer logic in the Editor.
3. **New Requirements UI**:
   - Add the "Compare" button to `BottomControlPanel.xaml` and implement `ComparisonPresetsDialog` and `ComparisonTargetDialog`.
   - Add "Blank Sheet" toggle in Settings and update the grid renderer.
   - Update MainScreen and Toolbar with the new branding and Editor Start/Stop controls.

## Verification & Testing
- **Phase 1**: Unit tests for `CourseParser` and `CourseRepository` ensuring proper bundle extraction and parsing. Test derived lead calculations.
- **Phase 2**: ViewModel unit tests for state transitions and undo/redo logic.
- **Phase 3**: Visual validation of the WinUI 3 application. Verify that the image underlay correctly aligns with grid coordinates, that the Course Constructor parses Markdown accurately, and that the Comparative View successfully overlays multiple rhythms.

## Migration & Rollback
- No existing user data will be migrated; these are greenfield features.
- If rendering performance issues arise (e.g., WebView2 overhead for KaTeX), we will fallback to simplified text rendering or pure C# MathML renderers.
