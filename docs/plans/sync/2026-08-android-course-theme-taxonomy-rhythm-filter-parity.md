# Plan: Port Course Theme/Sub-theme Taxonomy Rhythm Filtering to Android

**Created:** 2026-08-19  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In Teaching mode, when a user selects a course theme (Тема) or sub-theme (Подтема / lecture) and navigates to the ECG Monitor or opens the Rhythm Selector, the list of available rhythms should reflect the clinical concept being taught.

Previously, only explicit rhythm IDs (`theme.pathologies`) listed in course JSONs were filtered. With this update, CardioSimulator queries `Taxonomy.tsv` (`Taxonomy.shared`) using the section/subsection metadata (`Section`, `Subsection`, `SubtopicKey`, `AltSubsections`) of the selected theme or sub-theme to automatically populate the rhythm list with all matching ECG pathology datasets.

### Performance Requirements:
1. **Strict Regex Numeration Extraction**: Avoid matching single digits in arbitrary string IDs (`"lec1"`, `"topic2"`). Match dotted subsections (`"4.6.2"`, `"4.6"`) or explicit section headers (`"Раздел 4"`).
2. **O(1) HashSet Filtering**: Filter pathologies using a `Set<String>` of IDs or acronyms to prevent $O(N \times M)$ UI thread lags.
3. **No-op Re-filters**: Skip re-filtering if the course filter ID list is unchanged.

---

## 2. Part A: Taxonomy Extensions (`Taxonomy.kt`)

1. Add `forSubsectionOrTopic(subsectionOrKey: String?)` method to `Taxonomy`:
   - Match exact `subsection`, `subtopicKey`, or `altSubsections`.
   - Match subtopic key prefix (e.g. `"4.6.2"` → `"4.6"`).
   - Match top-level `section` number (e.g. `"4"`).

2. Add `resolvePathologyIdsForAcronyms(acronyms: Collection<String>, pathologies: Collection<PathologyEntry>)`:
   - Filter `pathologies` returning IDs of any pathology whose `acronymList` intersects with `acronyms`.

---

## 3. Part B: App State & Rhythm Filter (`AppViewModel.kt` / `RhythmViewModel.kt`)

1. Update `effectiveTeachingPathologies` calculation:
   - Extract `selectedLecture` and `selectedTopic`.
   - Extract dotted subsection keys from `lecture.subsection`, `topic.subsection`, and numeration prefixes in titles/IDs (e.g., `"4.6.2"`).
   - Query `Taxonomy.shared.forSubsectionOrTopic()` for matching acronyms.
   - Query `Taxonomy.resolvePathologyIdsForAcronyms()` against all loaded pathologies.
   - Return union of course-wide rhythms, explicit topic rhythms, and taxonomy-resolved pathology IDs.

2. React to navigation in Course Viewer:
   - Re-filter `RhythmViewModel.setCourseFilter()` whenever selected topic/lecture changes in Teaching mode using a `HashSet<String>` for $O(N)$ filtering speed.

---

## 4. Part C: Verification

### 4.1 Automated Tests
- Port `TaxonomyCourseMappingTests.cs` to Android unit tests (`TaxonomyCourseMappingTest.kt`).
- Verify subsection, subtopic key, and section number acronym resolution.

### 4.2 Manual Verification Flow
1. Open Teaching Mode and select a course with topics/subtopics (e.g. Section 4 AV Blocks).
2. Tap a sub-theme / lecture (e.g. `4.6.2` or `4.6`).
3. Open the Rhythm Selector in Monitor mode.
4. Verify that rhythms carrying matching taxonomy acronyms (`1AVB`, `2AVB1`, `2AVB2`, `3AVB`) are populated smoothly without UI lag or freezing.
