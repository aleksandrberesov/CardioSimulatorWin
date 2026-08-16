---
name: save-changes-as-android-sync-plan
description: Create a cross-platform sync plan (porting prompt) to port recent changes from the Windows repository to the Android version of CardioSimulator.
---

Use this skill when you need to capture changes made to the Windows repository (`CardioSimulator\Win`) and save them as a structured porting plan/prompt for the Android repository (`CardioSimulator`).

## Prerequisites
- The agent must have read access to both `E:\VLN_Project\CardioSimulator\Win` and `E:\VLN_Project\CardioSimulator` repositories.
- Familiarity with the target directories:
  - Reference source root: `E:\VLN_Project\CardioSimulator\Win\src\`
  - Target source root: `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`

## Step-by-Step Instructions

### Step 1: Detect Windows Repository Changes
Run git commands or ask the user to identify the files changed and the logic implemented.
1. Check for uncommitted changes using:
   ```powershell
   git status
   git diff
   ```
2. If there are no uncommitted changes, check the most recent commit:
   ```powershell
   git log -n 1
   git diff HEAD~1 HEAD
   ```
3. Identify the modified Windows component files (typically `.cs` files under `src/`).

### Step 2: Locate Corresponding Android Components
Locate the corresponding Android classes or components under `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`.
Examples of common mappings:
- `src/CardioSimulator.App/Rendering/EcgRenderer.cs` → `app/src/main/java/com/example/cardiosimulator/EcgRenderer.kt` (or similar rendering pipeline files)
- `src/CardioSimulator.App/Controls/EcgMonitorControl.cs` → `app/src/main/java/com/example/cardiosimulator/...` (Compose UI or controller components)

If the matching file is not immediately clear, search the Android directory for files with similar names or that contain matching keywords:
```powershell
# In E:\VLN_Project\CardioSimulator
Get-ChildItem -Recurse -Filter "*Renderer*"
```

### Step 3: Analyze Logic Differences and Porting Steps
For each changed file/component in the Windows codebase, determine:
1. What was the exact issue/goal?
2. What C# logic changes were made? (e.g. adding properties, modifying timers, logic conditions).
3. How should this look in Kotlin/Android? (e.g. changing Compose state, canvas draw loops, or standard Android utilities).
4. Break down the plan into logical sections (e.g., Part A: Monitor Clock/State Updates, Part B: Renderer modifications, etc.).

### Step 4: Write the Sync Plan Document
Create a markdown file named:
`docs/plans/sync/YYYY-MM-android-<feature-kebab-case>-parity.md`

Use the following template:

```markdown
# Plan: Port <Feature Name> Behavior to Android

**Created:** <YYYY-MM-DD>  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

<Provide background on the issue or feature. Explain how it behaved in the Windows version, the fix/implementation that was made, and the desired outcome on Android.>

---

## 2. Part A: <Component/Logic Area 1>

<Outline the changes needed for the first component (e.g. view model, controller, state).>
- Identify the matching Kotlin file/class.
- Step-by-step instructions on what to change or implement, including reference snippets.

---

## 3. Part B: <Component/Logic Area 2>

<Outline the changes needed for the second component (e.g. rendering engine, Canvas drawing).>
- Identify the matching Kotlin file/class.
- Detailed logic modifications, equations, or methods to update.

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
Provide clear, step-by-step instructions for a developer or agent to verify the parity on Android:
1. <Step 1: Open app/emulator>
2. <Step 2: Action to perform>
3. <Step 3: Verification criteria/expected behavior>
```

### Step 5: Save and Sync the Plan
1. Save the plan to `E:\VLN_Project\CardioSimulator\Win\docs\plans\sync\YYYY-MM-android-<feature-kebab-case>-parity.md`.
2. Mirror the file by saving/copying it to `E:\VLN_Project\CardioSimulator\docs\plans\sync\YYYY-MM-android-<feature-kebab-case>-parity.md` so that the Android agent/developer has access to it.
3. Inform the user of the created plan, its path, and display a summary of the porting steps.
