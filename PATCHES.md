# Vendored AssetRipper

| | |
|---|---|
| Repository | https://github.com/AssetRipper/AssetRipper |
| Path | `vendor/AssetRipper` |
| Tag | `2.0.0` |
| Commit | `1ac666f47d8e9dedf96afb0b914c70d7656151ea` |

The submodule is **unmodified**. No local patches are applied.

To reproduce the pin from a fresh clone:

```powershell
git submodule update --init --recursive
git -C vendor/AssetRipper checkout --detach 1ac666f47d8e9dedf96afb0b914c70d7656151ea
```

If AssetRipper ever needs a local change, add it as a patch file in this
directory (`NNNN-short-name.patch`) and document it here, rather than editing
files inside the submodule.
