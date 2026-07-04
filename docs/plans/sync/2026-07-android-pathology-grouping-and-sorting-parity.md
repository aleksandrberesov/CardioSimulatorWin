# Plan - Pathology Grouping and Sorting Android Parity

Document the requirements to implement grouping by duplicate titles, sorting by factor complexity, and expand/collapse all button controls on Android for UI parity with the Windows version.

## Objective
Update the rhythm choosing drawer/panel in the Android app to match the Windows logic:
1. Collapse duplicate/identical pathology names into nested, collapsible subgroups.
2. Sort pathologies inside clinical categories by complexity (number of factors in the title, fewer factors first).
3. Add "Expand All" and "Collapse All" controls to the panel header.
4. Auto-expand category groups and subgroups upon selecting a pathology.

---

## Technical Specifications

### 1. Collapsible Subgroups
Within each clinical group (e.g., `conduction`, `sinus`, `infarction`):
* **Trigger**: If a pathology display name (localized `nameRu` or `titleEn` based on the locale) appears **more than once** in the category list, it must be collapsed into a subgroup.
* **UI Element**: Render a collapsible subgroup header containing:
  * The subgroup display name.
  * A count of nested items in parentheses: `(Count)`.
  * An expand/collapse chevron icon representing its open/closed state.
* **Default Items**: Unique pathology names should remain direct children of the clinical category (do not wrap unique names in subgroups).
* **Indentation**: Subgroup list items must be indented (e.g., add a left margin or padding offset to their view holder layout) to clearly establish hierarchy.
* **Labeling**: To differentiate items within a subgroup:
  * If the entry has a case number (`number`), prefix it: `"{number} {title}"`.
  * If the entry does not have a number, append its unique ID in parentheses: `"{title} ({id})"`.

### 2. Complexity-Based Sorting
Inside each clinical group list (containing both subgroups and standalone items):
1. **Factor Count**: Count the number of factors in the pathology title by splitting it by `+` (e.g., `"Sinus rhythm + hypertrophy + ST"` has 3 factors).
2. **First Sort Key**: Order ascending by the factor count (fewer factors first).
3. **Second Sort Key**: Order alphabetically (case-insensitive) by the item's display name.

### 3. Expand All / Collapse All Header Controls
Add two icon buttons to the rhythms panel header using clear visual symbols (e.g. Double Chevron Down and Double Chevron Up):
* **Expand All**: Clears all collapsed categories and subgroups from the collapsed state sets, fully expanding the list.
* **Collapse All**: Adds all currently visible category and subgroup keys to the collapsed state sets (respecting active search query and clinical filters).

### 4. Selection Auto-Expansion
When a pathology is selected:
* Determine its parent clinical category and parent subgroup (if any).
* Automatically remove them from the collapsed tracking sets to expand them.
* Scroll the list to ensure the selected item is fully visible.
* Only trigger this behavior when the selected item ID changes, so manual collapses are not overridden.
