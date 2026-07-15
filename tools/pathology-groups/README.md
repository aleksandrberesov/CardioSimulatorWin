# pathology-groups

Tools for assigning the rhythm-list **group** key (`sinus`, `arrhythmia`,
`conduction`, `hypertrophy`, `ischemia`, `infarction`, `electrolyte`,
`syndromes`, `pacemaker`, `special`, `pediatric`, `newborn`, `pregnant`,
`clinical`) to every pathology in a CardioSimulator dataset ZIP.

The group is written in two places and both must agree:
* `manifest.txt` — each `pathology:` line carries `;group:<key>`
* every `ecgNNNNN.dat` header — a `group:<key>` line right after `pathology:`

The catalog of groups (keys + localized names, display order) lives in
`groups.txt`, which is bundled into the ZIP and read by the app.

## `regroup_from_report.py` — regroup from the Combined Pathologies Report

Makes the report Excel the **single source of grouping** for the large dataset
(`Pathologies.All.corrected.grouped.enumerated.fixed.zip`).

Every pathology title in that dataset is a `" + "`-joined list of condition
names, e.g. `Sinus Bradycardia + Left ventricular hypertrophy + ST segment changes`.
The report's `Справочник состояний` sheet maps each condition (English name) to a
Russian group label; this tool:

1. reads that sheet → `condition → group key` (label→key table is in the script),
   extended by `condition_aliases.tsv` for title wording not verbatim in the Excel
   (synonyms, typos, raw SNOMED codes);
2. splits each title into components and maps each to a group key;
3. picks **one** group per pathology by a configurable clinical-severity priority —
   for a multi-condition record, the highest-priority group among its components wins;
4. rewrites the `group:` field in `manifest.txt` and every `.dat` header **in place**
   (field order preserved);
5. a title with **no** mappable component keeps its **existing** group (the hand-curated
   built-ins ecg00001–ecg00056 and other one-off legacy titles are never downgraded).

**Dry run by default** — with no `--out` it reads only `manifest.txt` (seconds),
prints the old→new group distribution, lists any unmatched title components, and
writes a per-record change report (`regroup-changes.tsv`). Pass `--out` to also
stream a full regrouped ZIP. `--out` must differ from `--in` (the source is never
overwritten).

```bash
# analyse only (fast): impact + change report, source zip untouched
python tools/pathology-groups/regroup_from_report.py \
    --in     E:/VLN_Project/Data/Pathologies.All.corrected.grouped.enumerated.fixed.zip \
    --report E:/VLN_Project/Data/Combined_Pathologies_Report_2026-07-10.xlsx

# also write the regrouped dataset
python tools/pathology-groups/regroup_from_report.py \
    --in     .../fixed.zip \
    --report .../Combined_Pathologies_Report_2026-07-10.xlsx \
    --out    E:/VLN_Project/Data/Pathologies.All.regrouped.zip
```

Useful flags: `--sheet` (condition sheet name), `--aliases` (alias TSV),
`--priority a,b,c,…` (override the tie-break order), `--report-out` (change-report
path), `--compresslevel 0-9`. Requires `openpyxl` (`pip install openpyxl`) to read
the `.xlsx`.

On the current data: 45,166 of 45,206 pathologies are regrouped straight from the
report, the 40 legacy built-ins keep their curated group, **0** are left ungrouped
(including the 674 that were previously ungrouped), and ~7,130 change group.

### Tuning
* **Group priority** — a multi-condition record's group is the highest-priority one
  present. The default (`DEFAULT_PRIORITY` in the script) mirrors the established
  convention: `infarction > ischemia > electrolyte > syndromes > conduction >
  pacemaker > arrhythmia > hypertrophy > sinus > special`. Change it with `--priority`.
* **Unmatched components** — the dry run lists them with counts; add the recurring
  ones to `condition_aliases.tsv` (`<title component, lower-case><TAB><group key>`).

### Deployment
Copy the output over `src/CardioSimulator.App/Assets/Pathologies.zip` (and the
`artifacts/*/Assets` copies) as usual. Existing installs must delete
`%LOCALAPPDATA%\CardioSimulator\pathologies` to force a re-extract; fresh installs
seed from the new zip automatically.

## Other tools here
* `update_zip_groups.py` — assigns groups by reading the **source WFDB `.hea`** `#Dx:`
  SNOMED codes (for `jsNNNNN`-style records), plus `pathology_groups.tsv` for the
  built-ins. Use when working from the raw PhysioNet dataset rather than the report.
* `add_groups.py` — writes groups into the small curated `Pathologies.zip` from
  `pathology_groups.tsv` + `groups.txt`.
