# TLD Level Viewer

A Unity desktop viewer for the GLB level exports. This initial milestone loads a
complete exported level GLB and provides fly navigation; it deliberately does
not invoke the exporter or generate content on demand.

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
- **F**: reframe the loaded level
- **Escape**: release the mouse cursor

At the top-left, choose a GLB from the configured folder or paste an absolute
path and press **Load**. The default folder is the existing shared export
location: `\\VBoxSvr\TLDLevels\tldlevels\levels`.

## Next milestones

This project currently treats each GLB as one complete level. The planned
packaged viewer format will instead be built once per processed level and
contain all authored LODs split into spatial cells. The viewer will only load
those prebuilt cells; it will never need to call the AssetRipper exporter.
