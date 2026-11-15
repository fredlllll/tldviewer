# TLD Level Exporter

Exports The Long Dark **Wintermute** campaign levels to glTF **`.glb`** (geometry +
approximated materials), optionally writes a streamed **`levelN.viewer`** cell
package, and renders a top-down unlit PNG preview of each level.

The heavy lifting is done by [AssetRipper](https://github.com/AssetRipper/AssetRipper)
2.0.0, vendored as a git submodule **based on tag `2.0.0` plus local patches**
(draw-mesh extraction for viewer packages and a couple of material fixes) and
called in-process through its typed API. Only scene GLBs and viewer packages are
produced — no Unity project, no PNG texture dump, no per-mesh GLBs.

## Requirements

| | |
|---|---|
| OS | Windows (paths are tuned for the VirtualBox shares `Z:` and `Y:`) |
| SDK | .NET 10 SDK |
| Blender | 4.x/5.x, auto-detected under `C:\Program Files\Blender Foundation` |
| Viewer (optional) | Unity 6 (tested with 6000.3.21f1) |
| Game | The Long Dark with Wintermute installed, campaign data at `\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data` |

## Layout

```
src/TldLevels.Exporter/   the CLI (C#)
src/TldLevels.Viewer/     Unity desktop viewer for GLBs and cell packages
tools/blender/            render_topdown.py, executed inside Blender
vendor/AssetRipper/       git submodule, tag 2.0.0
```

Generated files are written **outside the repository**, by default to
`\\VBoxSvr\TLDLevels\tldlevels\levels` (`levelN.glb`, `levelN.png`,
`levelN.viewer/`, `shared/`, `manifest.json`, `manifest.csv`, `export.log`).
Use `--output` to change that.

## Viewer

`src/TldLevels.Viewer` is a separate Unity 6 project that loads either an
exported level GLB or a `levelN.viewer` package and provides fly navigation.
Open it from Unity Hub, then run `Assets/Scenes/Viewer.unity`. It reads files
from the existing output folder by default and never invokes the exporter. See
its [README](src/TldLevels.Viewer/README.md) for setup and controls.

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

# one level, GLB + preview
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 9

# several levels, no previews
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 2-40 --no-preview

# everything, skipping chapter stubs
dotnet run --project src\TldLevels.Exporter -c Release -- --levels all --skip-stubs

# streamed viewer package instead of a GLB (one directory per level)
dotnet run --project src\TldLevels.Exporter -c Release -- --levels 1-10 --viewer-package --no-preview
```

### Options

```
--input <dir>            default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
--output <dir>           default: \\VBoxSvr\TLDLevels\tldlevels\levels
--levels <spec>          all | 9 | 2-40,76,120    default: all (level0 excluded)
--batch-size <n>         levels per AssetRipper load   default: 1
--skip-stubs             skip levels smaller than 20 KB
--force                  re-export levels whose GLB already exists
--validate <bool>        inspect each GLB after writing   default: true
--merge-overlays <bool>  add the preceding art scene to design-level exports  default: true
--no-merge-overlays      export each level file on its own
--all-lods               keep LOD1/LOD2/etc. in GLB exports (default: LOD0 only)
--viewer-package         write level.viewer packages (shared assets + cells)
--cell-size <m>          viewer package cell edge length   default: 256
--merge-scene <a=b>      export scene a with base scene b (repeatable)
--preview <bool>         render a top-down PNG            default: true
--blender <exe>          Blender executable (auto-detected)
--preview-script <path>  render script (auto-detected)
--preview-size <px>      default: 2048
--preview-samples <n>    default: 16
--preview-margin <f>     default: 1.05
--dry-run                list the selection, then exit
```

Exit codes: `0` success, `1` bad arguments / nothing to do, `2` some levels failed, `130` interrupted with Ctrl+C.

## Interrupting and resuming

Every level is its own checkpoint:

- a GLB is written as `<level>.glb.part` and only moved into place on success, so a kill never leaves a truncated `.glb`;
- a viewer package is built as `<level>.viewer.part` and only moved into place after all cells, instance files and the manifest are written; shared assets are content-addressed and written independently, so a crash never leaves a half-written asset that the next run would trust;
- previews use `<level>.png.part.png` the same way;
- `manifest.json` is rewritten after every completed level;
- design variants are recognised from the manifest's `mergeVersion`/`includes`, so a resumed run does not rebuild them (and an old manifest triggers one re-export to repair the previous merge direction);
- **Ctrl+C** finishes the current level, writes the manifest and exits with `130`; a second Ctrl+C aborts immediately;
- re-running the same command skips everything already done, so it resumes where it stopped.

`--batch-size 10` is a good balance: it amortises AssetRipper's ~20 s shared-asset load over ten scenes. Measured on levels 2–11 it finished in **54 s** with a **1.1 GB** peak working set, while one level per batch costs ~27 s each.

## Art scenes and design overlays

Wintermute often stores an interior twice: the room shell in one level file
(`level2`) and the loose props in the next (`level3`). Exported alone, the prop
file looks like items floating in a void. Scenes whose largest mesh is under
15 m across are therefore exported as variants containing a copy of the
preceding art scene plus the design props. The base keeps its own GLB, and the
variant keeps the design level's name (for example `level10.glb` is the base and
`level11.glb` is base 10 plus design 11). Multiple design scenes each receive
their own independent variant.
Use `--no-merge-overlays` to export every file separately.

For non-consecutive campaign content, use an explicit association such as
`--merge-scene 120=118`. This writes `level120.glb` as a copy of level118 plus
level120's content while leaving `level118.glb` unchanged. The base scene is
loaded as context automatically even when it is not adjacent to the requested
scene.

## Viewer packages

`--viewer-package` replaces the single GLB with a streamed package built in one
pass per level. Terrain, sky shells, distant backdrop and oversized meshes stay
in the cell files; trees, houses, props and all of their authored LODs are
written once to a shared library at the output root:

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
- Package mode always keeps the authored Unity LODs (`LOD1`, `LOD2`, …),
  independent of `--all-lods`. Cell-local geometry falls back to its closest
  authored LOD so a coarser cell file never drops terrain or LOD0-only objects.
- `focusMin`/`focusMax` cover the `TerrainDefault` triangles (the playable
  ground), so the viewer can frame the level without the sky shell and distant
  backdrop dominating the bounds.
- Package mode does not also write `levelN.glb`. Run without
  `--viewer-package` if you need the whole-level debug GLB.

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
instances the shared meshes and selects a LOD per instance at distance. See the
[viewer README](src/TldLevels.Viewer/README.md).

## Notes

- AssetRipper resolves a level's dependencies (`levelN.resS`, `sharedassets*`,
  `resources.assets`) from the level file's own directory, so no staging copies
  are needed even though the source tree is read-only.
- MonoBehaviours are not reconstructed (mixed/IL2CPP structure); meshes,
  transforms, materials and heightmap terrain are unaffected.
- Unity Terrain landscapes are rebuilt from `TerrainData` because the stock GLB
  scene exporter only walks `MeshFilter`s. The terrain is untextured (white).
- Previews use Cycles CPU because Workbench/EEVEE crash headless without a GPU
  context on this machine. They are top-down, unlit, backface-culled, and skip
  shadow casters, sky domes, light shafts/glow cards and ceiling shells.
- Defaults use the VirtualBox shared-folder UNC paths, not drive letters: the
  guest has mapped the game share as both `Y:` and `Z:` at different times.
- Alpha is inferred per texture (AssetRipper writes every material as `OPAQUE`);
  see `PLAN.md` §8.

## Licence

GPL-3.0 — because this tool links against AssetRipper, which is GPL-3.0.
See `LICENSE` and `PLAN.md` §5.
