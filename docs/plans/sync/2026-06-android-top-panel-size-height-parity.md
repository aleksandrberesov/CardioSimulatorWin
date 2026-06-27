# Plan: Sync the top-panel pill size + compact bar height to Android

**Created:** 2026-06-26
**Status:** NOT STARTED
**Direction:** **Windows → Android** (reverse of the usual). Two top-bar presentation tweaks were
made in the WinUI 3 port first, refining the new green-theme top panel against the customer's
reference mockup. Android should apply the same two changes so the top bar reads identically on
both platforms.

**Target (Android) source root:** `C:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`

> Scope note: this is a **sizing/layout tweak**, not a restructure. No controls added/removed/
> renamed; the mode pill, the course/lecture (or rhythm) breadcrumb pills, and the logo all stay.
> Only the **top-bar** selector pills grow; the dense monitor/bottom-panel pills are left untouched.

---

## Background — the two changes

The top bar holds, left-to-right: the **mode selector pill**, the **breadcrumb selector pills**
(Teaching: course + lecture/rhythm), a flexible spacer, and the **app logo** on the right. Two
things changed:

1. **Pill proportion + font** — the top-bar selector pills were too small/cramped next to the
   reference mockup. They now use a **larger size variant**: roomier padding and a slightly larger
   label + chevron, so each pill reads as a proper rounded-rect dropdown (the fixed 8 px corner no
   longer looks like a capsule on a too-short pill). This applies **only** to the top-bar
   selectors, not the densely-packed monitor/bottom-panel pills.

2. **Compact bar height** — the top bar previously took a **fixed proportional share** of the
   window height (`2*` of a `2 / 15 / 2` row split), so on a large window it ballooned to ~100 px
   with the logo and pills floating in a sea of whitespace. It now **hugs its content** (height
   driven by the logo + pills, ~44 px) and the bar's **vertical padding was removed**, collapsing it
   to a compact strip.

Neither change touches the monitor card, the bottom bar, or any pill **colors/labels** — they are
size/layout only.

---

## Reference: exact Windows changes to mirror

### Change 1 — larger top-bar pill size variant

A new `Large` flag was added to the shared `Tab` control. It swaps the resting **padding** and
**font sizes**; the dense default (used everywhere else) is unchanged.

| Windows file | Change |
|---|---|
| `CardioSimulator.App/Controls/Tab.xaml.cs` | New `Large` dependency property + `ApplySizing()`. Padding **`9,3` → `14,7`**, label font **13 → 14**, chevron font **9 → 10**, sub-text **9 → 10**, icon **16 → 17**. Dense values stay the default. |
| `CardioSimulator.App/Controls/TopControlPanel.xaml` | Mode pill `ModeTab` gains `Large="True"`. |
| `CardioSimulator.App/Controls/TeachingControlPanel.cs` | Course pill + lecture/rhythm pill (`_courseTab`, `_itemTab`) set `Large = true`. |

```csharp
// Tab.xaml.cs — the two metric sets (px at the reference scale)
private static readonly Thickness DensePadding = new(9, 3, 9, 3);   // monitor/bottom pills (unchanged)
private static readonly Thickness LargePadding = new(14, 7, 14, 7); // top-bar selector pills

private void ApplySizing()
{
    RootBorder.Padding   = Large ? LargePadding : DensePadding;
    TextView.FontSize    = Large ? 14 : 13;   // label
    SubTextView.FontSize = Large ? 10 : 9;    // stacked sub-label
    ChevronView.FontSize = Large ? 10 : 9;    // trailing ⌄
    IconView.FontSize    = Large ? 17 : 16;   // glyph-only tabs
}
```

Resulting top-bar pill is ~**35 px** tall (was ~25 px). Corner radius stays **8 px** — it just
reads as a rounded-rect now that the pill is taller.

### Change 2 — top bar hugs its content (compact height, no vertical padding)

| Windows file | Change |
|---|---|
| `CardioSimulator.App/Screens/MainScreen.xaml` | Top row height **`2*` → `Auto`** (size to content) in the `2 / 15 / 2` row split — so the bar is the logo + pills tall, not a proportional slab. Top-bar `Border` padding **`8,4` → `8,0`** (drop the vertical padding). The freed space goes to the middle monitor card (`15*`). Bottom bar left as `2*`. |

```
Before:  RowDefinitions = 2* / 15* / 2*      top Border Padding = 8,4   → bar ≈ 100 px (mostly whitespace)
After:   RowDefinitions = Auto / 15* / 2*    top Border Padding = 8,0   → bar ≈ 44 px (logo height)
```

> **Why Auto, not a smaller star:** the bar's floor is the **logo height (44 px)**; the pills
> (~35 px) sit comfortably inside it. Content-sizing makes the strip exactly tall enough for both
> and stops it scaling up with the window. The logo height itself was **not** changed.

---

## Steps (Android)

### 1. Add a "large" size variant to the shared `Tab` composable
Find the Android analog of `Controls/Tab.xaml(.cs)` — the shared pill/button composable that takes
`isActive` / `showChevron` (search: `Tab(`, `showChevron`, `isActive`, the chevron `⌄` / dropdown
glyph). Add an optional size parameter (e.g. `large: Boolean = false`, or a small `TabSize` enum)
that selects:

- **content padding** — dense `~9.dp horizontal / 3.dp vertical` (default, keep current) vs large
  `~14.dp / 7.dp`;
- **label font** — `13.sp` vs `14.sp`;
- **chevron** — `9.sp` vs `10.sp` (and sub-label `9 → 10.sp`, glyph icon `16 → 17.sp` if those exist).

Express the px reference numbers as **dp/sp** the way the rest of the Android control metrics are.
Default everything to the dense values so **no other caller changes**.

### 2. Apply the large variant to the top-bar selectors only
In the Android `TopControlPanel` and `TeachingControlPanel` analogs:
- the **mode selector** pill → `large = true`;
- the Teaching **course** pill and the **lecture/rhythm** pill → `large = true`.

Do **not** touch the monitor/bottom-panel pills (lead count, scheme, sweep speed, scale, Artifacts,
etc.) — they stay dense so the packed bottom row doesn't overflow.

### 3. Make the top bar hug its content and drop its vertical padding
Find the Android top-bar layout (analog of the top row in `Screens/MainScreen.xaml` — the scaffold
that stacks top bar / monitor content / bottom bar). On Compose this is typically a `Column` with
weighted children or a `Scaffold` topBar.
- The top bar should **wrap its content height** (`Modifier.wrapContentHeight()` / `height(IntrinsicSize.Min)` /
  no `weight`) instead of taking a fixed `weight(...)` share — its height should be driven by the
  **logo + pills** (the logo is the tallest, ~44 dp).
- Remove the bar's **vertical padding** (Android analog of the `8,4 → 8,0` change — keep the
  horizontal inset, zero the top/bottom). Keep the logo height as-is.
- Give the reclaimed vertical space to the **monitor/content** area (the middle region grows).

If Android uses a fixed `dp` height or a `weight` for the top bar, replace it with content-sizing;
if it already wraps content, just remove the extra vertical padding.

---

## Verification

On Android, open Teaching on a large/maximized window and confirm:

| Check | Expected |
|---|---|
| Top-bar pills | Mode + course + lecture/rhythm pills are noticeably **larger** (more padding, ~14 sp label) and read as rounded-rect dropdowns, not tiny capsules |
| Bar height | Top bar is a **compact strip** (~logo height), not a tall slab with whitespace above/below the logo |
| Bar padding | No empty vertical padding band above the pills/logo |
| Logo + pills fit | Both the logo and all selector pills sit inside the strip, vertically centered, not clipped |
| Monitor/bottom pills | Unchanged size (lead count / scheme / speed / scale / Artifacts still dense; bottom row does **not** overflow) |
| Monitor area | Slightly taller (it absorbed the freed top-bar space) |

Run the Android test suite; nothing data/logic-related should change (presentation only).

---

## Acceptance checklist
- [ ] Shared `Tab` composable gains a **size variant** (large vs dense) controlling padding + label/chevron font; dense stays the default.
- [ ] **Mode pill** and the **course + lecture/rhythm** breadcrumb pills use the **large** variant.
- [ ] Monitor/bottom-panel pills **unchanged** (dense); bottom row does not overflow.
- [ ] Top bar **hugs its content** (no fixed proportional/`dp` height) and its **vertical padding is removed**.
- [ ] Logo height unchanged; logo + pills fit the compact strip, vertically centered.
- [ ] No change to pill colors/labels, the monitor card, the bottom bar, or any data/logic.

---

> Companion Windows changes live in `Controls/Tab.xaml.cs` (the `Large` variant),
> `Controls/TopControlPanel.xaml`, `Controls/TeachingControlPanel.cs`, and `Screens/MainScreen.xaml`.
> Related sync plan: `2026-06-android-green-theme-parity.md` (the green theme that introduced these
> top-bar pills in the first place).
