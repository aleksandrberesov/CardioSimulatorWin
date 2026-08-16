# Plan: Port Table of Contents Exclusion in Learning Scale Screen to Android

**Created:** 2026-08-16  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the course navigation catalog (such as `CourseThemeCatalog`), overview / table-of-contents subtopic items that have no leading numeration and repeat the parent section's name are filtered out to avoid redundant items under a section. The **Learning Scale («Шкала обучения»)** dashboard's course map section builder (`LearningScaleViewModel`) has been updated to apply this exact same table-of-contents exclusion logic.

---

## 2. Part A: Android View Model TOC Exclusion Alignment

**Target:** `app/src/main/java/com/example/cardiosimulator/ui/viewmodels/LearningScaleViewModel.kt`

1. **Implement `isTableOfContents(subName: String?, sectionName: String?)` helper**:
   - Check if `subName` or `sectionName` is null or blank.
   - Check if `subName` starts with leading digits/numeration (e.g., `1.1`, `4.6.1`). If it has leading numeration, return `false`.
   - Return `true` if `sectionName` contains `subName` (case-insensitive).

2. **Filter Subtopics in Course Builder**:
   - When mapping lectures/subtopics for each section in `LearningScaleViewModel`, filter out subtopics where `isTableOfContents(subtopic.name, section.name)` returns `true`.

---

## 3. Verification

### 3.1 Manual Verification Flow
1. Open the Android application and switch to the **Learning Scale («Шкала обучения»)** screen.
2. Expand sections (e.g., Section 1, Section 2) in the course map.
3. Verify that overview / table-of-contents pages repeating the section title are excluded, and only numbered subtopics (or distinct leaf sections) are displayed.
