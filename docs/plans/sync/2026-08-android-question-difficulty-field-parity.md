# Plan: Port the `QuestionDifficulty` Field to Android

**Created:** 2026-08-09
**Status:** NOT STARTED
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\app\src\main\java\com\example\cardiosimulator\`
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\src\`

> **Foundational plan.** Land this first — the Question-Bank redesign, the Test generator, the Quick-Test
> launcher and the rich bank seed (their own sync plans) all read/write this field.

---

## 1. Background & Goals

Windows added an **optional authoring difficulty** to `TestQuestion`, shown as a badge in the question
bank and chosen in the editor. It is **nullable** and omitted from JSON when unset, so old question files
load unchanged.

**Reference (Windows) changes:**
- `src/CardioSimulator.Core/Domain/Test.cs` — new `enum QuestionDifficulty { Easy, Medium, Hard }`; new
  record param `QuestionDifficulty? Difficulty = null` on `TestQuestion` (appended last).
- `src/CardioSimulator.App/ViewModels/TestConstructorViewModel.cs` — `EditQuestion.Difficulty` field, carried
  in `From(...)` and both `Compile(...)` branches.
- `src/CardioSimulator.App/Screens/TestConstructorScreen.cs` — a difficulty `ComboBox` in `BuildThemeTagsRow`
  (theme · difficulty · tags), and a difficulty badge in the bank browse (see the bank-redesign plan).
- JSON: serializes directly on the record with `DefaultIgnoreCondition = WhenWritingNull`, so a null
  difficulty is not written and old files (no `difficulty`) deserialize to null.

---

## 2. Part A: Domain field

**Target:** `domain/Test.kt`

Add the enum and the nullable field (kotlinx.serialization; a nullable field defaulting to null is omitted
when null under the project's default `encodeDefaults=false`, so old JSON stays valid — verify the module's
`Json { }` config does **not** set `encodeDefaults = true`; if it does, this field is fine but confirm old
files still parse):

```kotlin
@Serializable
enum class QuestionDifficulty { Easy, Medium, Hard }

@Serializable
data class TestQuestion(
    // …existing params…
    val assemble: EcgAssembly? = null,
    val difficulty: QuestionDifficulty? = null,   // ← NEW (append last; nullable, omitted when null)
) { /* unchanged */ }
```

Grep first for any positional `TestQuestion(...)` construction that could break by appending a param
(`grep -rn "TestQuestion(" app/src/main`); a trailing param with a default keeps named/most positional calls
valid, matching what happened on Windows.

## 3. Part B: Edit model + editor dropdown

**Targets:** `ui/viewmodels/TestConstructorViewModel.kt`, `ui/screens/TestConstructorScreen.kt`
(+ its shared question-card composable, likely in `ui/screens/TestComponents.kt`).

- Mirror `EditQuestion` (or whatever mutable authoring model Android uses): add a nullable `difficulty`,
  read it in the "from `TestQuestion`" mapping, and write it in the "compile back to `TestQuestion`" mapping.
- In the question editor card, add a **difficulty dropdown** next to theme/tags with four choices:
  *(unset)* / Easy / Medium / Hard, bound to the edit model. Use the strings below.

## 4. Part C: Strings

Add to `res/values/strings.xml` (EN) and `res/values-ru/strings.xml` (RU); zh/es/hi fall back to EN.

| key | EN | RU |
|---|---|---|
| `diff_unset` | — difficulty — | — сложность — |
| `diff_easy` | ⭐ Easy | ⭐ Лёгкая |
| `diff_medium` | ⭐ Medium | ⭐ Средняя |
| `diff_hard` | ⭐ Hard | ⭐ Сложная |

(The Windows `AppStrings.DifficultyLabel(d)` helper maps the enum → these strings; add the Kotlin equivalent
where you render the badge.)

## 5. Verification

1. **Backward compat:** load an existing bank/test JSON (no `difficulty`) — it must parse with
   `difficulty == null` and re-serialize without adding the key.
2. Author a question, set difficulty = Hard, save, reopen — value round-trips; JSON contains
   `"difficulty":"Hard"`.
3. Leave difficulty unset — JSON omits the key.

## 6. Commit

```
feat(test): add optional QuestionDifficulty to TestQuestion

Nullable difficulty (Easy/Medium/Hard), omitted from JSON when unset so old
question files load unchanged. Editable in the question editor; rendered as a
badge in the bank browse (see bank-redesign parity plan).
```
