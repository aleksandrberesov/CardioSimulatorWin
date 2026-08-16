# Plan: Port Test Constructor Question Bank Acronym Selector Behavior to Android

**Created:** 2026-08-15  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows version (`TestConstructorScreen.cs`), the rhythm selector in Question Bank mode previously listed raw rhythm IDs directly. To establish consistency with Generator mode, the rhythm dropdown was updated to display canonical taxonomy acronyms (`AcronymLabel(code)  ·  inBank`) instead of individual rhythm items.

When an acronym filter is selected in Question Bank mode:
1. The dropdown lists unique taxonomy acronyms exhibited by rhythms in the system, along with the total count of bank questions available for each acronym.
2. A question matches the selected acronym filter if either:
   - Its bound rhythm (`PathologyId`) exhibits that acronym code, OR
   - The question is directly tagged with that acronym code in its `AcronymList`.

This plan outlines porting this acronym selector and filtering behavior to the Android implementation of the Test Constructor / Question Bank screen.

---

## 2. Part A: Question Bank Acronym Filter UI & State

1. Locate the Android Question Bank filter UI (e.g. `TestConstructorScreen.kt` or `QuestionBankFilterSection.kt`).
2. Update the rhythm filter dropdown state:
   - Replace the list of individual rhythms with distinct taxonomy acronym codes obtained from available pathology/rhythm entries (`RhythmAcronyms()`).
   - Format dropdown options as `"<Acronym Code> — <Name>  ·  <Count>"`.
   - Compute question counts per acronym code considering both direct question acronym tags and inherited acronyms from bound pathology entries.

---

## 3. Part B: Bank Question Filtering Logic

1. Update the bank filtering function (`FilteredBankQuestions()` or Kotlin equivalent):
   - When an acronym filter code is selected:
     - Expand the selected acronym code to all pathology/rhythm IDs that exhibit it.
     - Include bank questions where `pathologyId` matches any expanded rhythm ID, OR where `acronyms` list contains the selected acronym code.
   - Include question acronym tags in the text search filter (`q.acronyms.any { it.contains(searchQuery, ignoreCase = true) }`).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Navigate to Test Constructor -> Question Bank tab.
2. Open the Rhythm/Acronym filter dropdown in the filter bar.
3. Verify that the dropdown lists taxonomy acronyms with question counts (e.g. `NOR — Normal Sinus Rhythm  ·  5`).
4. Select an acronym (e.g. `AF`) and verify that matching questions (both rhythm-bound and directly tagged) are displayed in the list.
5. Search for an acronym code in the search text box and verify matching questions appear.
