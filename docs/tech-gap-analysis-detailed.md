# CardioSimulator: Comprehensive Tech Gap Analysis (Android vs. Windows)

This document provides a deep-dive analysis of the features, architectural solutions, and UX refinements implemented in the reference Android version (as identified through git history and source analysis) that are currently missing or under-implemented in the Windows port.

## 1. Advanced Waveform Editing (Constructor Mode)

The Android version implements a sophisticated mathematical approach to ECG waveform editing that is not yet fully mirrored in the Windows version.

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Smoothing Algorithms** | Implements `EditingAlgorithm` with support for: **Cosine, Spline, Bezier, LOESS (Locally Estimated Scatterplot Smoothing), and MLS (Moving Least Squares)** kernels. | Basic linear or simple smoothing only. |
| **Sub-integer Precision** | Uses `floatBuffers` to accumulate weighted changes, preventing rounding errors/artifacts during repeated "nudge" adjustments. | Direct integer ADC sample manipulation (prone to artifacts). |
| **Influence Radius** | Configurable `editingRadius` that applies changes naturally across a range of samples rather than single-point spikes. | Limited influence logic. |
| **Interactive Tool Modes** | Triple-mode interaction: `Select` (navigation), `Trace` (freehand/photo), `Position` (reference alignment). | Partially implemented; lacks dedicated `ToolMode` orchestration. |

## 2. ECG Digitization Pipeline (Photo Tracing)

A core USP of the Android version is the ability to digitize physical ECG strips from photos, which is significantly more advanced than the Windows implementation.

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Trace Extraction** | `TraceExtractor` utility uses luminance detection to automatically map photo pixels to ADC samples. | Missing automatic extraction logic. |
| **Ghost Trace Preview** | A "Ghost Trace" mechanism allows users to preview and refine auto-detected waveforms before commit. | Missing preview/refinement stage. |
| **Freehand Tracing** | `TraceOverlay` captures continuous gestures and uses linear interpolation for seamless manual digitization. | Basic point-by-point tracing only. |
| **Interactive Alignment** | Support for `transformable` gestures (scale, rotate, translate) specifically for aligning reference photos. | Basic scale/rotation sliders (less intuitive for touch/mouse). |

## 3. Course Constructor & Educational Content

The Android version's educational toolset is significantly more modular and user-friendly for content creators.

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Block-Based Editor** | A visual `HtmlBlockEditor` allowing creators to manage distinct blocks (H1, H2, Paragraph, KaTeX, ECG, Image, Table) rather than raw HTML. | Primarily a raw HTML editor with basic visual wrapping. |
| **Sync Scrolling** | Bi-directional scroll synchronization between the block editor and the `LectureWebView` preview using `scrollToBlockId`. | Preview updates on debounce, but no scroll synchronization. |
| **Math Rendering** | Integrated KaTeX v0.17.0 with expanded math capabilities (matrices, complex symbols). | Basic KaTeX support. |
| **ECG Reference Linking** | Specialized `HtmlBlock.Ecg` for semantic linking of pathologies directly within the lecture flow. | Manual HTML tag insertion. |

## 4. Monitoring & Comparison (Teaching Mode)

The comparison logic in Android is more robust and tailored for medical analysis.

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Interactive Placeholders** | Explicit "Click to choose" overlays for unselected comparison slots, guiding the student UX. | Basic monitor view without guided placeholders. |
| **Locked Reference Leads** | Automatic locking of Lead I and Lead II for the primary pathology to serve as a baseline for comparison. | Manual lead selection for all slots. |
| **Persistent Presets** | JSON-serialized comparison schemas stored in DataStore, allowing complex multi-pathology layouts to be reused. | Basic preset support, less robust serialization. |
| **Global Rhythm Filter** | Implementation of a course-aware rhythm filter that hides irrelevant pathologies during specific lectures. | Flat rhythm list always visible. |

## 5. UI/UX & Technical Refinements

Small but critical details that improve the "desktop-grade" feel of the mobile app.

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Auto-Resize Text** | `AutoResizeText` component ensures labels remain legible by scaling down to a minimum of 10sp. | Standard truncation or overflow. |
| **Synchronized Scrolling** | Grid and waveform scrolling are perfectly synchronized across all monitor components. | Potential drift during high-speed rendering. |
| **Repeatable Interactions** | `RepeatingClickable` modifier allows fine-tuning parameters (like speed/scale) by holding buttons down. | Single-click interaction only. |
| **Sweep Mode** | Special "Blank" grid scheme with "Sweep" animation logic for specific diagnostic views. | Standard grid-only rendering. |

## 6. Infrastructure & Localization

| Feature | Android Implementation Details | Windows Status |
| :--- | :--- | :--- |
| **Multi-Part Waveforms** | Support for `LeadStream` segments, allowing complex non-continuous rhythms to be edited. | Continuous buffer assumption. |
| **Encoding Robustness** | Automated detection of Cyrillic/UTF encodings for legacy pathology data. | Potential encoding issues with Russian labels. |
| **Globalized Markers** | Significant points (P, QRS, T) are globalized across the domain layer for automated R-R analysis. | Localized UI markers only. |

## Summary of Actionable High-Priority Gaps

1.  **Implement `TraceExtractor` and `GhostTrace`**: Essential for digitizing physical ECG data.
2.  **Upgrade to Block-Based Course Editor**: Move away from raw HTML for a more user-friendly experience.
3.  **Port Advanced Smoothing Kernels (MLS/LOESS)**: Improve the visual quality of the ECG Constructor.
4.  **Implement Bi-directional Scroll Sync**: Crucial for a polished Course Constructor UX.
5.  **Refine Comparison Mode**: Add interactive placeholders and locked reference leads.
