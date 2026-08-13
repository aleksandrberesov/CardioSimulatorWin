# taxonomy-build

Generates the canonical ECG **acronym taxonomy** (`Taxonomy.tsv`) — the single join spine that wires
rhythms ⇄ lectures ⇄ test/exam questions ⇄ student results together.

## What it is

`build_taxonomy.py` reads the three customer source tables (under `CardioSimulator/Docs/`) and emits one
normalized row per acronym:

| column | meaning |
| --- | --- |
| `acronym` | canonical code, upper-case (`AFIB`, `2AVB1`, `MI_ANT`) — the primary key |
| `name_ru` | Russian display name |
| `group` | rhythm-group key, reusing the app's `groups.txt` vocabulary (`sinus`, `conduction`, …) |
| `section` | top-level course section «Раздел N», derived from `subsection` so it can't disagree |
| `subsection` | primary course node, e.g. `4.6.2` |
| `subsection_title` | localized (RU) subsection title |
| `alt_subsections` | `;`-joined extra nodes for multi-mapped acronyms (e.g. `WPW` → `8.1`) |

## Sources

1. `таблица соответсвия общая.txt` — master rows (acronym → group / subsection / section)
2. `Группировка по подразделам.txt` — reverse index (subsection → acronyms), used as a cross-check
3. `Акронимы инфарктов (детальная привязка).txt` — the `MI_*` family under 6.3 (absent from table 1)

## Run

```bash
python tools/taxonomy-build/build_taxonomy.py
```

Emits `tools/taxonomy-build/taxonomy.tsv` (for inspection) **and**
`src/CardioSimulator.Core/Domain/Taxonomy.tsv` (embedded into the Core assembly and loaded at runtime
by `Taxonomy.Shared`). The script prints per-section counts and warns on any acronym the grouping table
names but the output is missing.

## When to re-run

Whenever the customer updates any of the three source tables. Do **not** hand-edit `Taxonomy.tsv` —
edit the sources and regenerate so all three views stay consistent.

---

## Auto-tagging the bundled rhythms (`build_rhythm_acronyms.py`)

Tags the shipped pathology pack so every rhythm carries its taxonomy findings as `acronym:` (a
comma-separated list, **primary first**). The records' titles are compound finding-lists; each finding
maps to an acronym (span-containment drops sub-phrase noise), and the record's authored `group:` picks
the primary one (the mapped finding whose taxonomy group matches the record group; else the most
clinically significant). Output: `rhythm_acronyms.tsv` (`ecgNNNNN → acr1,acr2,…`).

```bash
# 1. dump the pack's manifest
dotnet run --project tools/ContentPacker -- cat src/CardioSimulator.App/Assets/Pathologies.pak manifest.txt > manifest.txt
# 2. generate the id→acronym map
python tools/taxonomy-build/build_rhythm_acronyms.py manifest.txt > tools/taxonomy-build/rhythm_acronyms.tsv
# 3. write acronyms into the pack (manifest + .dat headers), round-trip verified
dotnet run --project tools/ContentPacker -- apply-acronyms \
    src/CardioSimulator.App/Assets/Pathologies.pak \
    tools/taxonomy-build/rhythm_acronyms.tsv \
    Pathologies.tagged.pak
# then replace the shipped pak with the tagged one
```

Current coverage: **499 / 500** rhythms tagged, 986 codes (avg 2.0/rhythm); only *Biventricular
hypertrophy* is untagged (no combined code).
