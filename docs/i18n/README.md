# Treatment mode («Лечение») — translation worklist

`treatment-mode-translation.tsv` lists every user-facing string in the Treatment /
resuscitation mode that currently exists **only in English and Russian**. In Chinese,
Spanish, and Hindi the app falls back to English for these, so the panel shows mixed
languages until they are translated.

## The file

Tab-separated, UTF-8 (with BOM) — opens directly in Excel / Google Sheets. One row per string.

| column | meaning |
|---|---|
| `key` | internal id — **do not change** |
| `category` | what kind of string it is (drug name, button, clinical warning, …) |
| `status` | `NEEDS TRANSLATION`, or `reference` for the one already-translated row |
| `notes` | per-row warnings (safety-critical / placeholders / symbols) |
| `en_source` | English source text — translate from this |
| `ru_reference` | the existing Russian, as a second reference |
| `zh` / `es` / `hi` | **fill these in** (Simplified Chinese, Spanish, Hindi) |

## Rules for the translator

1. **Fill only the `zh`, `es`, `hi` columns.** Leave `key`, `en_source`, `ru_reference` untouched.
2. **Preserve placeholders `{0}` / `{1}` exactly** — they are replaced at runtime with a drug
   name, dose, energy, rate, etc. `Gave {0} {1} mg` → the translation must keep both tokens
   in an order that reads correctly in the target language.
3. **Keep the symbols** `→`, `⚠`, `×` where they appear.
4. **SAFETY-CRITICAL rows** (clinical warnings and the arrest banner) must use correct medical
   terminology for the target locale — these are shown to clinicians during a simulated
   resuscitation. When unsure, prefer the established ACLS wording in that language.
5. Keep translations short — most render on buttons, chips, and a narrow side panel.

## After translation

Hand the completed file back; the `zh` / `es` / `hi` values get added to the corresponding
dictionaries in `src/CardioSimulator.App/Localization/AppStrings.cs` (the `Zh`, `Es`, `Hi`
tables), keyed by `key`. Regenerate the worklist any time new `tx_*` strings are added.
