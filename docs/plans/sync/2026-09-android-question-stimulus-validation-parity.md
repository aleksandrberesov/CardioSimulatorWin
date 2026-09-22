# Plan: Validate Question Stimulus (ECG & Image) Before Saving in Test Constructor and Bank (Android Parity)

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**  

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

### Problem
In the Test Constructor («Конструктор тестов») and Question Bank («Банк вопросов»):
When creating a question with type "ECG" («ЭКГ» / «Определи ЭКГ») or "Image" («Картинка»):
- If the user entered question text and options but did not select an ECG rhythm for the monitor («ЭКГ на мониторе») or an image, the question could still be saved.
- Saving an ECG question without an assigned rhythm (`pathologyId == null`) resulted in the question losing its ECG stimulus and falling back to a plain text question.
- **Expected result:** When attempting to save an ECG question without selecting an ECG rhythm for the monitor, saving is blocked and a warning toast/snackbar is shown: `"Выберите ЭКГ на мониторе"` (`"Select an ECG on the monitor"`). Similarly, saving an image question without selecting an image is blocked with `"Выберите изображение для вопроса"` (`"Select an image for the question"`).

### Fix Implemented on Windows
1. **Validation Logic (`TestConstructorViewModel.cs`):**
   - Extended `EditQuestion.InvalidReason` enum with `NoEcg` and `NoImage`.
   - Updated `EditQuestion.Validate()`:
     - If `Kind == QuestionStimulus.Ecg` and `string.IsNullOrWhiteSpace(PathologyId)` -> return `InvalidReason.NoEcg`.
     - If `Kind == QuestionStimulus.Image` and `string.IsNullOrWhiteSpace(ImagePath)` -> return `InvalidReason.NoImage`.
2. **Editor Guard (`TestConstructorScreen.cs`):**
   - In `GuardQuestionSavable(EditQuestion q)`: mapped `InvalidReason.NoEcg` to `AppStrings.BankErrNoEcg` and `InvalidReason.NoImage` to `AppStrings.BankErrNoImage`.
   - In `Save` (test saving): guarded each question in `_vm.Questions` before persisting.
3. **Localization (`AppStrings.cs`):**
   - Added / updated strings for `bank_err_no_ecg` ("Выберите ЭКГ на мониторе" / "Select an ECG on the monitor" / "请选择监护仪心电图" / "Selecciona el ECG en el monitor" / "मॉनिटर पर ईसीजी चुनें") and `bank_err_no_image`.

---

## 2. Part A: Question Validation Model in `TestConstructorViewModel.kt`

**File:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\viewmodels\TestConstructorViewModel.kt`

### Instructions
1. Define validation error enum / sealed class:
   ```kotlin
   enum class QuestionInvalidReason {
       NONE,
       NO_TEXT,
       TOO_FEW_OPTIONS,
       NO_CORRECT_OPTION,
       NO_ASSEMBLY_SOURCE,
       NO_IMAGE,
       NO_ECG
   }
   ```
2. Implement validation method for `TestQuestion`:
   ```kotlin
   fun validateQuestion(question: TestQuestion): QuestionInvalidReason {
       if (question.isAssembly) {
           return if (question.assemble?.sourcePathologyId.isNullOrBlank())
               QuestionInvalidReason.NO_ASSEMBLY_SOURCE
           else
               QuestionInvalidReason.NONE
       }
       if (question.text.isBlank()) return QuestionInvalidReason.NO_TEXT
       if (question.stimulus == QuestionStimulus.IMAGE && question.imagePath.isNullOrBlank()) {
           return QuestionInvalidReason.NO_IMAGE
       }
       if (question.stimulus == QuestionStimulus.ECG && question.pathologyId.isNullOrBlank()) {
           return QuestionInvalidReason.NO_ECG
       }
       val filled = question.options.filter { it.text.isNotBlank() }
       if (filled.size < 2) return QuestionInvalidReason.TOO_FEW_OPTIONS
       if (filled.none { it.id == question.correctOptionId }) return QuestionInvalidReason.NO_CORRECT_OPTION
       return QuestionInvalidReason.NONE
   }
   ```
3. Guard bank question save and test save operations using `validateQuestion(question)`. If invalid, surface an error event or StateFlow message.

---

## 3. Part B: UI Feedback in `TestConstructorScreen.kt` & String Resources

**Files:**
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\ui\screens\TestConstructorScreen.kt`
- `E:\VLN_Project\CardioSimulator\Android\app\src\main\res\values\strings.xml` (and localized variants)

### Instructions
1. Add string resources in `strings.xml`:
   ```xml
   <string name="bank_err_title">Нельзя сохранить вопрос</string>
   <string name="bank_err_no_text">Введите текст вопроса</string>
   <string name="bank_err_too_few_options">Заполните минимум два варианта ответа</string>
   <string name="bank_err_no_correct">Отметьте правильный ответ</string>
   <string name="bank_err_no_source">Выберите ритм-источник для сборки</string>
   <string name="bank_err_no_image">Выберите изображение для вопроса</string>
   <string name="bank_err_no_ecg">Выберите ЭКГ на мониторе</string>
   ```
2. In `TestConstructorScreen.kt`, when the user clicks "Сохранить вопрос" or "Сохранить тест", check validation and display a Snackbar with `bank_err_no_ecg` when `QuestionInvalidReason.NO_ECG` is returned.

---

## 4. Part C: Verification Plan
### 4.1 Manual Verification Flow: Image Question
1. Open the Android app and navigate to **Конструктор тестов** (Test Constructor).
2. Tap **Банк вопросов** (Question Bank).
3. Tap **Новый вопрос** (New Question).
4. Fill in the **Текст вопроса** field.
5. Provide at least two non-empty answer options, ensuring one is marked correct.
6. Select question type **Картинка** (Image).
7. Leave the image unselected / empty.
8. Tap **Сохранить вопрос** (Save question).
9. **Verify:**
   - A warning appears: `"Выберите изображение для вопроса"`.
   - The question is **not saved** to the bank.
10. Pick an image and tap **Сохранить вопрос**.
11. **Verify:** The question is now successfully saved and appears under the "Картинка" filter in the bank.

### 4.2 Manual Verification Flow: ECG Question
1. Follow steps 1-5 above, selecting question type **ЭКГ** (ECG).
2. Leave the **ЭКГ** rhythm selector unselected / empty.
3. Tap **Сохранить вопрос**.
4. **Verify:**
   - A warning appears: `"Выберите ЭКГ для вопроса"`.
   - The question is **not saved** to the bank.
5. Select a rhythm in the ECG picker and tap **Сохранить вопрос**.
6. **Verify:** The question is now successfully saved and appears under the "ЭКГ" filter in the bank.
