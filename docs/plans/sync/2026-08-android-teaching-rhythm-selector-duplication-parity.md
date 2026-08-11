# Plan: Remove Duplicate Rhythm Selector in Teaching "All Rhythms" (Top Tab vs. Side Drawer)

**Created:** 2026-08-11
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

---

## 1. Background & Goals

**Bug (QA):** *"Дублирование выбора ритма. Открыть экран выбора. Ожидаемый результат: Нет
дублирующих элементов. Фактический результат: Функция доступна сверху и сбоку."*
(Duplicate rhythm selection — the choose‑rhythm function is offered both at the **top** and on the
**side**.)

In **Teaching mode → "All rhythms"** the monitor is the standalone main view, and the user can pick a
rhythm from **two** places:

- **Top (сверху)** — `TeachingControlPanel`'s item tab, in the `isAllRhythms` branch, is a rhythm
  picker: tapping it opens a `RhythmSelector` (Android: in a `Dialog`; Windows: in a `Flyout`).
- **Side (сбоку)** — the left `SideDrawer` hosting `RhythmSelector` (Android `MonitorOverlay` /
  Windows `MonitorViewerOverlay`).

The functional specification (`Docs/functional-specification.md` §2.1 **"Rhythm Drawer (Left)"**)
defines the **left drawer as the canonical rhythm selector**. The top‑bar rhythm picker is a non‑spec
duplicate and must be removed.

**Important constraint — keep the name display.** In the standalone "All rhythms" view the monitor
overlay deliberately has **no title bar** (Windows `MonitorViewerOverlay.SetCloseButtonVisible(false)`;
Android `MonitorOverlay` with `onClose == null`), on the assumption that *the selected rhythm's name is
already shown by the top mode bar's item tab*. So the top item tab must remain as a **read‑only title
that displays the current rhythm's name** — only its **selection function** (the dropdown/dialog) is
removed. Rhythm selection then lives **solely** in the left drawer.

### What was done on Windows (reference implementation)

File: `src/CardioSimulator.App/Controls/TeachingControlPanel.cs`

- `UpdateSelectors()` now makes the item tab non‑interactive in All‑rhythms mode:
  ```csharp
  _itemTab.ShowChevron   = !IsAllRhythms;   // no dropdown chevron → reads as a plain title
  _itemTab.IsHitTestVisible = !IsAllRhythms; // no hover/click → cannot open a picker
  ```
  (`UpdateItemLabel()` still sets `_itemTab.Text` to the selected rhythm's localized name, so the top
  bar keeps showing the name.)
- `OnItemClick()` early‑returns when `IsAllRhythms` (only the subtopic/lecture selectors remain live).
- The private `ShowRhythmFlyout()` method (which built the duplicate `RhythmChoosingPanel` flyout) was
  **deleted**.
- In course modes the item tab is unchanged — a fully interactive subtopic/lecture selector.

The side `RhythmChoosingDrawer` in `MonitorViewerOverlay` is **left exactly as is** — it is the single,
canonical selector.

---

## 2. Android Porting Steps

**File:** `app/src/main/java/com/example/cardiosimulator/ui/panels/TeachingControlPanel.kt`

The change is confined to the `if (isAllRhythms) { … }` branch (currently ~lines 97–137).

**Current (to be changed):**
```kotlin
if (isAllRhythms) {
    val rhythms by rhythmViewModel.rhythms.collectAsState()
    val selectedRhythm by rhythmViewModel.selectedRhythm.collectAsState()
    var rhythmExpanded by remember { mutableStateOf(false) }
    val rhythmLabel = selectedRhythm?.let { if (currentLanguage == Language.RU) it.nameRu ?: it.titleEn else it.titleEn }
        ?: stringResource(R.string.rhythm_selector_title)

    Box {
        Tab(
            text = rhythmLabel,
            showChevron = true,
            isLarge = true,
            onClick = { if (rhythms.isNotEmpty()) rhythmExpanded = true },
            modifier = Modifier.padding(horizontal = 4.dp).wrapContentWidth()
        )
        if (rhythmExpanded) {
            androidx.compose.ui.window.Dialog(onDismissRequest = { rhythmExpanded = false }) {
                androidx.compose.material3.Surface(/* … */) {
                    RhythmSelector(
                        appViewModel = appViewModel,
                        rhythms = rhythms,
                        selectedId = selectedRhythm?.id,
                        showPinButton = false,
                        onRhythmSelect = { rhythmViewModel.selectRhythm(it.id); rhythmExpanded = false }
                    )
                }
            }
        }
    }
}
```

**Target — read‑only name title, no picker:**
```kotlin
if (isAllRhythms) {
    val selectedRhythm by rhythmViewModel.selectedRhythm.collectAsState()
    val rhythmLabel = selectedRhythm?.let { if (currentLanguage == Language.RU) it.nameRu ?: it.titleEn else it.titleEn }
        ?: stringResource(R.string.rhythm_selector_title)

    // Rhythm SELECTION belongs solely to the left rhythm drawer (functional spec §2.1). The top bar
    // only DISPLAYS the current rhythm's name here (the standalone monitor view has no title bar and
    // relies on this). A second picker made the choose-rhythm function appear both "сверху" and
    // "сбоку" — the reported duplication.
    Text(
        text = rhythmLabel,
        style = MaterialTheme.typography.titleMedium, // match the large Tab label size/weight
        color = MaterialTheme.colorScheme.onSurface,
        maxLines = 1,
        modifier = Modifier.padding(horizontal = 14.dp) // ~= isLarge Tab horizontal padding
    )
}
```

### Porting notes / gotchas

1. **Do NOT use `Tab(enabled = false)`.** In `ui/components/Tab.kt`, `enabled = false` renders a
   greyed‑out *disabled* look (`Color.LightGray` fill, `Color.Gray` text). That misrepresents a normal
   title. Windows keeps the label in the **normal** foreground color with **no** border/hover — so the
   faithful port is a plain `Text` (as above), not a disabled `Tab`. (A `Tab(showChevron = false,
   onClick = {})` is also wrong: `repeatingClickable` still ripples on tap, implying interactivity.)
2. **Match the Tab's large typography** so the title's size/weight/vertical rhythm are unchanged next
   to the still‑interactive course tab. Pick the `MaterialTheme.typography` style the large `Tab` uses
   for its text (verify against `Tab.kt`); adjust horizontal padding to match `isLarge` (14.dp).
3. **Remove now‑dead code in this branch:** the `rhythmExpanded` state, the `Dialog` + `Surface`
   picker block, the `onClick`, and the `rhythms` collectAsState (only `selectedRhythm` is still needed
   for the label — confirm `rhythms` isn't used elsewhere in the composable before deleting).
4. **Unused imports:** after removal, check whether `RhythmSelector` (and, if the lecture branch does
   not use them, `Dialog`/`Surface`/`RoundedCornerShape`) become unused and delete the dead imports.
   The `else` (course) branch still uses `Dialog` + `Surface` + `LectureSelector`, so those imports
   likely stay; `RhythmSelector` most likely becomes unused here — verify and remove.
5. **Leave the side drawer untouched.** `TeachingScreen.kt` → `MonitorOverlay` → `SideDrawer` +
   `RhythmSelector` is the canonical selector and must keep working (collapsed handle, pinned/fixed
   drawer, `showScrollButtons = true`).
6. **Course modes unchanged.** Only the `isAllRhythms` branch changes; the flat‑lecture and
   subtopic/lecture selectors (the `else` branch) must remain fully interactive.

---

## 3. Verification

### 3.1 Manual verification flow
1. Launch Teaching mode (defaults to **"All rhythms"** → the monitor).
2. **Top bar:** confirm the item tab shows the current rhythm's name **with no chevron** and does
   **not** open any picker/dialog when tapped.
3. **Side drawer:** open the left "Rhythms" drawer and pick a different rhythm → the monitor updates
   **and** the top‑bar title updates to the newly selected name.
4. Confirm there is now exactly **one** way to choose a rhythm (the side drawer) — no duplicate.
5. Switch the course selector to a real **course**: confirm the item tab is again a working
   subtopic/lecture selector (chevron + dropdown), unaffected by this change.
6. Pinned ("Fixed") drawer: confirm rhythm selection still works and the top title still tracks it.
7. RU/EN: confirm the top title shows the correct localized name in both languages.

### 3.2 Regression checks
- Standalone all‑rhythms monitor still has no title bar and still shows the rhythm name (now via the
  read‑only top title).
- Compare mode and the graduation‑cap rhythm‑info screen are unaffected.

---

## 4. Affected Files (Android)

| File | Change |
| --- | --- |
| `ui/panels/TeachingControlPanel.kt` | `isAllRhythms` branch: replace the rhythm `Tab` + `Dialog`/`RhythmSelector` picker with a read‑only `Text` name title; drop `rhythmExpanded`, the picker, and dead imports. |

_No changes to `TeachingScreen.kt`, `SideDrawer.kt`, or `RhythmSelector.kt`._
