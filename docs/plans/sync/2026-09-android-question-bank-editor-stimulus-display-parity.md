# Plan: Port Question Bank Editor Stimulus Display Behavior to Android

**Created:** 2026-09-22  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Question Bank (`View.Bank` / `ConstructorTab.BANK`), when authoring or editing an individual question, the split layout provides an editor form on the right and a stimulus display panel on the left.

Previously, the left panel in the question editor always displayed the ECG monitor in a stopped/paused state with a Start button overlay, even when authoring a plain text question or an image-based question. The question image was only displayed as a small thumbnail within the editor form, and text questions displayed a residual ECG monitor trace from whatever rhythm was previously selected.

### Windows Implementation:
1. Retained the left split column for all question stimulus types when editing in the question bank.
2. Added an image stimulus view (`_bankStimulusImage`) to the left panel alongside the ECG monitor (`_monitor`).
3. Implemented stimulus resolution (`ApplyBankEditStimulus`):
   - **Text Question** (`Kind == QuestionStimulus.Text`): Left panel is kept empty (`_monitor` collapsed, `_startStop` collapsed, `_bankStimulusImage` collapsed).
   - **Image Question** (`Kind == QuestionStimulus.Image`): Left panel displays the selected question image (`_bankStimulusImage` visible with `Stretch.Uniform`, `_monitor` collapsed, `_startStop` collapsed).
   - **ECG Question** (`Kind == QuestionStimulus.Ecg`): Left panel displays the ECG monitor (`_monitor` visible, `_startStop` visible, `_bankStimulusImage` collapsed). If `PathologyId` is specified, preview runs automatically.
   - **ECG Assemble Question** (`IsAssembly == true`): Left panel displays the ECG monitor (`_monitor` visible, `_startStop` visible, `_bankStimulusImage` collapsed). If `AssembleSourceId` is specified, preview runs automatically.
4. When exiting question editing (returning to bank browse or changing tabs), preview is stopped and stimulus views are hidden/cleared.

### Android Goal:
Update Android's `TestConstructorScreen.kt` so that when editing a single bank question (`ConstructorTab.BANK` with `editingQuestionId != null`), the left panel (`weight(1f)`) renders the appropriate stimulus based on `question.stimulus` and `question.isAssembly`:
- Empty if `QuestionStimulus.TEXT`
- Image if `QuestionStimulus.IMAGE`
- Monitor if `QuestionStimulus.ECG` or assemble

---

## 2. Part A: TestConstructorScreen.kt UI Updates

- **Target File:** `Android/app/src/main/java/com/example/cardiosimulator/ui/screens/TestConstructorScreen.kt`
- **Reference File:** `src/CardioSimulator.App/Screens/TestConstructorScreen.cs`

### Steps:
1. In `TestConstructorScreen(appViewModel, monitorViewModel, rhythmViewModel, testConstructorViewModel)`:
   - When `activeTab == ConstructorTab.BANK && editingQuestionId != null`, obtain the current question being edited:
     ```kotlin
     val editingQuestion = bankQuestions.find { it.id == editingQuestionId }
     ```
   - In the left monitor panel (`Box(modifier = Modifier.weight(1f).middleSectionLeft())`):
     - Check the active question stimulus and assembly status:
       ```kotlin
       if (activeTab == ConstructorTab.BANK && editingQuestion != null) {
           when {
               editingQuestion.isAssembly -> {
                   // Show live ECG Monitor for Assemble source rhythm
                   Monitor(modifier = Modifier.fillMaxSize(), monitorViewModel = monitorViewModel) { ... }
               }
               editingQuestion.stimulus == QuestionStimulus.ECG -> {
                   // Show live ECG Monitor for question pathologyId
                   Monitor(modifier = Modifier.fillMaxSize(), monitorViewModel = monitorViewModel) { ... }
               }
               editingQuestion.stimulus == QuestionStimulus.IMAGE -> {
                   // Show question image if present, or empty space
                   val imageFile = editingQuestion.imagePath?.let { TestImageStore.getFile(it) }
                   if (imageFile != null && imageFile.exists()) {
                       Image(
                           painter = rememberAsyncImagePainter(imageFile),
                           contentDescription = null,
                           modifier = Modifier.fillMaxSize().padding(16.dp),
                           contentScale = ContentScale.Fit
                       )
                   }
               }
               else -> {
                   // Plain text question: render empty slot (no monitor, no image)
               }
           }
       } else {
           // Default monitor view when in Test constructor tab
           Monitor(modifier = Modifier.fillMaxSize(), monitorViewModel = monitorViewModel) { ... }
       }
       ```
2. When switching stimulus types in `SingleQuestionEditor`, ensure rhythm preview or image updates reactively.

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Open the Android application and navigate to **Конструктор тестов** (Test Constructor).
2. Switch to the **Банк вопросов** (Question Bank) tab.
3. Tap **Создать вопрос** (New Question) or edit an existing text question:
   - Verify the left panel is empty (no ECG traces, no monitor background/grid).
4. Change the question type to **Изображение** (Image):
   - Pick an image: verify the image is displayed prominently in the left panel.
5. Change the question type to **ЭКГ** (ECG):
   - Verify the ECG monitor appears on the left.
   - Select a rhythm: verify the monitor runs and renders the selected ECG.
6. Change the question type to **Собери ЭКГ** (Assemble):
   - Verify the ECG monitor appears on the left and runs the source rhythm preview.
7. Save or cancel: verify returning to the bank browse list cleanly shuts down the monitor.
