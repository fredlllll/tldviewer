# TLD Level Exporter

Exports The Long Dark **Wintermute** campaign levels as streamed **`levelN.viewer`**
cell packages: cell-local meshes, per-instance transforms, and a content-addressed
shared asset/texture library.

The heavy lifting is done by [AssetRipper](https://github.com/AssetRipper/AssetRipper)
2.0.0, vendored as a git submodule **based on tag `2.0.0` plus local patches**
(draw-mesh extraction for viewer packages and a couple of material fixes) and
called in-process through its typed API. Only viewer packages and their shared
asset library are produced — no whole-level GLB, no preview images, no Unity
project, no PNG texture dump, no per-mesh GLB files.

## Requirements

| | |
|---|---|
| OS | Windows (paths are tuned for the VirtualBox shares `Z:` and `Y:`) |
| SDK | .NET 10 SDK |
| Viewer (optional) | Unity 6 (tested with 6000.3.21f1) |
| Game | The Long Dark with Wintermute installed, campaign data at `\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data` |

## Layout

```
src/TldLevels.Exporter/   the CLI (C#)
src/TldLevels.Viewer/     Unity desktop viewer for viewer packages
vendor/AssetRipper/       git submodule, tag 2.0.0
```

Generated files are written **outside the repository**, by default to
`\\VBoxSvr\TLDLevels\tldlevels\levels` (`levelN.viewer/`, `shared/`,
`manifest.json`, `manifest.csv`, `export.log`). Use `--output` to change that.

## Viewer

`src/TldLevels.Viewer` is a separate Unity 6 project that loads a `levelN.viewer`
package and provides fly navigation. Open it from Unity Hub, then run
`Assets/Scenes/Viewer.unity`. It reads files from the existing output folder by
default and never invokes the exporter. See its
[README](src/TldLevels.Viewer/README.md) for setup and controls.

## Build

```powershell
git clone --recurse-submodules https://example.invalid/tldlevels.git
cd tldlevels
dotnet build src\TldLevels.Exporter -c Release
```

If you already cloned without submodules:

```powershell
git submodule update --init --recursive
```

## Run

```powershell
# see what would be exported
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 9 --dry-run

# one level
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 9

# several levels
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 2-40

# everything, skipping chapter stubs
dotnet run --project src\TldLevels.Exporter -c Release -- --levels all --skip-stubs
```

### Options

```
--input <dir>            default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
--output <dir>           default: \\VBoxSvr\TLDLevels\tldlevels\levels
--levels <spec>          all | 9 | 2-40,76,120    default: all (level0 excluded)
--batch-size <n>         levels per AssetRipper load   default: 1
--skip-stubs             skip levels smaller than 20 KB
--force                  re-export packages that already exist
--merge-overlays <bool>  add the preceding art scene to design-level exports  default: true
--no-merge-overlays      export each level file on its own
--cell-size <m>          cell edge length   default: 256
--merge-scene <a=b>      export scene a with base scene b (repeatable)
--dry-run                list the selection, then exit
```

Exit codes: `0` success, `1` bad arguments / nothing to do, `2` some levels failed, `130` interrupted with Ctrl+C.

## Interrupting and resuming

Every level is its own checkpoint:

- a viewer package is built as `<level>.viewer.part` and only moved into place after all cells, instance files and the manifest are written; shared assets are content-addressed and written independently, so a crash never leaves a half-written asset that the next run would trust;
- `manifest.json` is rewritten after every completed level;
- a package counts as complete when its own `manifest.json` exists, so a resumed run skips every finished package (use `--force` to rebuild one);
- **Ctrl+C** finishes the current level, writes the manifest and exits with `130`; a second Ctrl+C aborts immediately;
- re-running the same command skips everything already done, so it resumes where it stopped.

`--batch-size 10` is a good balance: it amortises AssetRipper's ~20 s shared-asset load over ten scenes. Measured on levels 2–11 it finished in **54 s** with a **1.1 GB** peak working set, while one level per batch costs ~27 s each.

## Art scenes and design overlays

Wintermute often stores an interior twice: the room shell in one level file
(`level2`) and the loose props in the next (`level3`). Exported alone, the prop
file looks like items floating in a void. Scenes whose largest mesh is under
15 m across are therefore exported as variants containing a copy of the
preceding art scene plus the design props. The base package keeps its own name,
and the variant keeps the design level's name (for example `level10.viewer` is
the base and `level11.viewer` is base 10 plus design 11). Multiple design scenes
each receive their own independent variant.
Use `--no-merge-overlays` to export every file separately.

For non-consecutive campaign content, use an explicit association such as
`--merge-scene 120=118`. This writes `level120.viewer` as a copy of level118
plus level120's content while leaving `level118.viewer` unchanged. The base
scene is loaded as context automatically even when it is not adjacent to the
requested scene.

## Viewer packages

Each level is a streamed package built in one pass. Terrain, sky shells, distant
backdrop and oversized meshes stay in the cell files; trees, houses, props and
all of their authored LODs are written once to a shared library at the output
root:

```
<output>/
  shared/
    assets/<id>.gltf        one mesh + one material per file
    assets/<id>.bin
    textures/<sha256>.png   unique textures, shared by every level
  level1.viewer/
    manifest.json           format version 2, shared root, assets and cells
    materials.json          material names used by the level
    cells/x-1_z0/lod0.gltf
    cells/x-1_z0/lod0.bin
    cells/x-1_z0/lod1.gltf  (only for cells whose objects provide a coarser LOD)
    cells/x-1_z0/instances.json
  level118.viewer/
    ...
```

- Static cell-local meshes are split by triangle centroid onto a configurable
  grid (`--cell-size`, default 256 m). Terrain is split on the same grid.
- Every shared asset is content-addressed: its 128-bit id hashes the primitive
  geometry plus the material parameters and texture bytes. Identical props,
  trees and materials therefore resolve to the same file across levels, and a
  re-export skips assets that already exist instead of rewriting them.
- Shared textures are stored by SHA-256 prefix, so a texture used by a hundred
  assets and several levels still exists only once. Cells reference them as
  `../../../shared/textures/<name>`, assets as `../textures/<name>`.
- Instances are assigned to one cell by their world-bounds centre. A cell's
  `instances.json` stores each transform and the asset ids for every authored
  LOD, so the viewer chooses a LOD per instance rather than per cell. The
  manifest lists every available asset and LOD file; the viewer never probes
  the file system or generates content.
- All authored Unity LODs (`LOD1`, `LOD2`, …) are kept. Cell-local geometry
  falls back to its closest authored LOD so a coarser cell file never drops
  terrain or LOD0-only objects.
- `focusMin`/`focusMax` cover the `TerrainDefault` triangles (the playable
  ground), so the viewer can frame the level without the sky shell and distant
  backdrop dominating the bounds.

Measured with the shared library (`--cell-size 256`, authored LODs):

| Level | Package | Shared assets | Shared textures | Instances | Cells / LODs | Package build |
|---|---:|---:|---:|---:|---:|---:|
| `level1` | 4.1 MB | 15.9 MB (747) | 157.1 MB (74) | 585 | 344 / 3 | 60 s |
| `level111` | 53.6 MB | 68.5 MB (4,991) | 194.8 MB (154) | 19,062 | 701 / 4 | 240 s |
| `level118` | 47.0 MB | 110.6 MB (7,007) | 276.6 MB (216 new) | 11,401 | 668 / 5 | 226 s |

For comparison, the previous per-cell layout produced a 205.3 MB `level1`
package (48.0 MB of cells), 726.4 MB for `level111` after per-cell mesh dedup
(1,615.5 MB before) and 914.6 MB for `level118`. Exporting `level118` into the
same output root as `level1` reused 17 assets and added 216 textures to the
existing 74; 214 of `level118`'s asset ids also match `level111`, so a combined
export stores those once. Caching each material fingerprint and skipping already-written
assets cut `level111`'s first export from 1,332 s to 240 s.

The Unity viewer streams cells nearest-first, keeps them inside a radius,
instances the shared meshes and selects a LOD per instance at distance. Package
files are read on background threads and imported within a 1/30 s per-frame
budget, so cell and asset loading does not stall navigation. See the
[viewer README](src/TldLevels.Viewer/README.md).

## Notes

- AssetRipper resolves a level's dependencies (`levelN.resS`, `sharedassets*`,
  `resources.assets`) from the level file's own directory, so no staging copies
  are needed even though the source tree is read-only.
- MonoBehaviours are not reconstructed (mixed/IL2CPP structure); meshes,
  transforms, materials and heightmap terrain are unaffected.
- Unity Terrain landscapes are rebuilt from `TerrainData`; the terrain is
  untextured (white).
- Defaults use the VirtualBox shared-folder UNC paths, not drive letters: the
  guest has mapped the game share as both `Y:` and `Z:` at different times.
- Alpha is inferred per texture (AssetRipper writes every material as `OPAQUE`);
  see `PLAN.md` §8.

## Licence

GPL-3.0 — because this tool links against AssetRipper, which is GPL-3.0.
See `LICENSE` and `PLAN.md` §5.
