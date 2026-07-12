# course-reorg

Reorganize a CardioSimulator course bundle from the flat lecture list into the
**topic (Тема / Подтема)** format, driven by a declarative plan.

The course format is backward compatible (see `docs/course-format.md` and
`Core/Domain/CourseParser.cs`): a course is a flat list of `lecture:` lines, and an
optional set of `topic:` lines groups them. This tool adds that grouping to an
existing bundle without re-authoring any lecture content.

## Usage

```sh
# ZIP in -> ZIP out (default output: <in>.reorg.zip)
python reorg_courses.py --in Courses.zip
python reorg_courses.py --in Courses.zip --out Courses.new.zip

# Preview only — reports every change, writes nothing
python reorg_courses.py --in Courses.zip --dry-run

# Operate on the app's extracted courses folder instead of a zip
python reorg_courses.py --in "%LOCALAPPDATA%\CardioSimulator\courses" --in-place
python reorg_courses.py --in <courses-dir> --out <new-dir>

# Use a different plan file
python reorg_courses.py --in Courses.zip --plan my_plan.json
```

Only Python 3 standard library is used. Text the tool writes is UTF-8, no BOM, LF;
every file it doesn't change is copied byte-for-byte. **Courses not named in the plan
are left exactly as they were** (they keep their flat lecture list, which the app
still renders).

## The three course shapes

| Shape | course.txt | UI |
|-------|-----------|----|
| Flat | `lecture:` lines only | plain lecture dropdown |
| Group Тема | `topic:<id>;…` + lectures with `;topic:<id>` | Тема dropdown → Подтема dropdown |
| Leaf Тема | `topic:<id>;…;leaf:true` (its own content) | Тема dropdown, opens directly |

A **leaf** Тема's HTML lives at `lectures/<topicId>.<lang>.html`. Point `content_from`
at the lecture whose files become that content: if it equals the topic id the files
are reused as-is; otherwise they're renamed (and their front-matter `id:` patched).

## Plan schema (`reorg_plan.json`)

```jsonc
{
  "courses": {
    "<course-id>": {
      "delete_topics":   ["<junk-topic-id>", ...],     // optional; removes the topic line
      "delete_lectures": ["<junk-lecture-id>", ...],   // optional; removes the lecture + its files
      "topics": [
        { "id": "bbb", "title": "Bundle Branch Blocks", "name": "Блокады ножек пучка Гиса" },
        { "id": "wpw", "title": "Pre-excitation (WPW)", "name": "Преэкзитация (WPW)",
          "leaf": true, "content_from": "03-wpw" }
      ],
      "assign": {                                       // lecture id -> group topic id
        "01-rbbb": "bbb",
        "02-lbbb": "bbb"
      }
    }
  }
}
```

- `topics` — the Темы to create, in dropdown order. `name` (RU) is optional; omit
  `leaf` for a group. A `leaf` topic needs `content_from` (defaults to its own id).
- `assign` — files each surviving lecture under a **group** topic. Lectures left out
  keep a still-valid topic, else become ungrouped (and are reported).
- Ids in `delete_*` that aren't present are skipped harmlessly, so one plan works on
  both a pristine bundle and a junked-up extracted folder.

`manifest.txt`'s `lectures:` count for each reorganized course is updated to the
number of content items (lectures + leaf Темы).

## Validation

The run reports, per course: deletions, leaf content mapping, the resulting
topic/lecture/content counts, the manifest update, plus warnings for:
- a lecture assigned to a non-group (or missing) topic,
- a leaf topic with no `lectures/<id>.<lang>.html` content file,
- lectures left ungrouped.

`--dry-run` prints all of this and writes nothing — always preview first.

## Bundled plan

`reorg_plan.json` covers all six faculty courses in `E:\VLN_Project\Data\Courses.zip`
plus the bundled `cardio-101` sample. Each split is a clinically natural group Тема
(two related lectures) + a leaf Тема for the standout third:

| Course | Group Тема (Подтемы) | Leaf Тема |
|--------|---------------------|-----------|
| av-blocks | Incomplete AV Block (1°, 2°) | Third-Degree AV Block |
| mi-localization | Regional Infarction (anterior, inf/post) | Special Patterns |
| tachyarrhythmias | Narrow- & Wide-Complex (narrow, wide) | Malignant Ventricular Rhythms |
| chamber-enlargement | Ventricular Enlargement (ventricular, biventricular) | Atrial Enlargement |
| electrolyte-metabolic | Electrolyte Disturbances (K, Ca) | Hypothermia & Long-QT |
| conduction-preexcitation | Bundle Branch Blocks (RBBB, LBBB) | Ventricular Pre-excitation (WPW) |
| cardio-101 (bundled sample) | ECG Interpretation (Электрическая ось) | Введение |

`conduction-preexcitation` also removes leftover test junk. These groupings are a
sensible starting point — edit titles/ids or re-split to taste, then re-run.
