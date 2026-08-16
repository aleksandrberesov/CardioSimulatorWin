# Plan: Port Student List Import & Export with Learning Data to Android

**Created:** 2026-08-16  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`  

---

## 1. Background & Goals

In the Windows version of CardioSimulator (`CardioSimulator\Win`), Import (`📥`) and Export (`📤`) buttons were added to the Students roster screen (`StudentsScreen`). These buttons allow instructors to:
1. Export the full student roster along with their stored assessment results (`ExamResult` and `OskeResult`) as a JSON package file (`students_export_YYYYMMDD.json`).
2. Import student data and assessment results from a JSON package, merging roster entries and adding assessment attempts safely without creating exact duplicate records.

The goal of this port is to replicate this import/export functionality on the Android platform within the Students screen UI.

---

## 2. Part A: Data Transfer Model & Storage Handling

- **Data Transfer Contract**: Define `StudentExportPackage` data class in Android matching the JSON schema:
  - `version: Int`
  - `exportedAt: String` / ISO date
  - `students: List<Student>`
  - `examResults: List<ExamResult>`
  - `oskeResults: List<OskeResult>`
  - `results: List<ExamResult>?` (legacy fallback mapping to `examResults`)
- **JSON Serialization**: Use `Gson` or `kotlinx.serialization` matching camelCase property conventions.
- **De-duplication Logic**:
  - Merge student entries by `fullName` + `group` (case-insensitive); update email if existing student email is empty.
  - Skip exact duplicate assessment attempts (matching student `fullName`, `group`, test/scenario ID, and timestamp within 2 seconds).

---

## 3. Part B: Android UI & Storage SAF Pickers

- **Students Screen UI**:
  - Add Import (`📥 Import`) and Export (`📤 Export`) buttons to the Students screen header / roster toolbar.
  - Display a status text banner for success (`Imported X students and Y results`) or error messages.
- **System Activity Launchers**:
  - Export: Use `ActivityResultContracts.CreateDocument("application/json")` to let the user save the JSON package file.
  - Import: Use `ActivityResultContracts.GetContent()` with mime type `application/json` or `*/*` to let the user pick a file via Storage Access Framework (SAF).

---

## 4. Part C: Verification

### 4.1 Manual Verification Flow
1. Open CardioSimulator on Android and navigate to the Students screen.
2. Verify that **Import** and **Export** buttons are present on the screen.
3. Register 1–2 test students and complete an exam attempt.
4. Tap **Export**, choose destination, and verify `students_export_*.json` file is saved.
5. Tap **Import**, select the exported JSON file, and verify student records and assessment results are loaded cleanly with a success message.
