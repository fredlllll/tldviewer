# TLD Level Viewer

A Unity desktop viewer for The Long Dark levels. It lists every level file in the
game data folder, streams prebuilt `levelN.viewer` **cell packages**, and runs the
exporter for levels that have not been converted yet. It never generates level
content itself and never probes for package files beyond the level list.

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

The level list is built from the game data folder (default
`\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data`). Every level button shows
its source size and whether a package already exists:

- **exported** (green): `levelN.viewer/manifest.json` exists, so the level loads
  immediately;
- **convert** (amber): the button runs the exporter for that level first, then
  streams the new package.

The size is the source `levelN` file size. Conversion cost grows with it
(roughly half a minute of loader overhead plus several seconds per MB). The
**Filter** field narrows the list by name.

The viewer auto-detects a Release or Debug build of the exporter in
`src/TldLevels.Exporter/bin`, falling back to `dotnet run` on the project file.
The **Exporter** field can point at the `.exe`, `.dll` or `.csproj` if the
viewer runs outside the repository.

Every cell in the package is loaded as soon as the package opens, nearest first,
and nothing is ever released. Distance only picks detail: a cell swaps to the
coarser authored LOD when you move away, and the new variant is built beside the
old one so the level never shows a hole while it is read. There is no streaming
radius to fly into, so a level with dozens of cells is fully visible from the
start rather than only around the camera.
Cell files and shared assets are read on background threads, and all import
work is spread across frames by a shared budget. Trees, houses and props are
instantiated from the shared asset library, with a LOD chosen per instance. The
status line reports loaded cells, queued cells, triangles, loaded/known shared
assets and the number of cached textures.

## UI

The overlay is a UI Toolkit document, not IMGUI:

- `Assets/Scenes/Viewer.unity` hosts a `UIDocument` with
  `Assets/UI/ViewerPanelSettings.asset` and `Assets/UI/Resources/ViewerUI.uxml`.
- `Assets/UI/Resources/ViewerUI.uss` holds all layout and colours.
  `Assets/UI/Resources/ViewerTheme.tss` imports Unity's default runtime theme.
- `Assets/Scripts/ViewerUi.cs` binds the named elements, raises plain C# events
  and rebuilds the two-column level grid.

The panel fills 94% of the viewport up to 620 px wide and 92% of its height, so
it stays inside a small docked Game view instead of running off the edge. The
level `ScrollView` grows and shrinks with the remaining space. The panel
settings use `ConstantPixelSize`, and `ViewerUi` sets the panel scale from the
viewport size, clamped to 0.8–1.5 so text never becomes unreadable. **Hide**
collapses the panel to its title bar, and **Settings** hides the three path
fields when vertical space is tight.

The UI and the fly camera share the keyboard, so they take turns:

- while a text field has focus the camera ignores WASD, the scroll wheel and the
  right mouse button, so typing a path never moves the camera;
- starting to look (right mouse button) blurs the focused field, and the panel
  stops picking clicks while the cursor is locked, so a right-click over a path
  field cannot open its text menu or steal the keys;
- clicking anywhere that is not a text field gives the keyboard back to the
  camera;
- WASD never triggers UI Toolkit's directional navigation: the navigation
  events that both the event system and the scroll view listen to are stopped at
  the panel root, so the level list no longer scrolls and focus no longer drifts
  through the controls while flying;
- text fields can only be focused by clicking them: if the panel ever hands
  focus to the first field on its own (for example on an unrelated key press),
  the viewer takes it back within a frame, so WASD keeps flying.

## Scene

`Viewer.unity` contains the `Viewer Camera` (with `FlyCamera`), the `Sun`
directional light and the `Viewer Bootstrap` object, so the Game view renders
the background before Play and the camera is available to the level list. The
bootstrap only fills in anything missing at runtime and starts `LevelLoader`.

The UI smoke test opens the viewer scene and checks the camera and sun, the
`UIDocument` references, the theme, the panel scale mode and every element name
that `ViewerUi` binds:

```powershell
Unity.exe -batchmode -nographics `
  -projectPath src/TldLevels.Viewer `
  -executeMethod ViewerUiSmokeTest.Run `
  -logFile ui-smoke.log
```

It also appears as **Tools > TLD Levels > Smoke Test Viewer UI**.

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
  generates anything. Its `includes` list names the base scenes copied into a
  design variant (empty for a normal level), which the status line shows as
  `Streaming level112 + level111`.
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
