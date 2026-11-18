# The Long Dark — Wintermute level extraction to streamed viewer packages

## 1. Goal

Extract every Wintermute level from the installed game and write one **streamed
`levelN.viewer` package per level**, backed by a content-addressed shared
asset/texture library, for the Unity viewer in `src/TldLevels.Viewer`.

- Language: **C# / .NET 10**
- Engine: **AssetRipper 2.0.0**, referenced from a pinned source checkout (route C)
- No Unity Editor, no Unity licence required
- First target: `level9` as the golden test case

## 2. Verified environment

| Item | Value |
|---|---|
| Game version | 2.60 (Steam, Windows) |
| Unity version | **6000.0.60f1** (Unity 6 LTS) |
| Scripting backend | **IL2CPP** (`GameAssembly.dll` + `il2cpp_data/Metadata/global-metadata.dat`) |
| Base data | `\\VBoxSvr\TheLongDark\tld_Data` (only `level0` / menu scene) |
| Campaign data | `\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data` — **250 level files** (`level0`…`level249`) |
| Level files | 1.89 GB (`levelN` + `levelN.resS`) |
| Whole campaign data | 9.58 GB |
| Output volume | `\\VBoxSvr\TLDLevels`, 110 GB free |
| Free disk (C:) | 404 GB |
| RAM / CPU | 23.1 GB / Ryzen 5 3600XT (6 cores) |
| SDK | .NET SDK 10.0.401 |

AssetRipper 2.0.0 declares support for Unity `3.5.0` → `6000.4.X`, so this build is in range. Tag `2.0.0` = commit `1ac666f47d8e9dedf96afb0b914c70d7656151ea`.

## 3. Decisions (confirmed)

1. **Route C**: build AssetRipper from a pinned source checkout and call its typed API in-process. Not the GUI/HTTP route, not reflection, not a from-scratch parser.
2. **Content**: geometry + approximated materials (base colour + normal where resolvable). Custom TLD shaders become simple glTF PBR.
3. **Streamed viewer packages** are the only output. There is no whole-level GLB export and no preview rendering; the viewer is the only consumer.
4. **Shared library at the output root.** Cell files keep terrain, sky shells, distant backdrop and oversized meshes; everything else is written once to `shared/assets` and instanced per cell.
5. **Output lives outside the repository**, on the `TLDLevels` VirtualBox share (`\\VBoxSvr\TLDLevels\tldlevels\levels`). The repo holds code only. `--output` overrides it. Defaults use UNC paths because the guest's drive letters proved unstable.

## 4. Why this works (validated on `level9`)

- AssetRipper accepts a **single serialized file** (e.g. `\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data\level9`).
- It detects a *Mixed* structure and resolves `level9.resS`, `sharedassets9`, `sharedassets2/8`, `sharedassets0`, `resources.assets` **from the level file's own directory**. No staging/copying required.
- Dependencies load on demand, so only one level is in memory at a time.
- Scene traversal uses `GlbLevelBuilder.BuildDrawMeshes`, the vendored draw-mesh extraction path that converts Unity→glTF coordinates, applies static batching (`SubsetIndices` / `StaticBatchInfo`), resolves sub-meshes, prefab instances and materials, and keeps authored LOD objects separate.
- Measured on `level9`: **19.5 s** to load, **7.6 s** to build the package assets, 859,995 triangles, 32 materials, 26 embedded textures. Package size and cell layout depend on `--cell-size`.
- A cold `dotnet build` of the AssetRipper model pipeline took **~3 min**; our console app adds **~20 s** incremental.

## 5. Architecture

```
CLI ──► LevelCatalog ──► ExportRunner ──► ViewerPackageExporter ──► shared/ + levelN.viewer/
                              │
                              ├─ AssetRipper.Export.UnityProjects.ExportHandler
                              │      LoadAndProcess(levelPaths)
                              │      (asset processors: scenes, prefabs, materials)
                              ├─ SceneProfile (art scene vs design overlay)
                              └─ TerrainBuilder.BuildDrawMeshes (Unity Terrain)
```

### Repository layout

```
C:\Projects\tldlevels\               repo: code only
├─ PLAN.md
├─ README.md
├─ LICENSE                          # GPL-3.0
├─ TldLevels.slnx
├─ nuget.config                     # nuget.org + https://nuget.samboy.dev/v3/index.json
├─ .gitignore                       # bin/, obj/, Source/0Bins/, *.log
├─ vendor/
│  └─ AssetRipper/                  # git submodule, pinned to tag 2.0.0
├─ src/
│  ├─ TldLevels.Exporter/
│  │  ├─ TldLevels.Exporter.csproj
│  │  ├─ Program.cs
│  │  ├─ Cli/Options.cs
│  │  ├─ Levels/LevelCatalog.cs
│  │  ├─ Export/ExportRunner.cs
│  │  ├─ Export/SceneProfile.cs
│  │  ├─ Export/TerrainBuilder.cs
│  │  ├─ Export/ViewerPackageExporter.cs
│  │  └─ Manifest/ManifestWriter.cs
│  └─ TldLevels.Viewer/             # Unity 6 project
└─ (no tools/ scripts)

\\VBoxSvr\TLDLevels\tldlevels\levels\      generated, outside the repo
├─ levelN.viewer/              streamed cell package
├─ shared/                     content-addressed assets + textures
├─ manifest.json
├─ manifest.csv
└─ export.log
```

Generated artefacts never enter the repository: `--output` defaults to `\\VBoxSvr\TLDLevels\tldlevels\levels`, and `output/` is gitignored for anyone who points it back into the tree.

`nuget.config` is required because the AssetRipper projects use its own feed (`nuget.samboy.dev`); without it, restore from our solution directory may fail.

### Licensing

AssetRipper is **GPL-3.0**. Linking its assemblies into our tool makes the combined work GPL-3.0. Plan:

- license this repo as **GPL-3.0**,
- keep `vendor/AssetRipper` as a submodule (unmodified) pinned to `2.0.0`,
- record the pin and any local patches in `PATCHES.md`.

If the tool ever needs to stay non-GPL, switch to route A (prebuilt exe as a separate HTTP-driven process) — the same engine, no linkage.

## 6. Pipeline

### 6.1 Discovery

- Enumerate `level\d+` files (no extension) under the input directory.
- Parse the numeric id; keep the file path.
- Skip `level0` (menu) by default.
- Treat every level file equally; small files can still contain a few props.
- Optional explicit selection: `--levels 9`, `--levels 2-40,76,120`.

### 6.2 Load + export

Per batch of level files (default batch size **1**, tune later):

```csharp
FullConfiguration settings = new();
settings.LogConfigurationValues();

ExportHandler handler = new(settings);
GameData data = handler.LoadAndProcess(batchPaths, LocalFileSystem.Instance);

foreach (var scene in data.GameBundle.FetchAssets().OfType<SceneHierarchyObject>())
{
    ViewerPackageExporter.Export(scene.Assets, packagePath, cellSize, includeAllLods: true);
}
```

Notes:

- Use **one process per run**, with levels loaded in batches. `--batch-size 10` amortises shared-asset loading; memory grows with the batch. Measured on levels 2–11: 25.3 s to load 10 levels, then 0.7–5.8 s per scene, **54 s total**, peak working set **1.1 GB**. The runner falls back to one level at a time if a batch throws `OutOfMemoryException`.
- MonoBehaviours are **not** reconstructed (Mixed structure → "Unknown" scripting backend). This is fine for meshes/transforms/materials; it does not affect `MeshFilter`, `MeshRenderer`, `Transform`, `Terrain` or static batching.
- `GlbLevelBuilder` handles Unity→glTF coordinate conversion, static batching, sub-meshes, prefab instances and material construction. It does **not** emit scene `Terrain` components (it only walks `MeshFilter`s), so `TerrainBuilder.BuildDrawMeshes` rebuilds each referenced `TerrainData` heightmap with the same vertex layout as AssetRipper's `GlbTerrainBuilder` and yields it as a draw mesh on the same grid. Without this, level 1's ground would be missing entirely.

### 6.2.2 Art scenes and design overlays

Wintermute splits many interiors into two serialized level files:

- an **art scene** containing the room shell, furniture and terrain;
- a **design scene** containing only loose props, usually in the *next* level file.

Exported alone, a design scene looks like items floating in a void. `SceneProfile` classifies a scene as an overlay from its hierarchy: the authoring convention groups props under a top-level `Design` while an art scene carries `Art` (usually next to its own `Design` items), so `Design` without `Art` means design overlay. Level 109 (`Cinematics | Design`) and level 112 (`Design`) are the newer examples; levels 108 and 110 both carry `Art`. Older levels predate the group names and wrap everything in a neutral `Root`; for those the largest world-space mesh extent decides (≤ **15 m**, `OverlayMaxExtent`), which caught levels 3, 5, 7, 11, 17, 21, 25 and 31. The runner keeps the art scene's base package and exports each design scene under its own name as a variant containing a copy of the art assets plus that design scene's assets. For example, `level10.viewer` is the base and `level11.viewer` is base 10 plus design 11. Multiple designs therefore produce multiple independent variants. `--merge-overlays false` restores one package per level without the base copy.

Two pairs are deliberately treated as standalone art scenes: 22 + 23 (a cinematic dam set with 9.8 m beams and a cloud backdrop) and 28 + 29 (29 already contains its own basement shell, 20.2 m extent). They are only affected if their hierarchy labels them `Design` without `Art`.

### 6.2.1 Interruption and resume

Every level is a checkpoint:

- the package is written to `<level>.viewer.part` and moved into place only after all cells, instance files and the manifest are written;
- `manifest.json` is rewritten after each completed level;
- Ctrl+C finishes the current level, writes the manifest and exits with code `130`; a second Ctrl+C aborts immediately;
- the next run skips levels whose package contains `manifest.json`, so re-running the same command resumes where it stopped. `--force` rebuilds a package even if it is complete.

Drive letters on the guest are unstable (`Y:`/`Z:` swapped between sessions once), so the tool defaults to the VirtualBox share UNC paths instead:

```
input   \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
output  \\VBoxSvr\TLDLevels\tldlevels\levels
```

### 6.3 Validation

A completed package is identified by `<level>.viewer/manifest.json`. The package
manifest records the format version, shared root, level bounds, every shared
asset id and every cell LOD file, so the viewer never probes the file system.

The exporter reports `empty` when a package contains no triangles and records
per-level counts (cells, instances, assets, triangles, LODs) in the run
`manifest.json`/`manifest.csv`.

### 6.4 Manifest

`manifest.json` + `manifest.csv`, written next to the packages (default
`\\VBoxSvr\TLDLevels\tldlevels\levels`). Paths inside are relative to the manifest:

```json
{
  "run": {
    "startedUtc": "...",
    "assetRipper": "2.0.0",
    "input": "Z:\\tld_dlc\\wintermute\\tld_Data",
    "levels": 1,
    "exportedBytes": 4100000,
    "seconds": 60.0,
    "succeeded": 1,
    "empty": 0,
    "failed": 0,
    "pending": 0,
    "cancelled": false
  },
  "levels": [
    {
      "id": 1,
      "scene": "level1",
      "source": "Z:\\tld_dlc\\wintermute\\tld_Data\\level1",
      "sourceBytes": 1234567,
      "package": "level1.viewer",
      "bytes": 4100000,
      "cells": 344,
      "instances": 585,
      "assets": 747,
      "triangles": 123456,
      "lods": 3,
      "exportSeconds": 60.0,
      "status": "ok",
      "includes": [],
      "error": null,
      "warnings": []
    }
  ]
}
```

### 6.5 CLI

```
TldLevels.Exporter [options]

  --input <dir>            default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
  --output <dir>           default: \\VBoxSvr\TLDLevels\tldlevels\levels
  --levels <spec>          all | 9 | 2-40,76,120        (default: all, level0 excluded)
  --batch-size <n>         levels per loader instance   (default: 1)
  --force                  re-export existing packages
  --dry-run                list what would be exported
  --merge-overlays <bool>  add the preceding art scene to design-level exports (default: true)
  --no-merge-overlays      export each level file on its own
  --merge-scene <a=b>      export scene a with base scene b (repeatable)
  --cell-size <m>          cell edge length             (default: 256)
```

### 6.6 Viewer packages

Each level is a streamed package built in one pass. Terrain, sky shells, distant
backdrop and oversized meshes stay in the cell files; every other mesh is
content-addressed into a shared library at the output root:

```
<output>/
  shared/
    assets/<id>.gltf        one mesh + one material per file
    assets/<id>.bin
    textures/<sha256>.png   unique textures, shared by every level
  levelN.viewer/
    manifest.json           formatVersion 2, shared root, assets and cells
    materials.json          material names used by the level
    cells/x-1_z0/lod0.gltf
    cells/x-1_z0/lod0.bin
    cells/x-1_z0/lod1.gltf  (cells whose objects provide a coarser authored LOD)
    cells/x-1_z0/instances.json
```

- Static cell-local meshes are split by triangle centroid on a fixed X/Z grid;
  Unity terrain is split on the same grid. A mesh therefore appears in the cells
  its triangles actually occupy, not in every cell its AABB overlaps.
- A draw mesh is cell-local when all of its primitives use `TerrainDefault`, any
  material is a sky/distant-backdrop material (`TRN_Sky*`,
  `TRN_MountainDistant*`), or its world X/Z extent exceeds one cell. Everything
  else becomes a shared instance. Oversized props stay in the cell so they do
  not pop in and out with the streaming grid.
- A shared asset id is a 128-bit FNV of the primitive geometry (positions,
  normals, tangents, colours, UVs, indices), the material parameters and the
  image bytes. SharpGLTF's own content hash is not used because its string hash
  is process-randomised. Because the id is content-based, identical trees,
  houses, props and materials resolve to the same file in every level, and an
  existing `<id>.gltf`/`<id>.bin` pair is reused instead of rewritten.
- Shared textures are named by the first 8 bytes of their SHA-256 (16 hex
  characters) and stored once under `shared/textures`, regardless of how many
  assets or levels reference them. Cell files reference them with relative
  paths (`../../../shared/textures/<name>`), assets with `../textures/<name>`.
- Each instance is assigned to one cell by its world-bounds centre. The cell's
  `instances.json` stores the transform and the asset ids of every authored LOD
  (`{"level":0,"assets":[...],"triangles":N}`), so the viewer selects a LOD per
  instance. `manifest.json` lists every shared asset and every cell LOD file;
  it is the only source of truth, and the viewer never probes the file system.
- All authored Unity LODs (`LOD1`, `LOD2`, …) are kept. Authored LOD objects live
  in the same scene with a `_LODn` suffix and the same transform, so the exporter
  pairs them by stripped name and transform.
- Every written cell LOD is a complete snapshot of its cell-local geometry.
  Authored LOD files only contain the objects that have that level, so terrain
  and LOD0-only props would vanish on switch; each object instead falls back to
  its highest authored LOD at or below the requested level (or its lowest
  authored LOD when it has none), and the manifest reports the triangles
  actually written per LOD file.
- `focusMin`/`focusMax` come from the `TerrainDefault` triangles, falling back
  to the overall mesh bounds for interiors, and drive viewer framing.
- Shared assets are content-addressed and never deleted on `--force`; orphaned
  assets from a changed cell size or a removed level are harmless.

Measured sizes with the shared library (`--cell-size 256`, authored LODs):

| Level | Package | Shared assets | Shared textures | Instances | Cells / LODs | Package build |
|---|---:|---:|---:|---:|---:|---:|
| `level1` | 4.1 MB | 15.9 MB (747) | 157.1 MB (74) | 585 | 344 / 3 | 60 s |
| `level111` | 53.6 MB | 68.5 MB (4,991) | 194.8 MB (154) | 19,062 | 701 / 4 | 240 s |
| `level118` | 47.0 MB | 110.6 MB (7,007) | 276.6 MB (216 new) | 11,401 | 668 / 5 | 226 s |

The earlier per-cell layout produced 726.4 MB for `level111` (1,615.5 MB before
per-cell mesh dedup) and 914.6 MB for `level118`; `level1` alone was 205.3 MB,
48.0 MB of it cells. Exporting `level118` into the same output root as `level1`
reused 17 assets and added 216 textures to the existing 74; 214 of `level118`'s
asset ids also match `level111`, so a combined export stores those once. Caching each
material fingerprint (including texture bytes) per `MaterialBuilder`, computing
world bounds once, reusing single-primitive source meshes and skipping
already-written assets cut `level111`'s first export from 1,332 s to 240 s.

The viewer loads a shared asset glTF once, takes `GetMesh(0,0)` and
`GetMaterial(0)`, and keeps the import alive for the lifetime of the level.
glTF cannot reference a mesh defined in another file, so this cache, not the
package format, is what makes reuse real at runtime. Two background workers read
the tiny `<id>.gltf`/`<id>.bin` pairs (and stage referenced PNG bytes) so the
main thread never waits on the network share; prepared imports are then started
at most four at a time. Cells, assets and texture application all draw from one
`PackageLoadBudget` of 1/30 s per frame, so importing a new cell cannot stall the
rest of the frame. Cell glTF/bin and `instances.json` are read the same way.

The shared texture cache in the viewer owns its textures and marks them
`HideFlags.DontUnloadUnusedAsset`, as do cached meshes and materials. They are
referenced only from managed code, so Unity's editor asset GC would otherwise
destroy them while the next cell or asset imports and leave materials with
glTFast's 1×1 placeholders. PNGs are prefetched off-thread into a bounded 128 MB
staging cache; only decoding and GPU upload stay on the main thread, paused
between texture slots when the frame budget is spent.

Draw mesh transforms are world matrices from AssetRipper's scene traversal.
SharpGLTF's SRT constructor is `AffineTransform(scale, rotation, translation)`;
passing the components in a different order silently moves every object by its
own translation (observed as multi-kilometre cell bounds in `level118`). The
package smoke test therefore rejects cell bounds that are far outside the level
bounds or implausibly large for the configured grid.

## 7. Golden test

Package acceptance uses the Unity smoke test in
`src/TldLevels.Viewer/Assets/Editor/PackageSmokeTest.cs`:

```
Unity.exe -batchmode -nographics -projectPath src/TldLevels.Viewer \
  -executeMethod PackageSmokeTest.Run -logFile smoke.log
```

with `TLD_SMOKE_PACKAGE` pointing at a package. It verifies the package
manifest, cell/instance loading, shared asset imports, background prefetch and
the 1/30 s import budget. `level9` and `level118` pass, including queued asset
imports.

For a new exporter build, also compare per-level counts (cells, instances,
assets, triangles, LODs) against the measured table in §6.6. A large drop means
draw-mesh extraction or terrain rebuilding regressed.

## 8. Viewer runtime

The Unity viewer loads every cell of a package up front, nearest first, and never
releases one: locating is not distance-driven, only detail is, so moving away
swaps a cell to the coarser authored LOD in place. It instances the shared meshes
and selects a LOD per instance at distance.

- **On-demand conversion.** The level list comes from the game data folder, not
  from existing packages. Each button shows the source file size and whether a
  package already exists; clicking a missing level runs the exporter for that
  level and then opens the new package. Package creation stays checkpointed in
  the exporter, so a failed or interrupted conversion leaves no usable package.
- **1/30 s import budget per frame.** `PackageLoadBudget.BeginFrame()` resets
  the window explicitly because `Time.frameCount` may not advance in
  `-nographics` batch mode.
- **Background prefetch.** Package glTF/bin/PNG bytes are read on worker threads;
  the main thread only decodes and uploads.
- **Where the budget applies.** Cell glTF/bin, `instances.json`, shared asset
  glTF/bin and texture application all consume one shared budget, so loading a
  new cell cannot starve the rest of the frame.
- **No generated content.** The viewer instantiates only the meshes, materials
  and LOD variants listed in the package manifest; it never probes for files or
  builds fallback geometry.
- **UI Toolkit overlay.** The level list, path fields and status lines are a
  `UIDocument` built from `Assets/UI/Resources/ViewerUI.uxml`
  and `ViewerUI.uss`. The panel uses percentage sizing inside the viewport with
  a `ConstantPixelSize` scale clamped to 0.8–1.5, so it fits small Game views
  without shrinking text into illegibility.
- **Shared keyboard.** A focused text field blocks the fly camera, capturing the
  mouse blurs the field and disables panel picking, and clicking outside a field
  returns the keyboard to the camera. `Viewer.unity` also owns the camera and
  sun, so the Game view renders before Play instead of building them at runtime.

## 9. Milestones

| # | Deliverable | Status |
|---|---|---|
| **M0** | `git init`, submodule pinned to 2.0.0, solution builds, `nuget.config`, `.gitignore`, GPL-3.0 LICENSE | **done** |
| **M1** | CLI + discovery + loader + viewer package export + run manifest + shared asset library; `level9` golden test | **done** |
| **M2** | Batch export of all levels; failure list; full manifest | **done** (12 levels verified: 1–10, 111, 118) |
| **M3** | QA pass: viewer review, terrain/material fixes, level id → chapter name mapping, large-scene handling | **in progress** |
| **M4** (opt.) | Parallel batches, incremental re-runs, mesh simplification/Draco | planned |

## 10. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Custom TLD shaders do not map to glTF | Materials look flat/approximate | Accepted; only `_MainTex`/normal are used. |
| Addressables content (`StreamingAssets/aa`, 2 GB) is not PPtr-linked from scenes | Some props/FX may be missing | Compare package content to in-game screenshots; optionally scan the catalog for per-level bundles later. |
| Batch memory growth | OOM / slowdown | Default batch size 1; expose `--batch-size`; fall back to one level at a time on OOM. |
| AssetRipper drift | Build/API breakage | Submodule pinned to tag 2.0.0, no floating master. |
| IL2CPP scripts not reconstructed | No script data in output | Accepted; geometry/materials do not depend on it. |
| Tiny level files | Easy to mistake for empty scenes | Treated like every other level; export and check the package. |
| GPL obligations | Distribution constraints | GPL-3.0 repo + unmodified submodule; or route A for a non-linked tool. |

## 11. Open questions

1. Does `--batch-size 8` meaningfully reduce total time without excessive memory? Measure peak working set.
2. Do the largest levels (`level244`, `level248`, `level231`, `level215`) stay within practical cell/package sizes?
3. Is there a reliable id → region/chapter name mapping in game data, or do we keep `levelN` names?
4. How many levels contain zero meshes (pure logic/audio scenes)?

## 12. Out of scope for v1

- Terrain splat maps / lightmaps / probes
- Lights, cameras, colliders, navmesh, audio, particles
- Animations and rigged characters
- Texture repacking or shader decompilation
- Rebuilding a Unity project (AssetRipper's "Unity project" export)
- Whole-level GLB exports and preview images — the viewer package is the only output
