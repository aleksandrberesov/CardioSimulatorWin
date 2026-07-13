# Plan: Port Rhythm List Selection Scrolling Behavior to Android

**Created:** 2026-07-13  
**Status:** COMPLETE (NO-OP / ALREADY IN PARITY)  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows version (`CardioSimulatorWin`), selecting a rhythm, collapsing/expanding headers, or performing filtering/search caused the list scroll position to jump unexpectedly. The tester reported that the list window "jumps" and scroll position goes to the top or bottom during selection, and clicking on items or headers reset the scroll offset back to the start (top).

This happened because:
1. The `Rebuild()` method of `RhythmChoosingPanel` was calling `ScrollToSelected()` on every rebuild, which in turn performed `List.ScrollIntoView(match, ScrollIntoViewAlignment.Leading)`. Because `Rebuild()` updates `ItemsSource`, the item containers were not immediately realized, causing the visibility check `List.ContainerFromItem(match)` to return `null` and fallback to scrolling the list, forcing the item to the leading edge of the viewport.
2. Reassigning `List.ItemsSource = rows` in `Rebuild()` causes the WinUI `ListView` scroll offset to reset to `0` (the top of the list). When `ScrollToSelected()` was removed to prevent the jump to the leading edge, user clicks on items/headers caused the list scroll position to reset to `0`.

The fix on Windows was to:
1. Change the signature of `Rebuild` to `Rebuild(bool preserveScroll = false)`.
2. Inside `Rebuild()`, fetch the list's `ScrollViewer.VerticalOffset` before reassigning `ItemsSource`.
3. If `preserveScroll` is `true`, enqueue a call to `ScrollViewer.ChangeView` with the saved offset at low dispatcher priority (running after layout is completed) to instantly restore the scroll position.
4. Call `Rebuild(preserveScroll: true)` for direct user interactions (clicks on items, headers, or subgroup headers inside `OnItemClick`).
5. Only invoke `ScrollToSelected()` on programmatic selection changes (inside the `SelectedId` property setter) and during initial loading (inside the `SetRhythms()` method).

The goal of this plan is to verify that the Android implementation is aligned and does not exhibit similar scrolling jumps or resets.

---

## 2. Part A: Android RhythmSelector Implementation

Reviewing `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\ui\panels\RhythmSelector.kt`, we can observe the layout details:

```kotlin
LazyColumn(
    modifier = Modifier.fillMaxSize(),
    state = listState
) {
    listItems.forEach { line ->
        when (line) {
            is RhythmListLine.GroupHeader -> {
                stickyHeader(key = "group_${line.key}") { ... }
            }
            is RhythmListLine.SubgroupHeader -> {
                item(key = "subgroup_${line.key}") { ... }
            }
            is RhythmListLine.RhythmItem -> {
                item(key = "item_${line.entry.id}") { ... }
            }
        }
    }
}
```

### Analysis:
- Android uses Compose with a `LazyColumn` driven by `listItems` state.
- Stable keys are assigned for each group header (`group_${line.key}`), subgroup header (`subgroup_${line.key}`), and rhythm item (`item_${line.entry.id}`).
- When items are clicked, selection state changes, or headers are collapsed/expanded, Jetpack Compose updates the UI through recomposition. Because of the stable keys and because `LazyColumn` preserves scroll state via `rememberLazyListState()`, **Compose automatically maintains the vertical scroll position** without resetting it to the top.
- Clicking an item or collapsing a header does not trigger any programmatic scroll action or reset the viewport.
- Therefore, Android is already in parity with the corrected behavior on Windows (it naturally preserves scroll offset on click and does not perform unwanted jumps). No code changes are required on Android.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
To confirm there are no scroll jumps or resets on Android:
1. Open the Android application on an emulator or physical device.
2. Navigate to **Teaching Mode** and open the left rhythm drawer.
3. Scroll down the rhythm list.
4. Click on any visible rhythm item.
   - **Verification:** The clicked item is highlighted (text changes to red), but the scroll position of the list does not jump (the item stays exactly under the cursor/finger).
5. Click on any group header to collapse or expand it.
   - **Verification:** The header toggles its state, but the scroll position is maintained (it does not scroll to start/top).
6. Type text into the search bar.
   - **Verification:** The list is filtered dynamically, and the scroll position does not jump to the top or selected item unexpectedly.
