# Vendored AssetRipper

| | |
|---|---|
| Repository | https://github.com/AssetRipper/AssetRipper |
| Path | `vendor/AssetRipper` |
| Tag | `2.0.0` |
| Upstream commit | `1ac666f47d8e9dedf96afb0b914c70d7656151ea` |
| Local tip | `1ef5f5d` (detached) |

The submodule starts from the 2.0.0 tag and carries a small set of local commits
on top of it. They are part of this repository's pin, not a patch queue applied
at build time:

- duplicate-placement filtering experiments (`d317fef` and earlier);
- vertex colour tweak for rock cliff 09 (`5d8d346`);
- expose scene draw meshes for viewer packages (`9d6e68f`);
- honour the caller's `includeAllLods` flag when collecting draw meshes
  (`1ef5f5d`): `ShouldSkipForVisualization` read the static `IncludeAllLods`
  property, which the exporter no longer sets, so package exports dropped every
  `_LOD1+` object before the LOD logic saw it. The draw-mesh entry point now
  takes `includeAllLods` as a parameter.

The main repository pins the submodule commit; `git submodule update --init
--recursive` followed by the checkout recorded by `git ls-tree HEAD
vendor/AssetRipper` reproduces the exact tree.

New local changes should be committed inside `vendor/AssetRipper` (as above) and
the updated pointer committed here, rather than editing files and leaving the
submodule dirty.
