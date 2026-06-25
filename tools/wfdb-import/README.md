# wfdb-import

`add_wfdb.py` — bulk-import PhysioNet **WFDB** ECG records (`.hea` + `.mat`/`.dat`)
into the app's bundled `Pathologies.zip`. It is the offline / batch counterpart of
the in-app **Import WFDB file…** button, and a faithful port of the C# pipeline
(`WfdbHeaderParser` → `WfdbSignalCodec` / `MatlabLevel4` → `WfdbConverter`).

Stdlib only (no `pip install`), like the other tools in `tools/`.

## What it does

For each record it:
1. parses the `.hea`, groups signals by file, and decodes raw ADC samples
   (formats **16, 61, 80, 212, 24, 32** and MATLAB v4 `.mat`);
2. keeps every signal whose description names a known lead
   (I, II, III, aVR, aVL, aVF, V1–V6 — case-insensitive, first-wins; PLETH/RESP/… skipped);
3. rescales into the app domain — `sample = round(1024 + ((raw − baseline) / gain) × 256)`
   (baseline-centred, 256 counts/mV — see `EcgCalibration`);
4. writes a new `<id>.dat` and a `manifest.txt` entry, bumping the manifest's
   `pathologies` / `total_lead_streams` / `total_samples` counts. `groups.txt` is preserved.

Output is **UTF-8 without a BOM, LF** (a BOM breaks the app's first-line `version:` parse).

By default it reads each decoded signal back and checks the **initial value** and
**checksum** against the header (the same ground-truth assertion the Core tests make),
printing `WARN` on any mismatch. Use `--no-verify` to skip, `--strict` to fail.

## Usage

Run it from the repo root (`E:\VLN_Project\CardioSimulatorWin`) and reference the
tool by its path, e.g. `python tools/wfdb-import/add_wfdb.py …`. On Windows, prefix
with `PYTHONUTF8=1` (Git Bash) or `set PYTHONUTF8=1` (cmd) so the Cyrillic/UTF-8
console output doesn't error:

```sh
PYTHONUTF8=1 python tools/wfdb-import/add_wfdb.py --help
```

```sh
# one record (resolved under --dataset), into a fresh copy of the asset zip
python add_wfdb.py --dataset E:/VLN_Project/Data/010 JS00001 --group sinus

# a curated batch with titles/names/groups from a TSV
python add_wfdb.py --dataset <chapman-root> --records wfdb_records.tsv \
    --out E:/VLN_Project/Data/Pathologies.new.zip

# preview without writing
python add_wfdb.py JS00001.hea --dry-run
```

| flag | meaning |
|------|---------|
| `--in`      | source zip (default: `src/CardioSimulator.App/Assets/Pathologies.zip`) |
| `--out`     | destination zip (default: `<in dir>/Pathologies.new.zip`; must differ from `--in`) |
| `--dataset` | base dir for resolving bare record names (searched recursively) |
| `--records` | TSV of records + metadata (see `wfdb_records.tsv`) |
| `--all`     | import **every** `.hea` under `--dataset` (see scale warning below) |
| `--group`   | default group key for records lacking one |
| `--limit N` | cap the number of records imported |
| `--no-verify` / `--strict` | relax / tighten the integrity check |
| `--dry-run` | parse + convert + report, write nothing |

Positional args and `--records` rows combine; `--records` rows come first.

## Importing the whole dataset (`--all`)

```sh
# every record under the dataset, full 12-lead / full-length signals
python add_wfdb.py \
    --dataset E:/VLN_Project/Data/a-large-scale-12-lead-electrocardiogram-database-for-arrhythmia-study-1.0.0 \
    --all --group arrhythmia \
    --out E:/VLN_Project/Data/Pathologies.all.zip
```

`--all` enumerates every `.hea` under `--dataset` and imports the **entire** signal
of each (all 12 leads, full length — nothing is truncated). It streams each `.dat`
straight into the zip, so memory stays flat no matter the count.

**Scale warning — this dataset has ~45,000 records.** Measured on this machine:
- **~7 records/sec** (disk-bound) → a full run takes **~1.5–2 hours**;
- **~67 KB compressed per record** → a **~3 GB** output zip;
- every entry is titled `JSxxxxx` and lands in one `--group` (no per-record diagnosis
  mapping), so the in-app rhythm drawer would show 45k near-identical entries.

For a teaching build you almost certainly want a **curated subset** instead — e.g.
cap the count and/or hand-pick records with real titles/groups via `--records`:

```sh
# first 300 records as a sample
python add_wfdb.py --dataset <root> --all --limit 300 --group arrhythmia

# preview the full run without writing (also prints any skipped records)
python add_wfdb.py --dataset <root> --all --dry-run
```

Records that aren't clean 12-lead (or have unreadable signal files) are skipped with
a `SKIP`/`WARN` line and the run continues; use `--strict` to abort on the first one.

## Worked examples

Real commands used against this machine's layout (run from the repo root). The
arrhythmia dataset root is abbreviated below as `$DS`:

```sh
DS="E:/VLN_Project/Data/a-large-scale-12-lead-electrocardiogram-database-for-arrhythmia-study-1.0.0"
```

```sh
# Build a 600-record sample on top of an existing dataset zip (Pathologies.new.zip)
# and save it next to it as Pathologies.600.zip.
#   -> 56 base + 600 imported = 656 pathologies, ~33 MB, ~2 min.
PYTHONUTF8=1 python tools/wfdb-import/add_wfdb.py \
    --in  "E:/VLN_Project/Data/Pathologies.new.zip" \
    --dataset "$DS" \
    --all --limit 600 \
    --out "E:/VLN_Project/Data/Pathologies.600.zip"
```

```sh
# Same, but tag the imported records into a group instead of leaving them ungrouped
# (otherwise all 600 land in the app's "Other" bucket).
PYTHONUTF8=1 python tools/wfdb-import/add_wfdb.py \
    --in  "E:/VLN_Project/Data/Pathologies.new.zip" \
    --dataset "$DS" \
    --all --limit 600 --group arrhythmia \
    --out "E:/VLN_Project/Data/Pathologies.600.zip"
```

```sh
# Preview the same selection without writing (also lists any skipped records).
PYTHONUTF8=1 python tools/wfdb-import/add_wfdb.py \
    --in "E:/VLN_Project/Data/Pathologies.new.zip" --dataset "$DS" \
    --all --limit 600 --dry-run
```

```sh
# Add a few hand-picked records (by name, searched under --dataset) to the live asset.
PYTHONUTF8=1 python tools/wfdb-import/add_wfdb.py \
    --dataset "$DS" JS00001 JS00002 JS00010 \
    --group arrhythmia \
    --out "E:/VLN_Project/Data/Pathologies.sample.zip"
```

To trim the progress output, append `2>&1 | tail -20` — it doesn't affect the result.

## Deploying the new zip

The tool never overwrites the live asset — it writes to `--out`. To ship the
result, copy it over the asset and the two artifact copies (see
`pathology-groups/README`-style note in memory). Existing installs must delete
`%LOCALAPPDATA%\CardioSimulator\pathologies` to pick up the additions, since the
app reuses a valid existing extraction.

See also `tools/pathology-groups/` (group tagging) — the zip format is identical.
