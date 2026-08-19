# Plan: Port Pathology Russian Translation Taxonomy Fallback to Android

**Created:** 2026-08-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When an ECG pathology (rhythm) dataset lacks an authored Russian display name (`NameRu` is null or empty), the application previously fell back directly to the English title (`TitleEn`).

In this change, CardioSimulator leverages `Taxonomy.tsv` (`Taxonomy.shared`) to dynamically synthesize single or composite Russian pathology titles from the pathology's taxonomy acronyms (`AcronymList`).

### Key Behaviors:
1. **Explicit Precedence**: An authored `name:` in the `.dat` file or manifest entry (`NameRu`) is used first.
2. **Taxonomy Translation**: If `NameRu` is null/empty, look up each code in `AcronymList` in `Taxonomy.shared`. If matching `TaxonomyEntry` objects are found with Russian names, combine them into a single or comma-separated composite title (e.g. `"SB"` → `"Синусовая брадикардия"`; `["SB", "LVH"]` → `"Синусовая брадикардия, Гипертрофия левого желудочка"`).
3. **English Fallback**: If `NameRu` is missing and no taxonomy acronyms match, fall back to `TitleEn`.

---

## 2. Part A: Domain Model Extensions (`Pathology.kt`)

- Update `PathologyEntry` and `PathologyFile` in Android domain models.
- Add `resolvedNameRu` property or helper function `resolveNameRu(nameRu, acronyms, taxonomy)`:
```kotlin
fun resolveNameRu(nameRu: String?, acronyms: List<String>?, taxonomy: Taxonomy = Taxonomy.shared): String? {
    if (!nameRu.isNullOrBlank()) return nameRu
    if (acronyms.isNullOrEmpty()) return null
    val parts = acronyms
        .mapNotNull { code -> taxonomy.find(code)?.nameRu }
        .filter { it.isNotBlank() }
        .distinct()
    return if (parts.isNotEmpty()) parts.joinToString(", ") else null
}
```

---

## 3. Part B: Rhythm Loading & UI Integration

1. **Rhythm Repository / ViewModel (`RhythmViewModel.kt`)**:
   - During pathology index loading/enrichment, if a pathology entry lacks `nameRu`, populate it with `resolvedNameRu` from its dataset/taxonomy.
2. **Rhythm Choosing Panel / Selectors (`RhythmChoosingPanel.kt`, UI Composables)**:
   - Update pathology title display logic when Russian language (`Language.RU`) is selected to use `entry.resolvedNameRu ?: entry.titleEn`.
3. **ECG Monitor Overlays**:
   - Update monitor headers and comparison labels to use `resolvedNameRu` when displaying pathology titles in Russian mode.

---

## 4. Part C: Verification

### 4.1 Automated Tests
- Port unit tests from `PathologyTranslationTests.cs` to Android unit tests (`PathologyTranslationTest.kt`):
  - Test explicit `nameRu` precedence.
  - Test single acronym taxonomy lookup (`"SB"` → `"Синусовая брадикардия"`).
  - Test composite acronym taxonomy lookup (`["SB", "LVH"]` → `"Синусовая брадикардия, Гипертрофия левого желудочка"`).
  - Test untagged pathology fallback (returns `null`).

### 4.2 Manual Verification Flow
1. Set application language to Russian (`RU`).
2. Open the Rhythm Selector / Teaching Drawer.
3. Observe rhythms tagged with taxonomy acronyms but lacking explicit Russian names in `.dat` files.
4. Verify that single and composite Russian pathology titles are rendered cleanly from `Taxonomy.tsv`.
