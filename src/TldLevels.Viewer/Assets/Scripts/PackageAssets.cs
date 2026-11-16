using System;
using System.Collections.Concurrent;
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
    /// Cache for the content-addressed meshes in the shared asset library.
    /// <para>
    /// One glTF file holds one primitive and one material, so a loaded asset
    /// maps directly to the shared Unity mesh/material used by every instance.
    /// glTF has no cross-file mesh references, which is why the viewer, not the
    /// cells, owns this cache.
    /// </para>
    /// <para>
    /// A background worker reads the small <c>.gltf</c>/<c>.bin</c> pairs (and
    /// prefetches their PNG bytes) from the level share. The main thread only
    /// imports prepared files, a few at a time, and glTFast's break points all
    /// check the shared <see cref="PackageLoadBudget"/> so a frame never spends
    /// more than its loading allowance.
    /// </para>
    /// </summary>
    public sealed class PackageAssets : IDisposable
    {
        private const int MaxConcurrentImports = 4;
        private const int WorkerCount = 2;
        private const int WorkerShutdownTimeoutMs = 2000;

        private readonly string sharedDirectory;
        private readonly PackageTextures textures;
        private readonly Dictionary<string, PackageAsset> assets =
            new Dictionary<string, PackageAsset>(StringComparer.Ordinal);
        private readonly BlockingCollection<PackageAsset> requests = new BlockingCollection<PackageAsset>();
        private readonly ConcurrentQueue<PreparedAsset> prepared = new ConcurrentQueue<PreparedAsset>();
        private readonly Task[] workers;
        private volatile bool disposed;
        private int loaded;
        private int activeImports;

        public PackageAssets(string sharedDirectory, PackageManifest manifest, PackageTextures textures)
        {
            this.sharedDirectory = sharedDirectory;
            this.textures = textures;

            if (manifest == null || manifest.assets == null)
            {
                workers = Array.Empty<Task>();
                return;
            }

            foreach (PackageAssetManifest entry in manifest.assets)
            {
                if (entry == null || string.IsNullOrEmpty(entry.id) || assets.ContainsKey(entry.id))
                {
                    continue;
                }

                assets.Add(entry.id, new PackageAsset
                {
                    Data = entry,
                    Completion = new TaskCompletionSource<PackageAsset>(
                        TaskCreationOptions.RunContinuationsAsynchronously),
                });
            }

            workers = new Task[WorkerCount];
            for (int i = 0; i < workers.Length; i++)
            {
                workers[i] = Task.Run(WorkerLoop);
            }
        }

        public int Count => assets.Count;

        public int LoadedCount => Volatile.Read(ref loaded);

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
        /// Queues an asset once and returns the shared completion. Unknown ids
        /// return null so a stale instance does not break the whole cell.
        /// </summary>
        public Task<PackageAsset> Request(string id)
        {
            if (disposed || id == null || !assets.TryGetValue(id, out PackageAsset asset))
            {
                return null;
            }

            if (asset.State == PackageAssetState.Ready)
            {
                return Task.FromResult(asset);
            }

            if (asset.State == PackageAssetState.Unloaded)
            {
                asset.State = PackageAssetState.Queued;
                try
                {
                    requests.Add(asset);
                }
                catch (InvalidOperationException)
                {
                    // Disposed between the check and the enqueue.
                }
            }

            return asset.Completion.Task;
        }

        /// <summary>
        /// Advances prepared imports. Called once per frame by the streamer;
        /// starting more imports than the frame can afford is pointless because
        /// each import checks the same budget at its break points.
        /// </summary>
        public void Update(PackageLoadBudget budget)
        {
            if (disposed || budget == null)
            {
                return;
            }

            // Start new imports while slots and frame time remain. The small
            // synchronous prefix of LoadGltfJson runs before the first break
            // point, so checking the budget after each start keeps at most one
            // prefix beyond the allowance.
            while (Volatile.Read(ref activeImports) < MaxConcurrentImports
                && budget.HasTime
                && prepared.TryDequeue(out PreparedAsset item))
            {
                Interlocked.Increment(ref activeImports);
                _ = ImportPreparedAsync(item, budget);
            }
        }

        /// <summary>
        /// Loads one asset synchronously through the same path, for the
        /// batch-mode smoke test which has no streaming update loop.
        /// </summary>
        public Task<PackageAsset> LoadNowAsync(string id)
        {
            if (id == null || !assets.TryGetValue(id, out PackageAsset asset))
            {
                return Task.FromResult<PackageAsset>(null);
            }

            if (asset.State == PackageAssetState.Ready)
            {
                return Task.FromResult(asset);
            }

            if (asset.LoadTask == null)
            {
                Interlocked.Increment(ref activeImports);
                asset.LoadTask = LoadNowInternalAsync(asset);
            }

            return asset.LoadTask;
        }

        private async Task<PackageAsset> LoadNowInternalAsync(PackageAsset asset)
        {
            PreparedAsset item = await Task.Run(() => Prepare(asset));
            await ImportPreparedAsync(item, new UninterruptedDeferAgent());
            return asset.IsReady ? asset : null;
        }

        private void WorkerLoop()
        {
            foreach (PackageAsset asset in requests.GetConsumingEnumerable())
            {
                if (disposed)
                {
                    return;
                }

                prepared.Enqueue(Prepare(asset));
            }
        }

        /// <summary>
        /// Runs on a worker thread: reads the glTF, its buffer and the PNGs it
        /// references. No Unity object is touched here.
        /// </summary>
        private PreparedAsset Prepare(PackageAsset asset)
        {
            var item = new PreparedAsset { Asset = asset };
            string file = asset.Data.file;
            if (string.IsNullOrEmpty(file))
            {
                item.Error = "no file in the manifest";
                return item;
            }

            string path = Path.Combine(
                sharedDirectory,
                file.Replace('/', Path.DirectorySeparatorChar));
            item.Path = path;

            try
            {
                if (!File.Exists(path))
                {
                    item.Error = $"missing asset file {path}";
                    return item;
                }

                if (path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    item.Json = File.ReadAllText(path);
                }
                else
                {
                    item.Bytes = File.ReadAllBytes(path);
                }

                string binPath = Path.ChangeExtension(path, ".bin");
                if (File.Exists(binPath))
                {
                    item.BinName = Path.GetFileName(binPath);
                    item.BinBytes = File.ReadAllBytes(binPath);
                }

                if (item.Json != null)
                {
                    textures.PrefetchFromJson(item.Json);
                }

                return item;
            }
            catch (Exception exception)
            {
                item.Error = exception.Message;
                return item;
            }
        }

        private async Task ImportPreparedAsync(PreparedAsset item, IDeferAgent deferAgent)
        {
            PackageAsset asset = item.Asset;
            GltfImport import = null;
            try
            {
                if (disposed)
                {
                    return;
                }

                if (asset.State == PackageAssetState.Ready)
                {
                    asset.Completion.TrySetResult(asset);
                    return;
                }

                if (asset.State == PackageAssetState.Failed)
                {
                    asset.Completion.TrySetResult(null);
                    return;
                }

                if (!string.IsNullOrEmpty(item.Error))
                {
                    throw new InvalidOperationException(item.Error);
                }

                ViewerDownloadProvider provider = new ViewerDownloadProvider(item.BinName, item.BinBytes);
                import = new GltfImport(provider, deferAgent);
                bool parsed = item.Json != null
                    ? await import.LoadGltfJson(item.Json, new Uri(item.Path))
                    : await import.Load(item.Bytes, new Uri(item.Path));
                if (!parsed)
                {
                    throw new InvalidOperationException($"glTFast could not parse {asset.Data.file}.");
                }

                if (disposed)
                {
                    import.Dispose();
                    return;
                }

                Mesh mesh = import.GetMesh(0, 0);
                if (mesh == null)
                {
                    throw new InvalidOperationException($"{asset.Data.file} contains no mesh.");
                }

                Material material = import.MaterialCount > 0
                    ? import.GetMaterial(0)
                    : import.GetDefaultMaterial();
                await textures.ApplyAsync(import, deferAgent as PackageLoadBudget);

                if (disposed)
                {
                    import.Dispose();
                    return;
                }

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
                Interlocked.Increment(ref loaded);
                asset.Completion.TrySetResult(asset);
            }
            catch (Exception exception)
            {
                import?.Dispose();
                asset.State = PackageAssetState.Failed;
                asset.Completion.TrySetResult(null);
                Debug.LogWarning($"[PackageAssets] {asset.Data.id}: {exception.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref activeImports);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            requests.CompleteAdding();

            try
            {
                Task.WaitAll(workers, WorkerShutdownTimeoutMs);
            }
            catch (AggregateException)
            {
                // Worker shutdown races are irrelevant once the level is gone.
            }

            foreach (PackageAsset asset in assets.Values)
            {
                asset.Import?.Dispose();
                asset.Import = null;
                asset.Mesh = null;
                asset.Material = null;
                asset.Completion.TrySetResult(null);
            }

            assets.Clear();
            prepared.Clear();
        }

        private sealed class PreparedAsset
        {
            public PackageAsset Asset;
            public string Path;
            public string Json;
            public byte[] Bytes;
            public string BinName;
            public byte[] BinBytes;
            public string Error;
        }
    }

    internal enum PackageAssetState
    {
        Unloaded,
        Queued,
        Ready,
        Failed,
    }

    public sealed class PackageAsset
    {
        internal PackageAssetManifest Data;
        internal GltfImport Import;
        internal Task<PackageAsset> LoadTask;
        internal TaskCompletionSource<PackageAsset> Completion;
        internal PackageAssetState State;

        public string Id => Data.id;
        public string MaterialName => Data.material;
        public long Triangles => Data.triangles;
        public Mesh Mesh { get; internal set; }
        public Material Material { get; internal set; }
        public bool IsReady => State == PackageAssetState.Ready;
    }
}
