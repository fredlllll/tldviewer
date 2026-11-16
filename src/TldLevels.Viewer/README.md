# TLD Level Viewer

A Unity desktop viewer for the exported The Long Dark levels. It loads either a
single whole-level **GLB** (debug/diagnostic mode) or a prebuilt
`levelN.viewer` **cell package** that streams nearby cells while flying around.
It deliberately never invokes the exporter and never generates or probes
content on demand.

## Open

1. Open `src/TldLevels.Viewer` in Unity Hub with Unity **6000.3.21f1** or a
   compatible Unity 6 editor.
2. Unity Package Manager fetches the pinned `glTFast` dependency (version
   6.20.0) the first time the project is opened.
3. Open `Assets/Scenes/Viewer.unity` and press Play.

## Controls

- **WASD**: move
- **Q / E**: down / up
- **Right mouse button + mouse**: look around
- **Mouse wheel**: change movement speed
- **Shift**: move faster
- **F**: reframe the loaded level on its playable terrain
- **Escape**: release the mouse cursor

At the top-left, pick a `*.glb` file or `*.viewer` directory from the configured
folder, or paste an absolute path and press **Load**. The default folder is the
shared export location: `\\VBoxSvr\TLDLevels\tldlevels\levels`.

When a package is open, a slider sets the streaming radius (256–4000 m). Cells
are loaded nearest-first and released once they are well outside that radius.
Cell files and shared assets are read on background threads, and all import
work is spread across frames by a shared budget. Trees, houses and props are
instantiated from the shared asset library, with a LOD chosen per instance. The
status line reports loaded cells, queued cells, triangles, loaded/known shared
assets and the number of cached textures.

## Package format

A package is generated once per level by the exporter. Level-independent
geometry lives in a shared library at the output root, so several levels
reference the same meshes and textures:

```
<output>/
  shared/
    assets/<id>.gltf        one mesh + one material per file
    assets/<id>.bin
    textures/<sha256>.png   unique textures, shared by every level
  level1.viewer/
    manifest.json           formatVersion 2, shared root, assets and cells
    materials.json          material names used by the level
    cells/x-1_z0/lod0.gltf
    cells/x-1_z0/lod0.bin
    cells/x-1_z0/instances.json
```

- Terrain, sky shells, distant backdrop and oversized meshes stay in the cell
  files. Cell-local geometry is split by triangle centroid on the configured
  grid (`--cell-size`, default 256 m), including Unity terrain.
- Every other mesh is stored once in `shared/assets`, addressed by a 128-bit
  content hash of its geometry, material and texture bytes. Cells reference
  textures with relative paths (`../../../shared/textures/<name>`); assets use
  `../textures/<name>`.
- `instances.json` assigns each object to one cell and stores its transform plus
  the asset ids for every authored LOD. The viewer chooses a LOD per instance at
  distance, not per cell.
- `manifest.json` is the only source of truth for which assets and LOD files
  exist; the viewer never checks the file system for optional content or
  generates anything.
- Each cell LOD file is a complete snapshot of its cell-local geometry: objects
  without a matching authored LOD (terrain, `_LOD0`-only props) fall back to
  their closest level, so switching to a coarser LOD never removes geometry.
- Shared assets are never deleted by the exporter, so a package can be updated
  or re-exported without invalidating another level that uses the same assets.

## Shared asset cache

glTF cannot reference a mesh defined in another file, so `PackageAssets` loads
each `<id>.gltf` lazily and keeps the import alive for the lifetime of the
level. Two background workers read a shared `<id>.gltf` plus its `<id>.bin`
from the level share and prefetch the referenced PNG bytes through
`PackageTextures.PrefetchFromJson` (staging is bounded at 128 MB). The main
thread then starts at most four prepared asset imports at a time, calls
`GetMesh(0,0)` and `GetMaterial(0)`, and every instance in every cell uses the
same Unity mesh and material.

Cell streaming takes the same route: `PackageStreamer.PrepareCell` reads the
cell `.gltf`, `.bin` and `instances.json` on a background thread, and the main
thread imports the prepared cell (creating its instance GameObjects with
budgeted yields).

Cell imports, asset imports and texture application share one
`PackageLoadBudget` of 1/30 s per frame, configured by the `LoadFrameBudget`
inspector field in seconds (it replaced the old `ImportFrameBudget` fraction).
glTFast import break points use that budget, and `PackageTextures.ApplyAsync`
yields between texture slots once it is spent.

`PackageTextures` works the same way for images: `ViewerDownloadProvider`
returns a 1x1 placeholder during import, then the cache replaces each material
slot with a texture from `shared/textures`, deduplicated by file name, color
space and sampler. Cached meshes, materials and textures are marked
`HideFlags.DontUnloadUnusedAsset`: they are referenced only from managed code,
so Unity's editor asset GC would otherwise unload them while the next cell or
asset imports and leave materials with placeholders.

## Package smoke test

The editor has a batch-mode test that loads the cell with the most authored
LODs, instantiates every one of its LOD files, reserves and loads a sample of
the shared assets referenced by its instances, applies the shared textures and
fails if any placeholder texture survives, if a referenced asset is missing
from the manifest or fails to load, if the LODs disagree on renderer count, or
if cell/instance bounds are outside the level or implausibly large for the
configured cell size (which catches broken node transforms). A second asset
sample is loaded through the normal `Request`/`Update` queue, so the background
prefetch and shared frame budget are exercised too:

```powershell
Unity.exe -batchmode -nographics `
  -projectPath src/TldLevels.Viewer `
  -executeMethod PackageSmokeTest.Run `
  -logFile smoke.log
```

Set `TLD_SMOKE_PACKAGE` to test a different package directory. The test also
appears as **Tools > TLD Levels > Smoke Test Package** inside the editor.
