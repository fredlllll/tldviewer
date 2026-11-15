using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;
using Material = UnityEngine.Material;
using Mesh = UnityEngine.Mesh;

namespace TldLevels.Viewer
{
    /// <summary>
    /// Lazy cache for the content-addressed meshes in the shared asset library.
    /// <para>
    /// One glTF file holds one primitive and one material, so a loaded asset
    /// maps directly to the shared Unity mesh/material used by every instance.
    /// glTF has no cross-file mesh references, which is why the viewer, not the
    /// cells, owns this cache.
    /// </para>
    /// </summary>
    public sealed class PackageAssets : IDisposable
    {
        private const int MaxConcurrentLoads = 4;

        private readonly string sharedDirectory;
        private readonly PackageTextures textures;
        private readonly ViewerDownloadProvider downloadProvider;
        private readonly Dictionary<string, PackageAsset> assets =
            new Dictionary<string, PackageAsset>(StringComparer.Ordinal);
        private readonly SemaphoreSlim gate = new SemaphoreSlim(MaxConcurrentLoads, MaxConcurrentLoads);
        private bool disposed;

        public PackageAssets(string sharedDirectory, PackageManifest manifest, PackageTextures textures)
        {
            this.sharedDirectory = sharedDirectory;
            this.textures = textures;
            downloadProvider = new ViewerDownloadProvider();

            if (manifest == null || manifest.assets == null)
            {
                return;
            }

            foreach (PackageAssetManifest entry in manifest.assets)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || assets.ContainsKey(entry.id))
                {
                    continue;
                }

                assets.Add(entry.id, new PackageAsset { Data = entry });
            }
        }

        public int Count => assets.Count;

        public int LoadedCount
        {
            get
            {
                int loaded = 0;
                foreach (PackageAsset asset in assets.Values)
                {
                    if (asset.State == PackageAssetState.Ready)
                    {
                        loaded++;
                    }
                }

                return loaded;
            }
        }

        public PackageAsset Get(string id)
        {
            if (id != null
                && assets.TryGetValue(id, out PackageAsset asset)
                && asset.State == PackageAssetState.Ready)
            {
                return asset;
            }

            return null;
        }

        /// <summary>
        /// Starts loading an asset once and returns the shared task. Unknown ids
        /// return null so a stale instance does not break the whole cell.
        /// </summary>
        public Task<PackageAsset> Request(string id)
        {
            if (id == null || !assets.TryGetValue(id, out PackageAsset asset))
            {
                return null;
            }

            if (asset.State == PackageAssetState.Ready)
            {
                return Task.FromResult(asset);
            }

            if (asset.LoadTask == null)
            {
                asset.LoadTask = LoadAsync(asset);
            }

            return asset.LoadTask;
        }

        private async Task<PackageAsset> LoadAsync(PackageAsset asset)
        {
            await gate.WaitAsync();
            try
            {
                if (disposed)
                {
                    return null;
                }

                string file = asset.Data.file;
                if (string.IsNullOrEmpty(file))
                {
                    asset.State = PackageAssetState.Failed;
                    return null;
                }

                string path = Path.Combine(
                    sharedDirectory,
                    file.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    asset.State = PackageAssetState.Failed;
                    Debug.LogWarning($"[PackageAssets] Missing asset file {path}");
                    return null;
                }

                GltfImport import = new GltfImport(downloadProvider, new UninterruptedDeferAgent());
                try
                {
                    if (!await import.LoadFile(path, new Uri(path)))
                    {
                        throw new InvalidOperationException($"glTFast could not parse {file}.");
                    }

                    if (disposed)
                    {
                        import.Dispose();
                        return null;
                    }

                    Mesh mesh = import.GetMesh(0, 0);
                    if (mesh == null)
                    {
                        throw new InvalidOperationException($"{file} contains no mesh.");
                    }

                    Material material = import.MaterialCount > 0
                        ? import.GetMaterial(0)
                        : import.GetDefaultMaterial();
                    textures.Apply(import);

                    // Meshes and materials are shared by many instances in many
                    // cells, so they must outlive any single cell import. Unity's
                    // editor GC otherwise unloads them while the next cell loads.
                    mesh.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    if (material != null)
                    {
                        material.hideFlags = HideFlags.DontUnloadUnusedAsset;
                    }

                    asset.Mesh = mesh;
                    asset.Material = material;
                    asset.Import = import;
                    asset.State = PackageAssetState.Ready;
                    return asset;
                }
                catch (Exception exception)
                {
                    import.Dispose();
                    asset.State = PackageAssetState.Failed;
                    Debug.LogWarning($"[PackageAssets] {asset.Data.id}: {exception.Message}");
                    return null;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        public void Dispose()
        {
            disposed = true;

            foreach (PackageAsset asset in assets.Values)
            {
                asset.Import?.Dispose();
                asset.Import = null;
                asset.Mesh = null;
                asset.Material = null;
            }

            assets.Clear();
        }
    }

    internal enum PackageAssetState
    {
        Unloaded,
        Ready,
        Failed,
    }

    public sealed class PackageAsset
    {
        internal PackageAssetManifest Data;
        internal GltfImport Import;
        internal Task<PackageAsset> LoadTask;
        internal PackageAssetState State;

        public string Id => Data.id;
        public string MaterialName => Data.material;
        public long Triangles => Data.triangles;
        public Mesh Mesh { get; internal set; }
        public Material Material { get; internal set; }
        public bool IsReady => State == PackageAssetState.Ready;
    }
}
