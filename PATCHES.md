# Vendored AssetRipper

| | |
|---|---|
| Repository | https://github.com/AssetRipper/AssetRipper |
| Path | `vendor/AssetRipper` (git submodule) |
| Pin | tag `2.0.0` = `1ac666f47d8e9dedf96afb0b914c70d7656151ea` |
| Local changes | `vendor/AssetRipper.patch` |

The submodule is pinned to the **upstream 2.0.0 tag**, which exists on the
AssetRipper remote, so a fresh clone can always fetch it:

```powershell
git submodule update --init --recursive
```

The exporter still needs a set of TLD-specific changes on top of 2.0.0. They
used to be local commits inside `vendor/AssetRipper`, which only existed on the
machine that made them, so a fresh clone failed with
`upload-pack: not our ref`. They are now a single patch,
`vendor/AssetRipper.patch`, which `TldLevels.Exporter.csproj` applies
automatically before it builds the referenced AssetRipper projects. The target
is idempotent: if the patch is already applied, nothing happens.

## Changes in the patch

| Commit | Change |
|---|---|
| `3e805821f` | Skip lower LODs and shadow meshes in scene exports |
| `90dc98874` | Skip impostor meshes in scene exports |
| `9fce0196e` | Skip collision helpers in visualization exports |
| `72000b157` | Deduplicate overlapping meshes and fix terrain axes |
| `b7af06d23` | Prefer final occurrence of duplicate placements |
| `e7b7a8fc1` | Use tolerant placement matching for deduplication |
| `f8939bb78` | Deduplicate duplicate prefab roots |
| `c52844ed3` | Deduplicate by transformed mesh centers |
| `d933185e9` | Match hierarchy composition for duplicate detection |
| `6166a98cc` | Use object origins for duplicate detection |
| `d317fef86` | Remove experimental duplicate placement filtering |
| `5d8d346f2` | Avoid vertex colors on rock cliff 09 |
| `9d6e68fce` | Expose scene draw meshes for viewer packages |
| `1ef5f5d4a` | Honour the caller's `includeAllLods` flag when collecting draw meshes |

The last two are what the exporter calls. `ShouldSkipForVisualization` read the
static `IncludeAllLods` property, which the exporter no longer sets, so package
exports dropped every `_LOD1+` object before the LOD logic saw it. The draw-mesh
entry point now takes `includeAllLods` as a parameter, and
`GlbLevelBuilder.cs` exposes the per-scene draw meshes the viewer packages are
built from.

## Updating

1. Check out the new AssetRipper tag in the submodule (`git -C vendor/AssetRipper
   fetch --tags` first).
2. Re-apply the old patch, resolve, then `git -C vendor/AssetRipper diff` against
   the pristine tag to regenerate `vendor/AssetRipper.patch`.
3. Update the pin (`git add vendor/AssetRipper`) and the table above.

`dotnet build` leaves the submodule working tree patched. That is expected; it
shows up as `modified: vendor/AssetRipper (modified content)` until you run
`git -C vendor/AssetRipper checkout -- .` (or `git submodule update`), after
which the next build patches it again.

## If `vendor/AssetRipper` is empty

`dotnet build` stops with one error:

```
vendor/AssetRipper is not checked out, so its projects cannot be built.
Run 'git submodule update --init --recursive' in the repository root, then build again.
```

Run that command and build again. Without the check the missing project
reference merely produces a warning and then dozens of `CS0246` errors for every
AssetRipper type, which is hard to read and easy to mistake for a code problem.
