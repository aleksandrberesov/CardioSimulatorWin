# Plan: Port Question Bank "Delete All" Feature to Android

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version of CardioSimulator, the Question Bank had actions to create questions, import JSON batches, and export JSON, but no button to wipe or empty the standing question pool all at once (users had to delete questions one by one). Additionally, on app startup, sample bank questions could be re-seeded if the bank became empty unless marked as already handled.

To address this:
1. Added `DeleteAll()` to `FileQuestionBankSource` and `QuestionBankRepository` which deletes all question `.json` files in the bank directory while safely preserving `themes.json`.
2. Added `DeleteAllBankQuestions()` to `TestConstructorViewModel`.
3. In `TestConstructorScreen`, added a "Delete all" (`AppStrings.BankDeleteAll` / "Удалить все") action button in the Question Bank filter/actions toolbar (`BankFilters`), as well as a secondary action in the "Add from bank" dialog (`OnAddFromBankAsync`).
4. Added confirmation dialogs (`AppStrings.BankDeleteAllConfirm`) before wiping the bank.
5. In `DataSourcePrefs` / `AppViewModel`, tracked `QuestionBankSeeded` so that once seeded or cleared, the application does not automatically re-inject sample seed questions upon restart.

On Android, corresponding parity is needed:
- Ability to delete all bank questions from the Question Bank browse screen (`BankActionButtons`).
- Ability to wipe bank questions from any bank selection dialogs if applicable.
- Confirmation dialog (`AlertDialog`) before deletion.
- Updating `QuestionBankRepository` and `TestConstructorViewModel` to support `deleteAll()`.
- Preventing unwanted re-seeding when the bank is intentionally emptied.

---

## 2. Part A: Repository and ViewModel (`TestData.kt` & `TestConstructorViewModel.kt`)

### 2.1 FileQuestionBankSource and QuestionBankRepository
File: `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\data\TestData.kt`
- In `FileQuestionBankSource`:
  Add method `deleteAll(): Boolean`:
  Iterate over all files in `root`, delete `*.json` files except `themes.json` (or any theme store file).
- In `QuestionBankRepository`:
  Add method `deleteAll(): Boolean`:
  Calls `source.deleteAll()`, clears internal cache, and reloads/emits empty list.

### 2.2 TestConstructorViewModel
File: `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\viewmodels\TestConstructorViewModel.kt`
- Add `fun deleteAllBankQuestions()`:
  Calls `bankRepository.deleteAll()`, clears `filteredBankQuestions`, resets `bankPage` to 0.

---

## 3. Part B: UI Implementation (`TestConstructorScreen.kt`)

### 3.1 BankActionButtons in Question Bank Browse View
File: `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\screens\TestConstructorScreen.kt`
- In `BankActionButtons`:
  - Add an action button (e.g. `OutlinedButton` with `Icons.Default.DeleteSweep` or `Delete` icon) for "Delete all" / `stringResource(R.string.bank_delete_all)`.
  - Disable or hide if `bankQuestions.isEmpty()`.
  - When clicked, trigger an `AlertDialog` confirming:
    - Title: "Удалить все" / "Delete all"
    - Message: "Удалить все вопросы из банка? Банк вопросов станет пустым." / "Delete all questions from the bank? This will empty the question bank."
    - Confirm button: invokes `viewModel.deleteAllBankQuestions()`.
    - Dismiss/Cancel button.

### 3.2 Add to Strings Resource (`strings.xml`)
File: `E:\VLN_Project\CardioSimulator\Android\app\src\main\res\values\strings.xml` and `values-ru\strings.xml`:
- `bank_delete_all`: "Delete all" / "Удалить все"
- `bank_delete_all_confirm`: "Delete all questions from the bank? This will empty the question bank." / "Удалить все вопросы из банка? Банк вопросов станет пустым."

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open CardioSimulator on Android.
2. Navigate to **Test Constructor** (`Конструктор тестов`).
3. Switch to the **Question Bank** tab (`Банк вопросов`).
4. Verify the new "Delete all" (`Удалить все`) button is visible in the toolbar.
5. Tap "Delete all". Verify a confirmation dialog appears warning that all questions will be removed.
6. Confirm deletion.
7. Verify the bank count drops to 0, question grid displays the empty state message ("The question bank is empty" / "Банк вопросов пуст"), and pagination updates.
8. Restart the application and verify sample questions are not re-seeded if the bank was intentionally cleared.
