# TLD Level Exporter

Exports The Long Dark **Wintermute** campaign levels to glTF **`.glb`** (geometry +
approximated materials) and renders a top-down unlit PNG preview of each level.

The heavy lifting is done by [AssetRipper](https://github.com/AssetRipper/AssetRipper)
2.0.0, vendored as a git submodule pinned to tag `2.0.0` and called in-process
through its typed API. Only scene GLBs are produced — no Unity project, no PNG
texture dump, no per-mesh GLBs.

## Requirements

| | |
|---|---|
| OS | Windows (paths are tuned for the VirtualBox shares `Z:` and `Y:`) |
| SDK | .NET 10 SDK |
| Blender | 4.x/5.x, auto-detected under `C:\Program Files\Blender Foundation` |
| Game | The Long Dark with Wintermute installed, campaign data at `Z:\tld_dlc\wintermute\tld_Data` |

## Layout

```
src/TldLevels.Exporter/   the CLI (C#)
tools/blender/            render_topdown.py, executed inside Blender
vendor/AssetRipper/       git submodule, tag 2.0.0
```

Generated files are written **outside the repository**, by default to
`Y:\tldlevels\levels` (`levelN.glb`, `levelN.png`, `manifest.json`,
`manifest.csv`, `export.log`). Use `--output` to change that.

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
```

### Options

```
--input <dir>            default: Z:\tld_dlc\wintermute\tld_Data
--output <dir>           default: Y:\tldlevels\levels
--levels <spec>          all | 9 | 2-40,76,120    default: all (level0 excluded)
--batch-size <n>         levels per AssetRipper load   default: 1
--skip-stubs             skip levels smaller than 20 KB
--force                  re-export levels whose GLB already exists
--validate <bool>        inspect each GLB after writing   default: true
--preview <bool>         render a top-down PNG            default: true
--blender <exe>          Blender executable (auto-detected)
--preview-script <path>  render script (auto-detected)
--preview-size <px>      default: 2048
--preview-samples <n>    default: 16
--preview-margin <f>     default: 1.05
--dry-run                list the selection, then exit
```

Exit codes: `0` success, `1` bad arguments / nothing to do, `2` some levels failed.

## Notes

- AssetRipper resolves a level's dependencies (`levelN.resS`, `sharedassets*`,
  `resources.assets`) from the level file's own directory, so no staging copies
  are needed even though the source tree is read-only.
- MonoBehaviours are not reconstructed (mixed/IL2CPP structure); meshes,
  transforms, terrain and materials are unaffected.
- Previews use Cycles CPU because Workbench/EEVEE crash headless without a GPU
  context on this machine.
- Alpha is inferred per texture (AssetRipper writes every material as `OPAQUE`);
  see `PLAN.md` §8.

## Licence

GPL-3.0 — because this tool links against AssetRipper, which is GPL-3.0.
See `LICENSE` and `PLAN.md` §5.
