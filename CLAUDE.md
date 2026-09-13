# CardioSimulator (Win) — notes for Claude Code

## Verify UI changes with App Clicker

After making a change that affects the app's UI, verify it with **App Clicker** — an
AI-driven manual tester at `E:\Automation_projects\App_Clicker` — rather than only
asserting the change works.

- **Build/publish:** `.\tools\build.ps1 -Publish` (Release/x64) → `artifacts\publish\antiAI-ECG-Simulator.exe`.
- **Run a check** (from `E:\Automation_projects\App_Clicker`, venv `.venv`). Two modes:
  ```powershell
  # FREE — quick smoke check (auto-selects a live free OpenRouter model)
  python -m app_clicker --free `
    --exe "E:\VLN_Project\CardioSimulator\Win\artifacts\publish\antiAI-ECG-Simulator.exe" `
    --keep-open --task "<verify the screen you changed, asserting a concrete result>"

  # PAID — cleaner / more reliable, for critical checks (Claude, prompt caching on)
  python -m app_clicker --paid `
    --exe "E:\VLN_Project\CardioSimulator\Win\artifacts\publish\antiAI-ECG-Simulator.exe" `
    --keep-open --task "<...>"
  ```
  Default to **`--free`** for routine smoke checks; use **`--paid`** when you need a clean,
  minimal repro or a trustworthy pass/fail (free models wander and are non-deterministic).
  `--free` auto-picks a live model (OpenRouter free slugs change often — never hardcode one;
  `--list-free-models` shows current ones). `--paid` needs `ANTHROPIC_API_KEY` in App Clicker's `.env`.
- Read the generated `reports\<timestamp>\report.md` + screenshots and report the verdict.
- **Edition matters:** the constructor screens (Course/ECG/OSCE/Test constructors — e.g.
  "lecture 7.2" lives in Course Constructor) exist only in the **Full** build
  (`Release`/`Debug`); the **Limited** build hides them. Test constructor flows on a Full build.
- Free models are non-deterministic (good for smoke/exploratory checks); for a strict pass/fail
  gate, tighten the task wording or use a paid model. See App Clicker's `README.md` for options.
