# Plan: Port Pathology & Clinical Case Russian Title Taxonomy Fallback to Android

**Created:** 2026-08-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When operating CardioSimulator in Russian language mode (`Language.RU`), all pathology entries, rhythm drawer items, subgroup headers, and clinical case info cards display translated Russian titles.

Performance optimization & re-entrancy prevention when toggling Clinical Case mode:
1. `ResolveTextRu` is cached in a thread-safe map (`ConcurrentDictionary` in C# / `ConcurrentHashMap` in Kotlin) so regex translation of hundreds of dataset titles executes in <1ms.
2. `RhythmChoosingPanel.rebuild()` uses an `isRebuilding` guard and enqueues filter auto-selection events asynchronously to avoid UI re-entrancy loops.

---

## 2. Part A: Taxonomy Extensions & Caching (`Taxonomy.kt`)

1. Add `TextRuCache` map to `PathologyTranslationHelpers`:
   - Store input English string $\rightarrow$ translated Russian string.
2. Add `EnglishRules` to `Taxonomy`:
   - Regex pattern pairs matching canonical English finding phrases (e.g. `lower voltage QRS in all lead` $\rightarrow$ `LVQRSAL`, `Artificial pacing rhythm` $\rightarrow$ `APACE`).
3. Update `PathologyEntry.resolvedNameRu`:
   - Check `resolveTextRu(titleEn)` (verifying no remaining ASCII letters `[a-zA-Z]`).
   - Fall back to `resolveNameRu(acronyms)`.

---

## 3. Part B: Rhythm Picker & Re-entrancy Protection (`RhythmChoosingPanel.kt`)

1. Add `isRebuilding` guard inside `rebuild()`.
2. Enqueue `RhythmSelected` listener notifications when `autoSelectOnFilter` selects a new item so drawer re-rendering finishes before event dispatch.
3. Pass clinical case titles in `getClinicalCaseTitle` through `resolveTextRu()` when `DisplayLanguage == DomainLanguage.RU`.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open CardioSimulator on Android emulator/device.
2. Switch app language to Russian (Русский).
3. Open the Rhythm Selector / Teaching Screen.
4. Toggle Clinical Case mode (Клинический случай).
5. Verify:
   - Switching modes is instantaneous without UI frame drops or freezes.
   - All clinical case titles (e.g. `"1 degree atrioventricular block + Artificial pacing rhythm"`) display in Russian (`"АВ-блокада 1 степени + Ритм ЭКС (искусственный)"`).
