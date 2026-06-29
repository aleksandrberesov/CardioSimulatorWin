# Implementation Plan - Fix Course ZIP Export (Recursive Compression)

## Problem Description
Currently, the course ZIP export function produces a ZIP file containing only `manifest.txt`. This happens because `ZipCompressor.Zip` is implemented to only pack files at the top-level of the target directory (`Directory.GetFiles(sourceDir)`), completely ignoring subdirectories (such as `cardio-101/`).

This plan details how to make the ZIP compression recursive, so that all course and pathology nested contents are correctly included in the exported archives.

---

## User Review Required
No breaking changes or major design decisions are required. The recursive zip compression preserves the flat structure for Pathologies (since they are flat on disk anyway) while enabling correct hierarchical zipping for Courses.

---

## Proposed Changes

### CardioSimulator.Core (Data Layer)

#### [MODIFY] [ZipCompressor.cs](file:///e:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.Core/Data/ZipCompressor.cs)
* Modify `WriteArchive` to recursively retrieve all files within the source directory using `Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories)`.
* Compute the relative path of each file using `Path.GetRelativePath` to preserve the directory structure.
* Replace any backslashes (`\`) with forward slashes (`/`) in the relative path for cross-platform compatibility of ZIP archives.
* Update XML documentation comments to reflect recursive archiving.

---

## Verification Plan

### Automated Tests
We will build the solution and run existing unit tests to verify no regressions:
```powershell
dotnet test
```

### Manual Verification
1. Run the application and navigate to Settings.
2. Click **Export Courses ZIP** under the Courses section.
3. Verify that the exported ZIP file contains the full structure:
   - `manifest.txt`
   - Course directories (e.g., `cardio-101/course.txt`)
   - Lectures (e.g., `cardio-101/lectures/01-intro.en.html`)
4. Verify that **Export Pathologies ZIP** continues to work and produces a valid ZIP archive containing `manifest.txt` and all `.dat` files at the root of the ZIP.
