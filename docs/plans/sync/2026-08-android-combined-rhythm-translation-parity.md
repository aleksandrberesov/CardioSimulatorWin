# Plan: Port Combined Rhythm Translation Parity Fixes to Android

**Created:** 2026-08-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In CardioSimulator (Windows version), combined rhythm titles (such as `"Sinus rhythm + PACs"`, `"Sinus rhythm + 2 degree AV block"`, `"Sinus rhythm with PVCs"`, `"Sinus rhythm + Mobitz I"`) were not translated properly when Russian language mode was selected (`DomainLanguage.RU`).
Specifically, untranslated or incomplete titles occurred in three key UI locations:
1. **Academic Mode Rhythm Selector - Subgroup Headers** (`RhythmSubgroupHeader`)
2. **Academic Mode Rhythm Selector - Rhythm Item Rows** (`RhythmItem`)
3. **Monitor Top Selected Rhythm Label** (`MonitorViewerOverlay` title / `TopControlPanel`)

### Root Cause
- `PathologyTranslationHelpers.ResolveNameRu` dropped translated composite titles (`textRu`) whenever any single component contained an English abbreviation (like `PACs`, `PVCs`, `VPB`, `APB`, `Mobitz I`, `1AVB`, `2AVB`, etc.) or if `nameRu` was stored in English.
- `Taxonomy.EnglishRules` lacked regex pattern rules for common combined rhythm abbreviations (e.g. `PACs`, `PVCs`, `PABs`, `PVBs`, `PJCs`, `Mobitz I/II`, `1st/2nd/3rd degree AV block`, `NSR`, `LBBB/RBBB`, etc.).
- `ResolveTextRuInternal` did not normalize conjunction delimiters (`with`, `and`) or split on semicolons.
- When `ResolveTextRu` was rejected due to English character checks, `ResolveNameRu` fell back to single primary acronym lookups, discarding secondary findings (e.g., dropping `+ PACs` and returning only `Sinus rhythm`).

The Android application shares the same domain translation helpers (`PathologyTranslationHelpers` / `Pathology.kt` and `Taxonomy.kt`) and UI panels (`RhythmSelector.kt` and `TopControlPanel.kt`).

---

## 2. Part A: Domain Model & Translation Rules (`Taxonomy.kt` & `Pathology.kt`)

### Matching Android Files:
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\domain\Taxonomy.kt`
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\domain\Pathology.kt` (or `PathologyTranslationHelpers.kt`)

### Implementation Steps:
1. **Expand `EnglishRules` in `Taxonomy.kt`**:
   Add rule mappings for combined rhythm terms and abbreviations:
   - `PACs?`, `PABs?`, `APBs?` → `APB` (`"Предсердная экстрасистолия"`)
   - `PVCs?`, `PVBs?`, `VPBs?`, `VEBs?` → `PVC` (`"Желудочковая экстрасистолия"`)
   - `PJCs?`, `JPTs?` → `JPT` (`"Узловая экстрасистола"`)
   - `SVPBs?`, `SVES` → `SVPB` (`"Наджелудочковая экстрасистолия"`)
   - `mobitz\s*(type\s*)?(2|ii|two)\b|mobitz\s*2\b` → `2AVB2` (`"АВ-блокада 2 ст. (Мобитц II)"`)
   - `mobitz\s*(type\s*)?(1|i|one)\b|mobitz\s*1\b|wenckebach` → `2AVB1` (`"АВ-блокада 2 ст. (Мобитц I)"`)
   - `(1st|1|first)\s*(degree)?\s*(atrioventricular|av)?\s*block` → `1AVB` (`"АВ-блокада 1 степени"`)
   - `(2nd|2|second)\s*(degree)?\s*(atrioventricular|av)?\s*block` → `2AVB` (`"АВ-блокада 2 степени"`)
   - `(3rd|3|third)\s*(degree)?\s*(atrioventricular|av)?\s*block` → `3AVB` (`"АВ-блокада 3 степени (полная)"`)
   - `normal sinus rhythm|\bNSR\b` → `SR` (`"Синусовый ритм"`)
   - `complete left/right bundle branch block`, `lbbb`, `rbbb`, `wpw`

2. **Update `ResolveNameRu` in `Pathology.kt`**:
   - Check if `nameRu` contains English letters (`[a-zA-Z]`); if so, attempt `resolveTextRu(nameRu)`.
   - Normalize ` with ` and ` and ` to ` + ` in `resolveTextRuInternal`.
   - Split compound strings on `+`, `,`, `;`.
   - In `resolveNameRu`: If `titleEn` translates into a valid Russian string without English characters, and `acronyms` contains only 1 acronym while `titleEn` has multiple parts (e.g. `"Sinus rhythm + PACs"` with `acronyms = ["SR"]`), return the translated compound string so secondary findings are retained.

---

## 3. Part B: UI Components (`RhythmSelector.kt` & `TopControlPanel.kt`)

### Matching Android Files:
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\panels\RhythmSelector.kt`
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\panels\TopControlPanel.kt`

### Implementation Steps:
1. Ensure subgroup headers and row titles in `RhythmSelector.kt` call `entry.resolvedNameRu` when language is Russian.
2. Ensure top control panel monitor title in `TopControlPanel.kt` renders `rhythm.resolvedNameRu` when language is Russian.

---

## 4. Part C: Verification

### 4.1 Automated Tests
Run Android unit tests:
```bash
./gradlew testDebugUnitTest --tests "*PathologyTranslationTest*"
```

### 4.2 Manual Verification Flow
1. Launch Android app in Russian language mode (`RU`).
2. Switch to Academic Mode (non-clinical case view) in the Rhythm Selector.
3. Observe subgroup headers (e.g., `"Синусовый ритм + Предсердная экстрасистолия"`).
4. Observe item rows for combined rhythms (e.g., `"Синусовый ритм + АВ-блокада 2 степени"`).
5. Select a combined rhythm and verify the monitor top label displays the translated Russian name.
