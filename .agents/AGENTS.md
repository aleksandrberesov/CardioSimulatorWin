# Project Rules & Customizations

## 1. Post-Task Parity & Sync Plan Generation
Upon successfully completing any code change, feature implementation, or bug fix in the Windows repository (`CardioSimulatorWin`), the agent **MUST** run the `save-changes-as-android-sync-plan` skill to document and structure a porting plan for the Android repository.

### Action Items:
1. Trigger the `save-changes-as-android-sync-plan` skill.
2. Generate the sync plan markdown document following the standard template.
3. Save/mirror the plan to both paths:
   - `E:\VLN_Project\CardioSimulatorWin\docs\plans\sync\`
   - `E:\VLN_Project\CardioSimulator\docs\plans\sync\`
4. Present the sync plan to the user in the final response.
