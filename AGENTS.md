# Agent instructions

## Unity: check for an open editor, ask only when one is running

**Before any Unity-related work, check whether the editor has the viewer project
open. Only ask the user to quit Unity when that check finds it open** — a closed
editor should never cost a prompt or a wait.

Run the check first:

```powershell
Get-Process Unity -ErrorAction SilentlyContinue | Select-Object Id, Path, MainWindowTitle
Test-Path src\TldLevels.Viewer\Temp\UnityLockfile
```

- `Temp\UnityLockfile` existing means the editor holds `src/TldLevels.Viewer`.
  That is the signal that matters.
- `Unity.exe` processes also exist during the agent's own batch runs, so do the
  check *before* starting a batch job, never while one is in progress. Ignore
  `Unity Hub.exe`.
- A Unity process without the lockfile is editing another project; viewer files
  are safe to touch.

If the editor is open, ask and wait for confirmation, and do not start until the
user says it is closed. If it is closed, go ahead without asking.

This includes:

- editing anything under `src/TldLevels.Viewer` (`Assets/`, `ProjectSettings/`,
  `Packages/`),
- running Unity in batch mode (e.g. `PackageSmokeTest.Run`),
- running any build, reimport, or compile step that touches the viewer project,
- anything else that would make the open editor notice changed files and
  recompile.

Why: when files change while the editor is open it recompiles as soon as it
regains focus, which races with the agent's own builds and produces confusing,
half-finished results.

Wording when a prompt is needed:

> The Unity editor currently has `src/TldLevels.Viewer` open. Please quit it and
> tell me when it is closed.

If batch work is needed while the editor must stay open, use a temporary copy of
the project outside the repository and say so explicitly.

## General

- Keep generated output outside the repository (`Z:\tldlevels\levels` by default).
- The exporter must only produce streamed viewer packages; no GLB export paths.
- The viewer must never generate level content or probe for LOD files; the
  package manifest is the source of truth for what a package contains.
