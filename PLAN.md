# The Long Dark — Wintermute level extraction to glTF/GLB

## 1. Goal

Extract every Wintermute level from the installed game and write one **`.glb` (binary glTF 2.0) per level**, including geometry and approximated materials.

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
| Stub levels | 83 files < 20 KB → **166 meaningful levels** |
| Output volume | `\\VBoxSvr\TLDLevels`, 110 GB free |
| Free disk (C:) | 404 GB |
| RAM / CPU | 23.1 GB / Ryzen 5 3600XT (6 cores) |
| SDK | .NET SDK 10.0.401 |

AssetRipper 2.0.0 declares support for Unity `3.5.0` → `6000.4.X`, so this build is in range. Tag `2.0.0` = commit `1ac666f47d8e9dedf96afb0b914c70d7656151ea`.

## 3. Decisions (confirmed)

1. **Route C**: build AssetRipper from a pinned source checkout and call its typed API in-process. Not the GUI/HTTP route, not reflection, not a from-scratch parser.
2. **v1 content**: geometry + approximated materials (base colour + normal where resolvable). Custom TLD shaders become simple glTF PBR.
3. **Binary `.glb`** output. `.gltf` + external buffer only as a fallback for scenes that exceed glTF's 2 GB buffer limit.
4. **Automatic previews** with Blender: top-down orthographic, unlit, black background, alpha-aware materials, one PNG per level.
5. **Output lives outside the repository**, on the `TLDLevels` VirtualBox share (`\\VBoxSvr\TLDLevels\tldlevels\levels`). The repo holds code only. `--output` overrides it. Defaults use UNC paths because the guest's drive letters proved unstable.

## 4. Why this works (validated on `level9`)

- AssetRipper accepts a **single serialized file** (e.g. `\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data\level9`).
- It detects a *Mixed* structure and resolves `level9.resS`, `sharedassets9`, `sharedassets2/8`, `sharedassets0`, `resources.assets` **from the level file's own directory**. No staging/copying required.
- Dependencies load on demand, so only one level is in memory at a time.
- Export call used:
  ```csharp
  GlbModelExporter.ExportModel(scene.Assets, path, isScene: true, LocalFileSystem.Instance);
  ```
- Measured result, straight from `Z:`:

  | Metric | Value |
  |---|---|
  | Wall time | **27.1 s** (19.5 s load + 7.6 s export) |
  | File size | **97.4 MB** |
  | Nodes | 2,744 |
  | Meshes | 1,607 |
  | Triangles | 859,995 |
  | Materials | 32 |
  | Embedded textures | 26 |
  | Output dir | GLB only (no PNG/mesh dump) |

- A cold `dotnet build` of the AssetRipper model pipeline took **~3 min**; our console app adds **~20 s** incremental.

## 5. Architecture

```
CLI  ──► LevelCatalog ──► ExportRunner ──► ManifestWriter
                              │
                              ├─ AssetRipper.Export.UnityProjects.ExportHandler
                              │      LoadAndProcess(levelPaths)
                              │      (asset processors: scenes, prefabs, materials)
                              └─ AssetRipper.Export.PrimaryContent.Models.GlbModelExporter
                                     scene.Assets → levelN.glb
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
│  └─ TldLevels.Exporter/
│     ├─ TldLevels.Exporter.csproj
│     ├─ Program.cs
│     ├─ Cli/Options.cs
│     ├─ Levels/LevelCatalog.cs
│     ├─ Export/ExportRunner.cs
│     ├─ Export/GlbInspector.cs
│     ├─ Preview/BlenderRenderer.cs
│     └─ Manifest/ManifestWriter.cs
└─ tools/
   └─ blender/render_topdown.py     # runs inside Blender

\\VBoxSvr\TLDLevels\tldlevels\levels\      generated, outside the repo
├─ levelN.glb
├─ levelN.png
├─ manifest.json
├─ manifest.csv
└─ export.log
```

Generated artefacts never enter the repository: `--output` defaults to `\\VBoxSvr\TLDLevels\tldlevels\levels`, and `output/` + `preview/` are gitignored for anyone who points it back into the tree.

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
- Classify `stub` when file size < 20 KB; still exportable, but reported separately.
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
    string name = scene.Name.String;            // e.g. "level9"
    string path = Path.Join(outputDir, name + ".glb");
    GlbModelExporter.ExportModel(scene.Assets, path, isScene: true, LocalFileSystem.Instance);
}
```

Notes:

- Use **one process per run**, with levels loaded in batches. `--batch-size 10` amortises shared-asset loading; memory grows with the batch. Measured on levels 2–11: 25.3 s to load 10 levels, then 0.7–5.8 s per scene, **54 s total**, peak working set **1.1 GB**. The runner falls back to one level at a time if a batch throws `OutOfMemoryException`.
- MonoBehaviours are **not** reconstructed (Mixed structure → "Unknown" scripting backend). This is fine for meshes/transforms/materials; it does not affect `MeshFilter`, `MeshRenderer`, `Transform`, `Terrain` or static batching.
- `GlbLevelBuilder` handles Unity→glTF coordinate conversion, static batching (`SubsetIndices` / `StaticBatchInfo`), sub-meshes and prefab instances. It does **not** emit scene `Terrain` components (it only walks `MeshFilter`s), so `SceneGlbExporter` rebuilds each referenced `TerrainData` heightmap with the same vertex layout as AssetRipper's `GlbTerrainBuilder` and attaches it at the terrain's world transform. Without this, level 1's ground was missing entirely.

### 6.2.2 Art scenes and design overlays

Wintermute splits many interiors into two serialized level files:

- an **art scene** containing the room shell, furniture and terrain;
- a **design scene** containing only loose props, usually in the *next* level file.

Exported alone, a design scene looks like items floating in a void (levels 3, 5, 7, 11, 17, 21, 25, 31 in the shipped data). `SceneProfile` classifies a scene as an overlay when its largest world-space mesh extent is ≤ **15 m** (`OverlayMaxExtent`); walls, floors, vehicles and terrain are all larger. The runner keeps the art scene's base GLB and exports each design scene under its own name as a variant containing a copy of the art assets plus that design scene's assets. For example, `level10.glb` is the base and `level11.glb` is base 10 plus design 11. Multiple designs therefore produce multiple independent variants. `--merge-overlays false` restores one file per level without the base copy.

Two pairs are deliberately treated as standalone art scenes: 22 + 23 (a cinematic dam set with 9.8 m beams and a cloud backdrop) and 28 + 29 (29 already contains its own basement shell, 20.2 m extent).

### 6.2.1 Interruption and resume

Every level is a checkpoint:

- the GLB is written to `<level>.glb.part` and moved into place only on success;
- the preview is rendered to `<level>.png.part.png` and moved the same way;
- `manifest.json` is rewritten after each completed level;
- Ctrl+C finishes the current level, writes the manifest and exits with code `130`; a second Ctrl+C aborts immediately;
- the next run skips levels whose GLB exists **and** inspects cleanly, so re-running the same command resumes where it stopped. A truncated GLB from a hard kill fails inspection and is re-exported automatically.

Drive letters on the guest are unstable (`Y:`/`Z:` swapped between sessions once), so the tool defaults to the VirtualBox share UNC paths instead:

```
input   \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
output  \\VBoxSvr\TLDLevels\tldlevels\levels
```

### 6.3 Validation

For every produced GLB, parse the JSON chunk and record:

- magic/version/chunk sizes,
- nodes, meshes, primitives, triangles,
- materials, textures/images,
- scene root count, mesh-local POSITION bounds (union of accessor min/max),
- presence of NaN/Inf in POSITION min/max,
- `empty` flag (0 meshes) → mark as stub/failed.

Optional deep check: load with SharpGLTF (already a transitive dependency) and verify it round-trips.

### 6.4 Manifest

`manifest.json` + `manifest.csv`, written next to the GLBs (default `\\VBoxSvr\TLDLevels\tldlevels\levels`). Paths inside are relative to the manifest:

```json
{
  "run": { "startedUtc": "...", "assetRipper": "2.0.0", "input": "Z:\\tld_dlc\\wintermute\\tld_Data" },
  "levels": [
    {
      "id": 9,
      "scene": "level9",
      "source": "Z:\\tld_dlc\\wintermute\\tld_Data\\level9",
      "glb": "level9.glb",
      "bytes": 102150000,
      "nodes": 2744,
      "meshes": 1607,
      "triangles": 859995,
      "materials": 32,
      "textures": 26,
      "exportSeconds": 7.6,
      "preview": "level9.png",
      "previewSeconds": 21.3,
      "alphaMaterials": 7,
      "status": "ok",
      "error": null,
      "warnings": []
    }
  ]
}
```

### 6.5 CLI

```
TldLevels.Exporter [options]

  --input <dir>        default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
  --output <dir>       default: \\VBoxSvr\TLDLevels\tldlevels\levels
  --levels <spec>      all | 9 | 2-40,76,120        (default: all, level0 excluded)
  --batch-size <n>     levels per loader instance   (default: 1)
  --skip-stubs         skip files < 20 KB           (default: false)
  --force              overwrite existing GLBs      (default: false)
  --validate           inspect each GLB after write (default: true)
  --dry-run            list what would be exported
  --merge-overlays     add the preceding art scene to design-level exports (default: true)
  --no-merge-overlays  export each level file on its own
  --preview            render a top-down PNG        (default: true)
  --blender <exe>      auto-detect if omitted
  --preview-size <px>  default: 2048
  --preview-samples <n> default: 16
  --preview-workers <n> parallel Blender processes  (default: 1)
  --viewer-package     write <level>.viewer cell packages instead of a GLB
  --cell-size <m>      viewer package cell edge length   (default: 256)
```

### 6.6 Viewer packages

`--viewer-package` writes a streamed package in one pass per level instead of a
GLB:

```
levelN.viewer/
  manifest.json          format version, cells, bounds, LOD list, per-cell files
  materials.json         material names used by the level
  textures/              unique textures, named by SHA-256 content hash
  cells/x-1_z0/lod0.gltf
  cells/x-1_z0/lod0.bin
```

- Static meshes are split by triangle centroid on a fixed X/Z grid; Unity
  terrain is split on the same grid. A mesh therefore appears in the cells its
  triangles actually occupy, not in every cell its AABB overlaps.
- Cells reference shared textures with relative paths
  (`../../textures/<hash>.png`), so each unique texture is stored once per level.
- The manifest is the only source of truth for available LODs; the viewer never
  probes the file system.
- `focusMin`/`focusMax` come from the `TerrainDefault` triangles, falling back
  to the overall mesh bounds for interiors, and drive viewer framing.
- Package mode does not also write `levelN.glb`.

Draw mesh transforms are world matrices from AssetRipper's scene traversal.
SharpGLTF's SRT constructor is `AffineTransform(scale, rotation, translation)`;
passing the components in a different order silently moves every object by its
own translation (observed as multi-kilometre cell bounds in `level118`). The
package smoke test therefore rejects cell bounds that are far outside the level
bounds or implausibly large for the configured grid.

## 7. Golden test

M1 is accepted when `--levels 9` produces `level9.glb` matching:

```
nodes       2744
meshes      1607
triangles   859995
materials   32
textures    26
size        ~97.4 MB
time        < 60 s
```

Small deltas are fine (±1 %) if AssetRipper settings differ; large deltas mean dependency resolution regressed.

Preview acceptance: `level9.png` is 2048×2048, black background, geometry fully inside the frame, the rendered footprint matches the GLB bounds, and the console reports `alphaMats=7`.

## 8. Preview rendering (Blender)

### 8.1 Verified behaviour

- Blender **5.3.0** at `C:\Program Files\Blender Foundation\Blender 5.3\blender.exe`.
- `level9` rendered at **2048×2048 in 21–22 s** including GLB import (Cycles CPU, 16 samples, alpha-aware).
- Framing came from the world-space union of object bounding boxes: `level9` = 193 × 237 × 78 m.
- **Workbench and EEVEE crash headless on this machine** (`EXCEPTION_ACCESS_VIOLATION`; Vulkan 1.1 unavailable, OpenGL fallback also crashes). Cycles CPU is the only reliable headless path — and it is fast enough.
- `Blender` runs from the C# tool as a child process:

  ```
  blender --background --factory-startup --python tools/blender/render_topdown.py -- <glb> <png> <size> <margin> <samples>
  ```

### 8.2 Render recipe

1. `bpy.ops.wm.read_factory_settings(use_empty=True)` then `bpy.ops.import_scene.gltf(...)`.
2. Compute world-space AABB from `object.bound_box` corners (no per-vertex loop).
3. Orthographic camera at `(cx, cy, maxZ + 100)`, rotation `(0,0,0)`, `ortho_scale = max(extentX, extentY) × margin`.
4. Black world background; `film_transparent = False`.
5. Replace every material with `Emission` (image texture → Emission Color, strength 1) → true unlit, no shadows, no bounces.
6. **Backface culling:** wire `Geometry → Backfacing` into a `Mix Shader` (front → emission, back → transparent). Cycles ignores `material.use_backface_culling`; the glTF flag only affects EEVEE/Workbench. Interior walls are single-sided with normals facing into the room, so culling shows the floor instead of the ceiling.
7. **Hidden geometry:** skip `*_Shadow` / `Shadow_Caster` proxies (invisible in game, white in an unlit pass), sky/cloud/horizon shells and any mesh wider than 5 km (backdrops would otherwise shrink the level to a dot), volumetric `LightShaft` / `GlowMesh` / light-pool cards (solid bright rectangles in game-space), and `*Ceiling*` shells (their top faces can point up, so culling alone does not remove them).
8. **Alpha inference:** AssetRipper writes every glTF material as `alphaMode` unset (`OPAQUE`) and never links `Alpha`, so there is no alpha signal to honour. Instead, sample each base-colour texture at 64×64 and take the fraction of texels with alpha < 0.5. Materials in the open range `0.005 … 0.995` get `Mix Shader(imageAlpha, Transparent BSDF, Emission)`. Materials outside it stay opaque — a nearly-clear texture is usually an RGB texture with an empty alpha channel, and hiding real geometry is worse than drawing one wrong decal.
9. `CYCLES`, `device=CPU`, `samples=16`, `max_bounces=4`, `transparent_max_bounces=16`, `use_denoising=False`.
10. `view_transform='Standard'`, `look='None'`, gamma 1.0 (avoid AgX/Filmic tone-mapping washing out the colours).
11. Output `<level>.png` next to `<level>.glb`.

Over a black background, alpha blending and additive blending are equivalent (`emission × alpha` either way), so glow decals read correctly without a separate additive path.

Measured on `level9`: 7 of 32 materials take the alpha path (`CORPSE_*`, `FX_TorchGradientGlow`, `GLB_GlowExt_A01[_Alpha]`, `OBJ_BranchA`, `TRN_TreePineBark_Atlas_2_Prefab`), covering 35 objects. The change costs ~2 s of render time and alters 0.17 % of pixels in the top-down view — small props are only a few pixels across at 249 m framing — but a zoomed crop confirms cutouts work (background geometry now shows through glow quads instead of being hidden by an opaque green rectangle).

### 8.3 Known limitations

- **Alpha is inferred, not authoritative.** The `0.005 … 0.995` coverage window is a heuristic; a fully transparent texture (`GEAR_KeroseneLampBGlass_01` in `level9`, alpha ≈ 1.0) deliberately stays opaque, so lamp glass can render as a solid panel.
- **Vertex colours are ignored** (emission uses only the base-colour texture).
- Snow/white materials dominate the image; a top-down unlit map is inherently low-contrast for snowy interiors.
- A tiny outlying object can expand the AABB and shrink the level in frame (seen in `level9`, top-right dot).
- **Baked shell effects cannot be filtered.** Some levels bake snow patches, light cards or ceiling pieces into one combined shell mesh; the name-based filters only remove separately named objects, so a few bright quads can remain.
- **Unity Terrain is untextured** (`TerrainDefault`, white) because splat maps are not reconstructed; heightmap geometry is complete.
- A design/art merge is a heuristic. If a genuine standalone scene is ever smaller than 15 m across it will be folded into its predecessor; `--no-merge-overlays` is the escape hatch.

### 8.4 Planned options (post-v1)

- Robust framing (ignore objects below an area threshold or outside the 1st–99th percentile).
- Optional "flat shaded" mode (normal colours or AO) for readability; still black background.
- Multi-tile high-resolution renders for big outdoor regions.
- Per-material alpha override list, for cases the coverage heuristic gets wrong.

## 9. Milestones

| # | Deliverable | Est. | Status |
|---|---|---|---|
| **M0** | `git init`, submodule pinned to 2.0.0, solution builds, `nuget.config`, `.gitignore`, GPL-3.0 LICENSE, Blender auto-detect | 0.5 d | **done** |
| **M1** | CLI + discovery + loader + GLB export + inspector + manifest + Blender preview; `level9` golden test | 1 d | **done** (`level9`: 19.2 s load, 7.4 s export, 20.5 s preview, 47.5 s total) |
| **M2** | Batch export of all levels; classification of stubs; failure list; first full manifest + preview contact sheet | 0.5 d (run ~1 h) | **running** (166 levels, `--batch-size 10`, resumable) |
| **M3** | QA pass: preview review, terrain/material fixes, level id → chapter name mapping, large-scene fallback | 1–2 d | planned |
| **M4** (opt.) | Parallel batches and parallel Blender workers, incremental re-runs, mesh simplification/Draco | 1 d | planned |

M1 was verified end-to-end against `\\VBoxSvr\TLDLevels\tldlevels\levels`: the exported GLB matches the golden counts exactly (2,744 nodes / 1,607 meshes / 859,995 triangles / 32 materials / 26 textures / 97.4 MB), the preview is byte-identical to the earlier manual render, and `--force` / existing-file skip / `--dry-run` paths all behave.

## 10. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Custom TLD shaders do not map to glTF | Materials look flat/approximate | Accepted for v1; only `_MainTex`/normal are used. Post-process in Blender if needed. |
| Addressables content (`StreamingAssets/aa`, 2 GB) is not PPtr-linked from scenes | Some props/FX may be missing | Compare GLB content to in-game screenshots; optionally scan the catalog for per-level bundles later. |
| Scenes > 2 GB combined buffer | GLB export fails (`GlbWriter` limit) | Detect, then fallback to `.gltf` + external `.bin` or split by static batch. |
| Batch memory growth | OOM / slowdown | Default batch size 1; expose `--batch-size`; process-level parallelism only. |
| AssetRipper drift | Build/API breakage | Submodule pinned to tag 2.0.0, no floating master. |
| IL2CPP scripts not reconstructed | No script data in output | Accepted; geometry/materials do not depend on it. |
| Stub levels | Noise in output | `--skip-stubs` + manifest classification. |
| Blender headless GPU crash | Preview step fails | Cycles CPU only; detected and reported with a clear error; `--preview false` to skip. |
| Preview cost | ~22 s/level | Parallel Blender workers; render only exported levels; optional lower resolution for contact sheets. |
| Alpha heuristic misclassifies a material | Wrong-looking preview (solids get cut out, or glass stays solid) | Restrict to the `0.005…0.995` coverage window; previews are diagnostic only; add a per-material override list if a level looks wrong. |
| GPL obligations | Distribution constraints | GPL-3.0 repo + unmodified submodule; or route A for a non-linked tool. |

## 11. Open questions (to resolve during M1/M2)

1. Does `--batch-size 8` meaningfully reduce total time without excessive memory? Measure peak working set.
2. Are Unity Terrain components actually used in TLD levels, and does `GlbTerrainBuilder` produce usable output?
3. Do the largest levels (`level244`, `level248`, `level231`, `level215`) exceed the 2 GB GLB limit?
4. Is there a reliable id → region/chapter name mapping in game data, or do we keep `levelN` names?
5. How many of the 145 non-stub levels contain zero meshes (pure logic/audio scenes)?
6. ~~Do alpha-tested materials make the unlit previews unreadable?~~ Resolved: alpha is inferred from texture coverage and applied; `level9` reports `alphaMats=7`.
7. Is one 2048 px top-down image enough, or do the large outdoor regions need tiled renders?

## 12. Out of scope for v1

- Terrain splat maps / lightmaps / probes
- Lights, cameras, colliders, navmesh, audio, particles
- Animations and rigged characters
- Texture repacking or shader decompilation
- Rebuilding a Unity project (AssetRipper's "Unity project" export) - only `.glb` and `.viewer` packages are produced
