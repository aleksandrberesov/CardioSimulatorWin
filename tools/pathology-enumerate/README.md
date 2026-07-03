# pathology-enumerate

Assigns a 1-based `number` to every pathology in a `Pathologies.zip` **and renames
each pathology to `ecg{number}`**.

The app surfaces the number in **clinical case mode**:

- the rhythm list shows rows as `{number} <case title>`;
- the clinical dashboard header reads `Clinical case №{number}`.

`number` is stored as a `number:` line in each `<id>.dat` header and as a
`;number:<n>` field on the matching `pathology:` line in `manifest.txt`.

## Renaming

Each pathology is renamed to `ecg{number}`, with the number **zero-padded to a uniform
width** so every id has the same digit count (e.g. a 10000-record set yields `ecg00001`
.. `ecg10000`). The width defaults to the digit count of the largest number; raise it
with `--width`. The `number:` field itself stays a plain integer — only the id/filename
is padded.

- the `.dat` entry becomes `ecg{0-padded number}.dat`,
- its header `pathology:` id becomes `ecg{0-padded number}`,
- the manifest `pathology:` line is renamed to match.

The id and filename change **together** because the app locates a pathology's file
as `<id>.dat` — the manifest has no separate filename field. The tool prints the full
`old-id -> new-id` mapping and also writes it to a **map file** (see below).

> ⚠️ **Ids change.** Any Course/Test data (e.g. `Courses.zip`) that references the old
> pathology ids must be updated to the new `ecg{number}` ids, and a user's
> "last selected rhythm" preference will no longer resolve (it falls back gracefully).
> Use `update_courses.py` (below) to re-point a `Courses.zip`.

## `enumerate_pathologies.py`

```sh
python enumerate_pathologies.py --in Pathologies.zip
```

With `--out` omitted the output defaults to the input path with an `.enumerated`
suffix (e.g. `Pathologies.zip` → `Pathologies.enumerated.zip`), and the map file to
`Pathologies.enumerated.rename-map.tsv`. Pass `--out` to choose your own path.

Options:

- `--out FILE` — destination zip (default: `<in>` with an `.enumerated` suffix).
- `--start N` — first number to assign (default `1`).
- `--prefix P` — id/filename prefix (default `ecg`, i.e. `ecg{number}.dat`).
- `--width N` — minimum zero-padded digit width for ids (default: auto = digits of the
  largest number).
- `--renumber` — ignore existing numbers and renumber every pathology from scratch,
  in manifest order.
- `--map-out PATH` — where to write the rename map (default `<out>.rename-map.tsv`).

By default existing numbers are **preserved**; only `.dat` files that lack a number
are assigned the next free value. Numbering order follows `manifest.txt`; any `.dat`
not listed there is appended alphabetically. Re-running the tool is idempotent
(byte-identical output).

### Rename map file

A TSV written alongside the output zip, one row per pathology, ordered by number:

```
# old_id<TAB>new_id<TAB>number
afib    ecg1    1
sinus   ecg2    2
```

## `update_courses.py`

Re-points a `Courses.zip` at the renamed ids using the map file. Courses reference
pathology ids in three places, all rewritten:

- the courses `manifest.txt` (`;pathologies:<csv>` on each `course:` line),
- each `<course>/course.txt` (`pathologies:<csv>` header line),
- each lecture `<course>/lectures/*.html` (`pathology="<id>"` on `<ecg>` embeds).

```sh
python update_courses.py --in Courses.zip --out Courses.new.zip --map Pathologies.new.rename-map.tsv
```

Remapping is a single pass over known old ids, so renames never cascade (a map of
`{aaa->ecg1, ecg1->ecg2}` turns `aaa,ecg1` into `ecg1,ecg2`, not `ecg2,ecg2`). Ids
referenced by a course but absent from the map are left unchanged and reported as a
`WARNING`. The nested zip layout and all non-course entries are preserved verbatim.

Both tools write UTF-8 **without a BOM** (a BOM breaks the app's first-line
`version:`/front-matter parse) with LF line endings, matching
`pathology-groups/add_groups.py`.
