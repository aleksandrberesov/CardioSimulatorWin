# Build Instructions

This project uses a PowerShell script for building, testing, and publishing the application.

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows 10/11 (for WinUI 3 support)

## Usage

Run the `tools\build.ps1` script from the project root using PowerShell.

### Basic Build

Builds the application and runs tests in `Release` configuration for `x64`.

```powershell
.\tools\build.ps1
```

### Build with Specific Configuration and Platform

```powershell
.\tools\build.ps1 -Configuration Debug -Platform x86
```

### Skip Tests

```powershell
.\tools\build.ps1 -SkipTests
```

### Clean and Build

```powershell
.\tools\build.ps1 -Clean
```

### Publish Application

Publishes the application to the `artifacts/publish` folder as a self-contained app.

```powershell
.\tools\build.ps1 -Publish
```

## Parameters

| Parameter | Default | Description |
|-----------|---------|-------------|
| `-Configuration` | `Release` | Build configuration (e.g., `Debug`, `Release`). |
| `-Platform` | `x64` | Target platform (e.g., `x86`, `x64`, `arm64`). |
| `-SkipTests` | `false` | If set, skips running the test suite. |
| `-Publish` | `false` | If set, publishes the application after a successful build. |
| `-Clean` | `false` | If set, cleans the solution and removes `bin`/`obj` folders before building. |

## Editions: Full vs. Limited (student)

The app ships in two editions, selected by build configuration:

- **Full** (`Debug` / `Release`) — the complete app, including the authoring/constructor
  screens (ECG, Course, OSCE, Test constructors) and the data import/export controls in Settings.
- **Limited** (`Limited`) — the locked-down build handed to end users (students). The four
  constructor operating modes are hidden from the mode picker and keyboard shortcuts, and the
  ECG-data / course-data import & export sections are removed from the Settings dialog.

The edition is a compile-time switch: the `Limited` configuration defines the `LIMITED` symbol,
which `AppEdition.IsLimited` reads. Because it is a compile-time constant, the full-edition entry
points are genuinely absent from the limited binary — there is no runtime toggle to flip.

### Build the limited edition

```powershell
.\tools\build-limited.ps1          # publish the student build to artifacts\publish
.\tools\build-limited.ps1 -Run     # ...and launch it afterwards
```

The output in `artifacts\publish` is packaged by the existing WiX installer exactly like the full
build (the installer harvests that folder and is edition-agnostic), so a limited installer is just
`tools\build-limited.ps1` followed by the usual installer build.

To build the limited edition manually or in Visual Studio, select the `Limited` solution
configuration (equivalent to `dotnet build -c Limited`).

## Time-limited demo builds

A **demo** is a normal build that stops working a fixed number of days after it was built — handed to
a prospect who should evaluate it for, say, 10 / 20 / 30 days and then come back for the full version.

```powershell
.\tools\build-demo.ps1 -Days 30          # 30-day demo, Limited (student) edition, to artifacts\publish
.\tools\build-demo.ps1 -Days 10 -Run     # 10-day demo, then launch it
.\tools\build-demo.ps1 -Days 20 -Full    # time-limit the FULL edition instead of Limited
```

How it works:

- `-Days N` passes `-p:DemoTrialDays=N` into the build. `Version.targets` stamps it into
  `BuildInfo.DemoTrialDays` alongside the existing `BuildInfo.BuildDate` (the UTC build date). A normal
  build ships `DemoTrialDays == 0`, and the whole subsystem is inert — nothing changes for non-demo
  builds.
- At startup `DemoGuard.Evaluate()` computes the window as `BuildDate + DemoTrialDays`. While valid, the
  title bar shows `DEMO — N days left`, and a dismissible reminder pops up in the final few days. Once
  the window passes, the app is **hard-blocked** by a full-window "demo expired" screen (see
  `DemoExpiredOverlay`) whose only action is Exit.
- **Clock-rollback hardening.** A monotonic high-water mark of the latest date the app has seen is kept
  in a small tamper-evident file under `%LOCALAPPDATA%\<brand>\.demostate`. All expiry math uses
  `max(today, high-water mark)`, so winding the system clock back does not buy extra days, and a gross
  backward jump is treated as expired.
- This is **casual** time-limiting, **not** DRM — a determined user who deletes the state file *and*
  rewinds the clock resets the window. Real enforcement needs a license/time server. See the class
  remarks in `src\CardioSimulator.App\DemoGuard.cs`.

The `artifacts\publish` output is packaged by the existing WiX installer exactly like any other build.

### Demos as part of a production run

`tools\build-production.ps1` (the distribution build) emits **four** deliverables: the two perpetual
editions, plus a **10-day demo of each**. A default run produces them side by side:

```powershell
.\tools\build-production.ps1                    # all four, under $OutputRoot\{Full,Light,Demo,FullDemo}
.\tools\build-production.ps1 -Edition Demo      # just the Limited demo
.\tools\build-production.ps1 -Edition FullDemo  # just the Full demo
.\tools\build-production.ps1 -DemoDays 14       # trial length for BOTH demos (default 10)
```

Each deliverable's folder can be redirected on its own with `-FullOutputDir`, `-LightOutputDir`,
`-DemoOutputDir` and `-FullDemoOutputDir` (the script refuses to start if two of the folders it is about
to write are the same, or nested inside one another — each one is wiped before it is published into).

| Folder | Configuration | Trial |
| --- | --- | --- |
| `Full` | `Release` | perpetual |
| `Light` | `Limited` | perpetual |
| `Demo` | `Limited` | `-DemoDays`, default 10 |
| `FullDemo` | `Release` | `-DemoDays`, default 10 |

Both demos are an ordinary edition binary with `-p:DemoTrialDays` baked in, so they behave at runtime
exactly like a `build-demo.ps1` demo: `FullDemo` runs the same build, publish and resource-harvest steps
as `build-demo.ps1 -Full`, but with this script's defaults (**10** days, not `build-demo.ps1`'s 30), its
`-PathologyPak` dataset override, and its `$OutputRoot` folder instead of `artifacts\publish`.
`-DemoDays` applies to both demos; to give them different windows, run the script once per edition
(`-Edition Demo -DemoDays 30`, then `-Edition FullDemo -DemoDays 10`). Build only the perpetual editions
with `-Edition Full` and `-Edition Light` if you want to skip the demos.

Four things to know about a four-deliverable run:

- **The trial clock starts at build time**, not at hand-over: `DemoGuard` counts from the UTC build date
  stamped into the binary, so a demo folder sent out a week after the run has already spent a week of its
  window. The script reads the stamp back after each build and prints the real window — e.g.
  `10-day trial, built 2026-09-23, usable through 2026-10-03`. That date is the **last day the app still
  starts**; it hard-blocks the day after. Rebuild rather than re-sending an old folder.
- **`Full` and `FullDemo` have the same file name and the same feature set**, and differ only in the
  baked trial stamp (and therefore in the auto-incremented build number — `FullDemo` is built first, so it
  carries the lower one). The file listing does not say which is which: launch the exe and read the title
  bar, where a demo appends a localized suffix after the version (English `DEMO — N days left`,
  `DEMO — last day`; see `AppStrings` `demo_title_*`), and an expired one shows a full-window block screen.
  Keep them in their own folders and do not mix up which one goes to a paying customer.
- **All four deliverables share one per-user data folder** on a given machine
  (`%LOCALAPPDATA%\antiAI-ECG-Simulator`, brand-derived), including the demo's `.demostate` high-water
  mark. A demo and a perpetual build installed on the same machine therefore share settings, students and
  results — they cannot be evaluated side by side with independent state.
- **Build order is deliberate.** `Full`/`FullDemo` share `bin\Release` and `Light`/`Demo` share
  `bin\Limited` (they differ only by `-p:DemoTrialDays`), so within each pair the demo is built first and
  the perpetual edition last. Each deliverable builds and publishes as one self-contained step, so what
  lands in the four folders is unaffected either way — but ending a **complete** run on the perpetual
  build keeps a later `dotnet publish --no-build`, a hand-zipped `bin\Release` or `tools\run-last-built.ps1`
  (it launches the newest app exe it finds under `artifacts\` or `bin\`) from picking up a trial-stamped
  exe. A demo-only run
  (`-Edition Demo` / `-Edition FullDemo`) has no paired perpetual build, so it does leave its `bin` tree
  trial-stamped — rebuild before reusing that tree. Visual Studio is unaffected either way: the solution
  maps this project to x86, so F5 builds into `bin\x86\<Configuration>`, which this script never touches.

## Protected content packs

The bundled dataset (ECG pathology `.dat` files and course `.html`/assets) ships **encrypted** so
end users cannot open it with an archiver or copy loose files out of the app-data folder. This
applies to **both** editions (Full and Limited).

> **The pack-authoring tooling is not in this repo.** `ContentPacker` and the `.pak` scripts are shared
> with the other platform repos, so they sit one level up in `E:\VLN_Project\CardioSimulator\Tools\`
> (`..\Tools\` from here, a sibling of `Win\`): `pack-content.ps1`, `build-pathology-packs.ps1`,
> `stamp-pack-revisions.ps1`, `build-course-converter.ps1`, `convert-courses-to-pak.ps1`,
> `pack-data-zips.ps1` and the Python helpers. Each one resolves `ContentPacker`, its sibling helpers
> and this repo from its own location, so the commands in this section work from any directory. Only
> the build scripts (`tools\*.ps1`) live in this repo.

- The app ships `Assets\Pathologies.pak` and `Assets\Courses.pak` — AES-256-GCM containers
  (`ContentCrypto`) — **instead of** the plaintext ZIPs. The `.csproj` copies only the `.pak` files
  into the build output.
- At runtime the packs are decrypted **into memory only** (`EncryptedArchive` +
  `Encrypted{Pathology,Course}Source`) and never extracted to disk. Course assets and rendered
  lectures are served to the WebView from memory (`LectureWebView` `WebResourceRequested`), so no
  plaintext lands in `%LOCALAPPDATA%` or `%TEMP%`.
- Packs are **read lazily**. A `CSP2` pack's plaintext is framed into 64 KiB chunks, each encrypted
  under its own nonce and tag, so `ZipArchive` sits on a seekable decrypt-on-demand stream
  (`ChunkedPack`) and only the chunks a read actually touches are decrypted. The 1.7 GB / 45k-record
  pack loads in **~380 MB of working set** and holds ~42 MB of managed memory, versus ~3.4 GB peak
  and 1.67 GB resident under the old whole-pack `CSP1` container — which also capped a pack at
  `Array.MaxLength` (~2 GB). There is no size ceiling now.
- `CSP1` packs still open (whole-buffer, as before) so anything already distributed keeps working.
  Migrate one with `ContentPacker repack <in.pak> <out.pak>` — entry bytes are copied verbatim.

### Giving a customer their courses in the new format

A customer holding an old plaintext `Courses.zip` (the app no longer accepts ZIPs) converts it with:

```
Convert courses to pak.cmd      <- drag the ZIP onto it, or double-click and it asks
convert-courses-to-pak.ps1      <- the same thing from a terminal
```

The `.cmd` wrapper exists because Windows blocks `.ps1` by default and double-clicking one opens
Notepad; it bypasses execution policy **for that run only**. The script identifies the file by its
magic rather than its extension, so it also upgrades an older `CSP1` course pack, refuses an ECG
pack with a plain-English message, converts via a temp file that is only moved into place once it
verifies, and confirms by reading the result back through the app's own `EncryptedCourseSource`.

To produce the folder you actually send, run:

```powershell
..\Tools\build-course-converter.ps1 -Zip
```

It publishes `ContentPacker` **self-contained and single-file** (~34 MB, no .NET runtime needed on
the customer's machine — they will not have one), stages it next to the two scripts plus a
plain-language `README.txt` into `..\artifacts\course-converter\`, and with `-Zip` also emits
`..\artifacts\course-converter.zip` ready to send. Before reporting success it smoke-tests the staged
bundle: it runs the published exe, and converts a real courses ZIP through the staged script exactly
as the customer will. That output sits beside `Tools\`, outside this repo.
- This is casual-copy protection, **not** unbreakable DRM: the decryption key is assembled inside
  the binary (`ContentCrypto.Secret`). Pair with a binary obfuscator to raise that bar further.

### Regenerating the packs

The `.pak` files are generated artifacts (git-ignored, like the source ZIPs). After changing the
dataset, regenerate them from the plaintext ZIPs in `Assets\`:

```powershell
..\Tools\pack-content.ps1
```

For a real student distribution, first replace `Assets\Pathologies.zip` / `Assets\Courses.zip` with
the **full** dataset, then run `pack-content.ps1`, then build. The offline packer lives at
`..\Tools\ContentPacker` (`pack` / `binarize` / `pack-dir` / `repack` / `prune` / `add` /
`apply-acronyms` / `stamp-revisions` / `verify` / `cat` / `inspect-pathologies` /
`inspect-courses` subcommands) and shares `CardioSimulator.Core` so the pack format can never drift
from the runtime.

### Delta-binary waveforms and the large-dataset pipeline

Inside a pack, each `<id>.dat` waveform is stored not as the plaintext `points:1024,1024,…` text but
as a compact **`CSD1` delta-binary** blob: samples are 16-bit deltas from the previous sample, which
zips far smaller (≈27 % on the real arrhythmia data, ≈64 % on the smooth built-in library). Loose
files on disk stay plain text for editing; the reader auto-detects each entry by its 4-byte `CSD1`
magic, so text and binary `.dat` can coexist in one pack. The format is defined once in
`PathologyParser` (`ParsePathology(byte[])` / `SerializePathologyBytes`), shared by the runtime and
the packer.

For the huge arrhythmia dataset (tens of thousands of records, multi-GB) build the size-variant packs
straight from the **loose master directory** — the plaintext master ZIPs are no longer needed:

```powershell
# Binarize the master once, then build 500 / 5000 / 10000 / 30000 / All packs from it.
..\Tools\build-pathology-packs.ps1 -MasterDir E:\VLN_Project\Data\Pathologies.All.regrouped
```

The script (1) runs `ContentPacker binarize <masterDir> <masterDir>.bin` — compiling every text
`.dat` to `CSD1` one file at a time (constant memory, so the full ~45k-record set is fine); then, per
size, (2) `subset_pathologies.py --in-dir <bin> --out-manifest <tmp> --target N` picks a
group-balanced subset by reading **only** `manifest.txt` (never the `.dat` bytes), and (3)
`ContentPacker pack-dir <bin> <out.pak> --manifest <tmp>` zips **and** encrypts the selected `.dat`
files in one streaming pass directly into the `.pak` — no temporary plaintext ZIP on disk, and no
step ever needs the whole dataset in one in-memory buffer. `pack-dir` with no `--manifest` packs the
entire directory (the "All" pack).

> The older `pack-data-zips.ps1` (plaintext ZIP → pak) is superseded by this binary-first pipeline
> and kept only for one-off small ZIPs (e.g. courses).

### Stamping rhythm revisions (last step)

Each rhythm can carry a short content id so two builds are comparable rhythm by rhythm:
`;revision:<hex>` on its `manifest.txt` line and `revision:<hex>` in its `.dat` header (documented in
`docs\data-structure.md` §2.7). The value is the first 6 hex characters of SHA-256 over that rhythm's
own bytes, which makes it idempotent, sensitive to any edit of that rhythm alone, and identical for
the same rhythm in every pack built from one master.

```powershell
# Dry run first (per-pack *.revisions.tsv reports, nothing modified), then stamp in place.
..\Tools\stamp-pack-revisions.ps1 -PackDir E:\VLN_Project\CardioSimulator\Data\CurrentPackages
..\Tools\stamp-pack-revisions.ps1 -PackDir E:\VLN_Project\CardioSimulator\Data\CurrentPackages -Replace
```

The runner wraps `ContentPacker stamp-revisions <in.pak> <out.pak> [--length 6] [--value V | --map F]
[--report F] [--dry-run]`, which streams a pack entry by entry and rewrites **only** the manifest text
and each `.dat`'s length-prefixed `CSD1` header — waveform bytes are copied untouched, and each output
is round-trip verified through the runtime read path.

Run it **after** everything else: `binarize`, `pack`, `apply-acronyms` and constructor saves all
re-serialize a rhythm through `PathologyParser`, which drops header keys it does not know — and
`revision` is currently one of them (unknown keys are ignored on read, so stamped packs load exactly
like unstamped ones). Re-stamp after any rebuild until the parsers carry the field themselves.

### Authoring on a pack build (encrypted overlay)

In the **Full** edition, a protected pack build is still editable: the constructor writes go to an
**encrypted writable overlay** layered over the read-only pack (copy-on-write). Reads merge
overlay-over-pack; editing a bundled item creates an override, deleting one records a tombstone, and
the pack itself is never mutated. The overlay is a single AES-256-GCM file per dataset
(`%LOCALAPPDATA%\antiAI-ECG-Simulator\overlay\pathologies.pak` / `courses.pak`) — it MUST stay
encrypted because duplicating/editing a bundled item copies decrypted bundle content into it, so a
plaintext overlay would let "duplicate everything" reconstruct the whole dataset. See
`Overlay{Pathology,Course}Source` + `WritableEncryptedOverlay` + `IWritable{Pathology,Course}Source`.

That folder name is brand-derived, not literal: `AppPaths.Root` is `%LOCALAPPDATA%\{BuildInfo.DataFolder}`,
stamped from `<AppBrandDataFolder>` in `Directory.Build.props` — `antiAI-ECG-Simulator` today. The older
`CardioSimulator` folder is only `<AppBrandLegacyDataFolder>`, moved across once on first run (see the
`AppPaths` static constructor), so it survives on upgraded machines but is never the current location.
The unsuffixed `pathologies.pak` / `courses.pak` stay reserved for the **bundled** pack; a user-picked
pack gets its own overlay in the same folder, keyed by that pack's own identity —
`pathologies-<key>.pak` / `courses-<key>.pak`, from `AppPaths.PathologyOverlayPakFor` /
`CourseOverlayPakFor` — so switching packs never replays one pack's edits and tombstones onto
another's ids.

The **Limited** edition has no constructor, so it never creates an overlay — the pack stays strictly
read-only for maximum protection.

Alternatively, author against plaintext files: point the app at a data folder/ZIP via Settings →
Change (a saved data source takes precedence over the bundled pack), edit, then re-run
`..\Tools\pack-content.ps1`.

### Export and TCP in pack mode

- **Export** (Full-edition Settings): in pack mode, exporting the dataset produces an encrypted
  `.pak` of the current merged view (base + overlay), built in memory — never a plaintext ZIP of the
  bundle. In file/author mode it still exports a plaintext ZIP of the on-disk data.
- **TCP dataset upload is disabled in pack mode.** The TCP target/Connect controls are visible even
  in the Limited edition, so auto-uploading the dataset would let anyone point the app at their own
  socket and receive it — an exfiltration hole. Only the command channel (start/stop by id/name)
  stays live. (The upload also no longer writes a plaintext zip to `%TEMP%` in any mode — it streams
  from memory.)
