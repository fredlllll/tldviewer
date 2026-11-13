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
The status line reports loaded cells, queued cells, triangles and the number of
shared textures currently cached.

## Package format

A package is generated once per level by the exporter
(`--viewer-package`) and is self-contained:

```
level1.viewer/
  manifest.json          cells, bounds, LOD list and file names
  materials.json         material names used by the level
  textures/              shared textures, named by content hash
  cells/x-1_z0/lod0.gltf
  cells/x-1_z0/lod0.bin
```

- Every cell is split by triangle centroid on the configured grid
  (`--cell-size`, default 256 m), including Unity terrain.
- Cells reference textures with relative paths (`../../textures/<hash>.png`),
  so each unique texture exists once per level instead of once per cell.
- `manifest.json` is the only source of truth for which LOD levels exist; the
  viewer selects the nearest listed LOD and never checks the file system for
  optional levels.
- The exporter writes authored LODs when they are present in the scene. Levels
  whose scene only contains `_LOD0` produce a single LOD, which is expected.

## Shared texture cache

glTFast normally decodes every texture referenced by every cell. For packages,
`ViewerDownloadProvider` returns a 1×1 placeholder during import and
`PackageTextures` then replaces each material slot with a texture from the
per-level cache, deduplicated by hash file name, color space and sampler. This
keeps runtime texture memory proportional to the level, not to the number of
loaded cells.

## Package smoke test

The editor has a batch-mode test that loads the largest cell of a package,
instantiates it, applies the shared textures and fails if any placeholder
texture survives:

```powershell
Unity.exe -batchmode -nographics `
  -projectPath src/TldLevels.Viewer `
  -executeMethod PackageSmokeTest.Run `
  -logFile smoke.log
```

Set `TLD_SMOKE_PACKAGE` to test a different package directory. The test also
appears as **Tools > TLD Levels > Smoke Test Package** inside the editor.
