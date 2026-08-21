# Plan: Port Pathology, Academic List & Clinical Case Info Card Russian Translation Fallback to Android

**Created:** 2026-08-19  
**Updated:** 2026-08-20  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

When operating CardioSimulator in Russian language mode (`Language.RU`), all pathology entries, rhythm drawer items, academic list topics/lectures, and clinical case info cards display translated Russian titles.

If explicit `name_ru` is unauthored in a dataset or clinical case metadata, CardioSimulator resolves the Russian title from `Taxonomy.tsv` (`Taxonomy.shared`) using:
1. Full non-ASCII translation of compound English display names (`ResolveTextRu(titleEn)`).
2. Cached lookup table (`TextRuCache`) for fast execution.
3. Phrase rules matching canonical finding terms.

---

## 2. Component Extensions

### 2.1 Academic List & Course Dropdowns (`CourseTopicFlyout.kt` / `CourseSelectorDrawer.kt`)
1. In `topicName()` and `lectureName()`, when `language == RU`:
   - Return `topic.nameRu ?: PathologyTranslationHelpers.resolveTextRu(topic.titleEn) ?: topic.titleEn`.
2. Apply `resolveTextRu()` to course drawer buttons and lecture item labels in `CourseSelectorDrawer.kt`.

### 2.2 Clinical Case Info Card (`RhythmChoosingPanel.kt`)
In `parseClinicalCase()`:
- `title`: `resolveTextRu(titleVal) ?: titleVal`
- `description`: `resolveTextRu(descriptionVal) ?: descriptionVal`
- `name`: `resolveTextRu(nameVal) ?: nameVal`
- Custom key-value pairs: Both `customKey` and `customVal` passed through `resolveTextRu()` when operating in Russian mode.

---

## 3. Verification

### 3.1 Manual Verification Flow
1. Open CardioSimulator on Android emulator/device.
2. Switch app language to Russian (Русский).
3. Open Academic List (Course Topics / Dropdowns) and Clinical Case Info Box at the bottom of the list.
4. Verify all titles, descriptions, and custom parameter fields display in Russian.
