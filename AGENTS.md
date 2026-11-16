# Agent instructions

## Unity: always ask the user to quit Unity first

**Before any Unity-related work, stop and ask the user to close the Unity editor,
then wait for their confirmation.** Do not start until they say it is closed.

This includes:

- editing anything under `src/TldLevels.Viewer` (`Assets/`, `ProjectSettings/`,
  `Packages/`),
- running Unity in batch mode (e.g. `PackageSmokeTest.Run`),
- running any build, reimport, or compile step that touches the viewer project,
- anything else that would make the open editor notice changed files and
  recompile.

Why: the editor is often open on `src/TldLevels.Viewer`. When files change it
recompiles as soon as it regains focus, which races with the agent's own builds
and produces confusing, half-finished results.

Recommended wording to the user:

> Please quit the Unity editor on `src/TldLevels.Viewer` before I start, and
> tell me when it is closed.

If batch work is needed while the editor must stay open, use a temporary copy of
the project outside the repository and say so explicitly.

## General

- Keep generated output outside the repository (`Z:\tldlevels\levels` by default).
- The exporter must only produce streamed viewer packages; no GLB export paths.
- The viewer must never generate level content or probe for LOD files; the
  package manifest is the source of truth for what a package contains.
