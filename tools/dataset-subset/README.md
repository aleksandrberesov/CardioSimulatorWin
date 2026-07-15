# dataset-subset

`subset_pathologies.py` builds a **small, group-balanced subset** of a
CardioSimulator `Pathologies.zip`.

The full arrhythmia dataset has tens of thousands of records and is heavily
skewed (e.g. ~25 000 `sinus` vs. 3 `pacemaker`). For quick builds, demos, tests
or teaching you want a small dataset that still exercises **every** group. The
tool subsamples so that:

- **every group in the source is still present** in the subset;
- each group keeps a **proportional share** (`--fraction`), so the subset
  mirrors the original balance;
- **no group drops below a floor** (`--min-per-group`, default **4**) — tiny
  groups are kept whole instead of being sampled away to nothing;
- an optional **cap** (`--max-per-group`) stops a giant group from dominating.

The output is a valid `Pathologies.zip`: `groups.txt` is copied verbatim (so the
app still lists all groups), the selected `<id>.dat` files are copied byte-for-
byte, and `manifest.txt` is rebuilt with only the kept entries and recomputed
`pathologies` / `total_lead_streams` / `total_samples` counts. Output is UTF-8
**without a BOM** with LF endings, matching the rest of the dataset.

## Usage

```sh
# 10% of every group, floor 4, from the full grouped dataset
python subset_pathologies.py \
    --in  E:/VLN_Project/Data/Pathologies.All.grouped.zip \
    --out E:/VLN_Project/Data/Pathologies.subset.zip
```

With `--out` omitted the output defaults to the input path with a `.subset`
suffix (`Pathologies.zip` → `Pathologies.subset.zip`). With `--in` omitted it
defaults to the bundled `src/CardioSimulator.App/Assets/Pathologies.zip`.

### Target a total count instead of a fraction

```sh
# aim for ~600 records total: every group gets its floor of 4 first, then the
# remaining budget is spread proportionally by group size, capped at 150/group.
python subset_pathologies.py --in Pathologies.All.grouped.zip \
    --target 600 --min-per-group 4 --max-per-group 150
```

### Preview only

```sh
python subset_pathologies.py --in Pathologies.All.grouped.zip --dry-run
```

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `--in FILE` | bundled Assets zip | source `Pathologies.zip` |
| `--out FILE` | `<in>.subset.zip` | destination zip |
| `--fraction F` | `0.10` | keep ~F of each group (`0 < F <= 1`) |
| `--target N` | — | aim for ~N records total (overrides `--fraction`) |
| `--min-per-group M` | `4` | minimum kept per group **where available** |
| `--max-per-group X` | — | optional per-group cap |
| `--select MODE` | `random` | `random` (seeded) · `stride` (evenly spaced) · `head` |
| `--seed S` | `0` | RNG seed for `--select random`; results are reproducible |
| `--drop-ungrouped` | off | drop records with no group (default: keep & sample them) |
| `--balance-clinical` | off | also stratify by clinical vs non-clinical within each group |
| `--dry-run` | off | print the plan, write nothing |

### Keeping the non-clinical records represented (`--balance-clinical`)

Most records are **clinical cases** — patient-derived, carrying a `clinical_case`
field (age/gender/diagnosis). A small set are **non-clinical**: the hand-authored
built-in library (the same records as the bundled `Assets/Pathologies.zip`),
which have no `clinical_case`. In the full dataset the non-clinical share is tiny
(~0.1 %), so plain per-group sampling almost wipes them out.

With `--balance-clinical` the sampling unit becomes **(group × clinical-flag)**
instead of the group alone. Each stratum gets the usual proportion + floor, so
the built-in non-clinical records stay represented in every group they appear in,
while the clinical cases still keep their group proportions. The report gains a
`class` column and a clinical/non-clinical rollup.

```sh
python subset_pathologies.py --in Pathologies.All.grouped.zip \
    --target 500 --balance-clinical
```

Notes:

- A group with fewer records than `--min-per-group` (e.g. `pacemaker` with 3) is
  **kept whole** — the floor never invents records that don't exist.
- Selection is deterministic for a given `--seed`, so re-running produces the
  same subset. `stride` gives a floor-to-ceiling spread without an RNG; `head`
  takes the first N in manifest order.
- Manifest entries with no matching `.dat` are ignored and reported; orphan
  `.dat` files (no manifest entry) are never copied.
```
