# Exclude Course Archives from TCP Uploads

Currently, when the application connects to the TCP server, it automatically packages and sends both the pathologies and the courses as ZIP archives. We want to exclude the courses data from being sent.

## Proposed Changes

### ViewModel Layer

#### [MODIFY] [AppViewModel.cs](file:///e:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.App/ViewModels/AppViewModel.cs)
- Update `SendUploadArchiveAsync` to remove the logic checking for courses validity and invoking `SendSingleArchiveAsync` with `Courses.zip`.

---

## Verification Plan

### Automated Tests
- Run `dotnet test` in `e:\VLN_Project\CardioSimulatorWin` to make sure unit tests pass.
