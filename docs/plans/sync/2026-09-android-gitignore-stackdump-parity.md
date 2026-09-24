# Plan: Port *.stackdump Git Ignore Pattern to Android Repository

**Created:** 2026-09-24  
**Status:** NOT STARTED  
**Direction:** **Windows → Android**

**Target (Android) source root:** `E:\VLN_Project\CardioSimulator\Android\`  
**Reference (Windows) source root:** `E:\VLN_Project\CardioSimulator\Win\`  

---

## 1. Background & Goals

Command-line utilities and tools run on Windows (such as MSYS/Cygwin grep) can occasionally generate crash dump files named `grep.exe.stackdump` or `<tool>.exe.stackdump`. `*.stackdump` pattern was added to `.gitignore` in the Windows repository to prevent diagnostic stack dumps from being tracked by Git. This sync plan documents adding the same rule to the Android/root repository environment.

---

## 2. Part A: Git Ignore Configuration

- Inspect `.gitignore` in the target Android repository root.
- Add `*.stackdump` under `# Logs and temporary files`.

```gitignore
# Logs and temporary files
*.stackdump
```

---

## 3. Part B: Verification

### 3.1 Manual Verification Flow
1. Run `git check-ignore -v grep.exe.stackdump` in target repository.
2. Confirm stackdump files are ignored by `git status`.
