# Plan: Sync Pathology Description Field to Android

**Created:** 2026-07-01  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\app\src\main\java\com\example\cardiosimulator\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulatorWin\src\`  

---

## 1. Background & Goals

In the Windows version, we have added support for a multiline **Description** field associated with each pathology. This field is stored under the `description:` header field in the `.dat` files (with newlines serialized as escaped `\n` characters). 

We need to implement the same description storage, parsing, viewing, and editing pipeline in the Android repository to maintain absolute feature parity.

---

## 2. Part A: Pathology Data & Parser Updates

### 2.1 Model Changes
Update the domain classes/records to include the `description` field:
- Add a nullable `description: String?` property to the `PathologyFile` and `PathologyEntry` data classes.
- Ensure that the copy constructor or helper functions copy this field.

### 2.2 Serialization & Deserialization
Update the pathology parser (equivalent to C#'s `PathologyParser.cs`):
- **Deserialization**: Parse the `description:` header line if present. Convert all escaped `\n` characters in the string back to actual newlines (e.g. `description.replace("\\n", "\n")`).
- **Serialization**: Write the `description:` line into the file header. Convert actual newlines to escaped `\n` characters (e.g. `description.replace("\r\n", "\n").replace("\n", "\\n")`).

### 2.3 Viewmodel Changes
Update `ConstructorViewModel`:
- Add a `currentDescription` state property or helper.
- Implement a `setDescription(description: String?)` method that modifies the active pathology file's description and marks the metadata as dirty.

---

## 3. Part B: UI & Localization in Android

### 3.1 Constructor Toolbar Button
- Add a description button to the constructor screen's toolbar. 
- Use the standard Info/Help icon (e.g., `Icons.Default.Info` or vector asset matching `\uE946` glyph).
- Set its visibility to be shown only when a pathology file is loaded.

### 3.2 Multiline Input Dialog
- When the description button is clicked, display a Dialog (or Bottom Sheet) containing:
  - A header titled "Pathology Information"
  - A multiline `TextField` filled with the current description. Set `minLines = 4`, `maxLines = 6`, and enable text wrapping.
  - OK and Cancel buttons.
- On OK click, update the viewmodel's description parameter.

### 3.3 Localization Strings
Add translations for the new strings in the localization asset folder (e.g. `strings.xml` or custom dictionaries):
- **English**:
  - `pathology_description_label` = "Pathology Information"
  - `description_edit_tooltip` = "Edit pathology information"
  - `description_edit_title` = "Pathology Information"
- **Russian**:
  - `pathology_description_label` = "Информация о патологии"
  - `description_edit_tooltip` = "Редактировать информацию о патологии"
  - `description_edit_title` = "Информация о патологии"
- **Spanish**:
  - `pathology_description_label` = "Información de la patología"
  - `description_edit_tooltip` = "Editar información de la patología"
  - `description_edit_title` = "Información de la patología"
- **Chinese**:
  - `pathology_description_label` = "病理信息"
  - `description_edit_tooltip` = "编辑病理信息"
  - `description_edit_title` = "病理信息"
- **Hindi**:
  - `pathology_description_label` = "पैथोलॉजी जानकारी"
  - `description_edit_tooltip` = "पैथोलॉजी जानकारी संपादित करें"
  - `description_edit_title` = "पैथोलॉजी जानकारी"

---

## 4. Part C: Verification

### 4.1 Unit Tests
- Add a unit test to verify that the parser reads and extracts multiline descriptions correctly with newline expansion.
- Add a unit test to verify that serialization roundtrips descriptions correctly.
