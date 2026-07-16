# Implementation Plan: Pathology Dataset Weight Reduction (Delta-Binary Format)

We evaluated several data formats on a sample of 50 pathology records from the dataset to find the most efficient way to store ECG waveforms. The results were:

*   **Original plaintext (.dat):** 2.47 MB uncompressed / 367 KB zipped (baseline).
*   **Base64-encoded binary:** 1.37 MB uncompressed / 426 KB zipped (+16.0% size increase due to Base64 entropy).
*   **Raw 16-bit binary:** 1.02 MB uncompressed / 302 KB zipped (-17.8% size reduction).
*   **Delta-encoded 16-bit binary:** 1.02 MB uncompressed / **130 KB zipped (-64.6% size reduction)**.

### Proposed Solution: Delta-Binary Compilation
By storing ECG points as delta-encoded 16-bit integers, we achieve a **64.6% reduction in zipped dataset size** while maintaining high loading speeds (no string parsing). We propose a dual-mode parser:
1.  **Loose files on disk during development** will remain plain text `.dat` files for easy editing and git compatibility.
2.  **During the build step (`pack-data-zips.ps1`)**, `ContentPacker` will automatically convert plaintext `.dat` files in the source `.zip` to the delta-binary format inside the final encrypted `.pak` distribution files.
3.  The C# runtime parser will support **both formats seamlessly** by looking at a magic header `CSD1`. If the file starts with `CSD1`, it reads as delta-binary; otherwise, it reads as plaintext.

---

## User Review Required

> [!IMPORTANT]
> The Android port will need to match the new `CSD1` binary decoding logic to open the updated `.pak` files. We will output an Android Parity Sync Plan at the end of the task to guide this.

---

## Open Questions

None at this time.

---

## Proposed Changes

### CardioSimulator.Core

#### [MODIFY] [PathologyParser.cs](file:///E:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.Core/Domain/PathologyParser.cs)
*   Add `ParsePathology(byte[] bytes)` that detects the `CSD1` header.
*   Implement `ParsePathologyBinary(byte[] bytes)` using delta-decoding.
*   Implement `SerializePathologyBytes(PathologyFile file, IReadOnlyList<Lead> leadOrder)` to generate the `CSD1` binary payload.
*   Refactor the existing `ParsePathology(string text)` to forward to `ParsePathology(byte[] bytes)` (encoded as UTF-8) for backward compatibility.

#### [MODIFY] [FilePathologySource.cs](file:///E:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.Core/Data/FilePathologySource.cs)
*   Modify `ReadPathology` to use `File.ReadAllBytes(path)` instead of `File.ReadAllText`, forwarding it to `PathologyParser.ParsePathology(byte[])`.

#### [MODIFY] [EncryptedPathologySource.cs](file:///E:/VLN_Project/CardioSimulatorWin/src/CardioSimulator.Core/Data/EncryptedPathologySource.cs)
*   Modify `ReadPathology` to call `_archive.ReadByName` (getting `byte[]`) instead of `ReadByNameText`, forwarding it to `PathologyParser.ParsePathology(byte[])`.

---

### ContentPacker (Tool)

#### [MODIFY] [Program.cs](file:///E:/VLN_Project/CardioSimulatorWin/tools/ContentPacker/Program.cs)
*   Modify the `Pack` method to convert `.dat` files to delta-binary in memory before encrypting the archive.

---

### Tests

#### [MODIFY] [PathologyParserTests.cs](file:///E:/VLN_Project/CardioSimulatorWin/tests/CardioSimulator.Core.Tests/PathologyParserTests.cs)
*   Add unit tests verifying that binary serialization and parsing works and round-trips correctly.

---

## Verification Plan

### Automated Tests
*   Run unit tests: `dotnet test tests/CardioSimulator.Core.Tests` to verify no regressions in plain-text parsing, and verify binary serialization/parsing.

### Manual Verification
*   Run `pack-data-zips.ps1` to rebuild the `.pak` files and verify they build successfully.
*   Run `ContentPacker inspect-pathologies` to verify that `ContentPacker` and the runtime reader can successfully open and parse the binary `.pak` file contents.
*   Launch CardioSimulator and verify pathologies load and display correctly.
