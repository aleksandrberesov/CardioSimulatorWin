# CardioSimulator UX/UI Schema & Platform Mapping

This document contains the comprehensive UX/UI schema extracted from the Android (Jetpack Compose) implementation and its mapping to Windows (WinUI 3 / Fluent Design).

## Phase 1: Source UX/UI Schema Extraction (Android)

### 1. Design Tokens

| Category | Token | Android Value (Compose) | Technical Description |
| :--- | :--- | :--- | :--- |
| **Colors (Dark)** | `Primary` | `#D0BCFF` | Material 3 Purple 80 |
| | `Secondary` | `#CCC2DC` | Material 3 PurpleGrey 80 |
| | `Tertiary` | `#EFB8C8` | Material 3 Pink 80 |
| **Colors (Light)** | `Primary` | `#6650A4` | Material 3 Purple 40 |
| | `Secondary` | `#625B71` | Material 3 PurpleGrey 40 |
| | `Tertiary` | `#7D5260` | Material 3 Pink 40 |
| **Surfaces** | `Panel-BG` | `#FFFFFF` | Solid White for top/bottom control panels |
| | `App-BG` | `#F5F5F5` | LightGray background for the monitor area |
| | `Disabled` | `LightGray (0.3α)` | Semi-transparent overlay for disabled tabs |
| **Typography** | `Body-L` | 16sp / 24sp LH | `bodyLarge`: Normal weight, 0.5sp letter spacing |
| | `Label-L` | 14sp | `labelLarge`: Used for primary tab text |
| | `Label-S` | 8sp | Custom small text used for sub-labels (e.g., "mm/s") |
| **Spacing** | `Grid-Unit` | 8dp | Base unit for padding and arrangement |
| | `Bar-Height` | 56dp | Standard height for Top and Bottom panels |
| **Rounding** | `Standard` | 4dp | Corner radius for Tabs and Labels |
| | `Drawer` | 8dp | Corner radius for the SideDrawer handle |

### 2. Component Hierarchy

#### 2.1 Atomic Components
*   **`Tab`**: The primary interactive element.
    *   *Structure*: `Box` + `RoundedCornerShape` (1dp border).
    *   *Variants*: Text-only, Icon-only, or Stacked (Text + Sub-text).
    *   *Logic*: Supports `isRepeatable` for continuous parameter adjustment.
*   **`Label`**: Non-interactive status display.
    *   *Examples*: HR (Black BG, White Text), EOS (Red Border).
*   **`ControlPanelDivider`**: 1dp vertical separator for grouping controls.
*   **`FilterChip`**: Used in `SignificantPointPanel` for toggling point types.

#### 2.2 Molecular Components (Panels)
*   **`TopControlPanel`**: 
    *   Weight: 2 (in `MainScreen` column).
    *   Components: Mode Selector (Dropdown), Contextual Tools (e.g., Start/Stop), Branding Logo.
*   **`BottomControlPanel`**:
    *   Weight: 2.
    *   Components: `MonitorControlPanel` or mode-specific editor tools (`ConstructorControlPanel`, `CourseConstructorControlPanel`), Settings Toggle.
*   **`MonitorControlPanel`**:
    *   Horizontal scrollable row of Tabs for: Count, Scheme, Speed, Scale, Comparison.
*   **`SignificantPointPanel`**:
    *   Width: 150dp.
    *   Vertical side panel in Constructor mode.
    *   Groups: P-Wave, QRS-Complex, T-Wave.
    *   Displays RR intervals if 2+ R-peaks are marked.
*   **`ImagePositionPanel`**:
    *   Overlay for reference image adjustment (Alpha, Lock, Reset).

#### 2.3 Organisms (Screens & Overlays)
*   **`Monitor`**: Central `LeadsGrid` that renders `LeadView` instances on a custom canvas.
*   **`Lead`**: Wraps `CalibrationPulse` and `PreviewPane`. Supports "Compare Mode" overlays.
*   **`EditableLead`**: Specialized canvas for drawing and marking points in Constructor mode.
*   **`SideDrawer`**: Sliding panel with a 24dp vertical handle.
    *   *RhythmSelector*: For choosing pathologies.
    *   *SignificantPointSelector*: For navigating marked points in a list.
*   **`CourseViewerOverlay`**: Full-screen modal containing a `WebView` for rich content.

### 3. Interaction Matrix

| Interaction | Trigger | Behavioral Logic |
| :--- | :--- | :--- |
| **Mode Switching** | Dropdown | Updates `AppViewModel.selectedOperatingMode`, which recomposes the entire `MainScreen` content and bottom panel. |
| **Parameter Adjust**| Tab Click | Triggers `MonitorViewModel` state updates (e.g., `setSpeed(25f)`). |
| **Repeatable Click** | Long Press | Used for fine-tuning numeric values in Constructor mode. |
| **Compare Mode** | Toggle Tab | Swaps monitor leads with interactive placeholders (`editingPaneIndex`). |
| **Drawer Toggle** | Handle Click | Uses `AnimatedVisibility` with `expandHorizontally` / `shrinkHorizontally`. |
| **Tool Mode Switch**| SegmentedBtn | Swaps between `Select` (gestures), `Trace` (drawing), and `Position` (moving image). |
| **Trace Auto-Fix** | Button Click | Triggers `TraceExtractor` to automatically detect waveform from reference image. |
| **Point Toggling** | FilterChip | Adds/Removes `SignificantPoint` at the `selectedIndex`. |

### 4. Detailed Component Specifications

#### 4.1 `Tab` (Generic Control)
*   **Properties**:
    *   `text: String?`: Primary label.
    *   `subText: String?`: Small auxiliary label (e.g., units).
    *   `icon: ImageVector?`: Vector icon (Play/Stop/Settings).
    *   `enabled: Boolean`: Toggles interactivity and visual alpha.
    *   `isRepeatable: Boolean`: If true, triggers `onClick` continuously while held.
    *   `backgroundColor: Color`: Custom background (used for active states).
*   **Behaviors**:
    *   *Ripple*: Standard Material ripple on press.
    *   *Auto-Resize*: Text scales down (min 10sp) to fit fixed dimensions.

#### 4.2 `MonitorControlPanel`
*   **State Management**:
    *   `availableSeriesCounts`: [1, 2, 3, 4, 6, 12].
    *   `availableSeriesSchemes`: [OneColumn, TwoColumn, Grid].
    *   `availableSpeeds`: [12.5, 25.0, 50.0, 100.0].
    *   `availableScales`: [0.25, 0.5, 1.0, 2.0, 4.0].
*   **Logic**: Each parameter opens a `DropdownMenu` on the associated `Tab`. Selection updates the `MonitorViewModel` and persists to `SharedPreferences`.

#### 4.3 `SideDrawer`
*   **Structure**:
    *   `drawerWidth`: 300dp (Fixed).
    *   `handlerWidth`: 24dp.
    *   `handlerHeight`: 64dp.
*   **Behavior**:
    *   Handle contains text rotated -90 degrees.
    *   Animation: 300ms slide-in duration.
    *   Z-Index: Floats above the Monitor area but below the Top/Bottom panels.
    *   Multiple Drawers: Can stack (e.g., Rhythm Selector and Points Selector offsets).

#### 4.4 `LeadView` / `ChartCanvas` (ECG Rendering)
*   **Properties**:
    *   `points: Points`: List of Float values representing the waveform.
    *   `isRunning: Boolean`: Toggles the sweep-line animation.
    *   `xOffsetPx: Float`: Current position of the sweep line.
    *   `gridScheme: GridScheme`: Determines background grid density.
*   **Rendering Logic**:
    *   Draws a background grid (red/pink tones).
    *   Renders the waveform segment-by-segment using `projectPath` (ADC to Px mapping).
    *   Erases a small "buffer gap" ahead of the sweep line to show the new data.

### 5. Information Architecture & State

*   **Operating Modes**:
    *   `Teaching`: Guided learning with `CourseViewer`.
    *   `Testing / Examination / OSKE`: Performance evaluation modes.
    *   `Constructor`: ECG pathology editor with reference image tracing.
    *   `CourseConstructor`: Curriculum builder.
*   **Root Structure**: `Column` [ TopPanel (2f) -> Content (15f) -> BottomPanel (2f) ].
*   **Navigation**: Modal-based mode selection via Top Panel.
*   **State Containers**:
    *   `AppViewModel`: Global app state (Language, Data Ready).
    *   `MonitorViewModel`: Hardware/Monitor settings (Speed, Scale).
    *   `ConstructorViewModel`: Editor state (Pathology being edited, Undo/Redo stack, Image transforms).

---

## Phase 2: Platform Mapping & Friction Analysis

### 1. Potential Friction Points

*   **Side Drawers with Handles**:
    *   *Android*: Standard mobile pattern for hiding list selectors.
    *   *Windows*: Use `NavigationView` or `SplitView`. The "Handle" should be replaced with a standard Toggle button (Hamburger) or a more desktop-friendly hover-trigger.
*   **Split Controls (Top & Bottom)**:
    *   *Android*: Efficient for thumb reachability.
    *   *Windows*: Desktop users expect a unified Toolbar. 
    *   *Resolution*: Maintain the Top/Bottom split for 1:1 parity but ensure the bottom panel is docked and visually consistent with the taskbar.
*   **Dropdown Mode Selector**:
    *   *Windows*: A persistent `NavigationView` on the left is better for visibility. 
    *   *Resolution*: To maintain parity, use a `ComboBox` or `MenuFlyout` styled to look like the Android Tab.
*   **Gestures (Pinch-to-zoom for images)**:
    *   *Android*: Native support for multi-touch.
    *   *Windows*: Must map to `Ctrl + Mouse Wheel` for scaling and `Middle Mouse Button` or `Space + Drag` for panning.

### 2. Desktop Adaptations (Windows)

*   **Hover States**: 
    *   Every `Tab` and `List` item MUST have a `PointerOver` visual state.
*   **Keyboard Shortcuts**:
    *   `Space`: Play/Stop.
    *   `Ctrl+1-6`: Switch Operating Modes.
    *   `Esc`: Close Drawers/Overlays.
    *   `Ctrl+Z / Ctrl+Y`: Undo/Redo in Constructor.
    *   `Del`: Delete selected point or pathology.
*   **Input Precision**: 
    *   Reduce hit target sizes slightly for mouse precision, but maintain the 8dp spacing grid for visual consistency.

---

## Phase 3: Target Implementation (Windows Blueprint)

### 1. XAML Style Mapping
*   **`TabStyle`**: A `Button` template with `CornerRadius="4"` and a `BorderThickness="1"`. Use `VisualStateManager` to handle Hover/Pressed/Disabled states.
*   **`MonitorGrid`**: A `Grid` control where `RowDefinitions` and `ColumnDefinitions` are dynamically updated by the `MonitorViewModel`.

### 2. Behavioral Parity
*   **ECG Rendering**: Use a `Canvas` control in WinUI 3 with `Composition` or `Win2D` to match the performance and smoothness of Android's `DrawScope`.
*   **Smooth Animations**: Use `StandardUICommand` and `ConnectedAnimationService` for transitions between pathology selections and mode switches.
