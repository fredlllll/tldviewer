using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace TldLevels.Viewer
{
    /// <summary>
    /// Streams a <c>level.viewer</c> package written by the exporter.
    /// <para>
    /// Cells are all loaded when the package opens, nearest first with a
    /// frame-time budget, and resolved to a LOD file listed in the manifest.
    /// Distance only selects the LOD: a loaded cell is never removed, and flying
    /// away swaps its geometry to the coarser authored variant, so the level
    /// never disappears behind the camera. The viewer never guesses which LODs
    /// exist on disk.
    /// </para>
    /// <para>
    /// Cell files only contain terrain, sky and oversized geometry. Trees,
    /// houses and props are content-addressed assets in the shared library;
    /// each cell loads its <c>instances.json</c> and the viewer instances those
    /// shared meshes with a per-instance LOD.
    /// </para>
    /// </summary>
    public sealed class PackageStreamer : MonoBehaviour
    {
        [Tooltip("Distance between LOD switches, in cell widths.")]
        public float LodDistanceCells = 1.5f;

        [Tooltip("Maximum seconds per frame spent importing cells and shared assets.")]
        public float LoadFrameBudget = 1f / 30f;

        private PackageManifest manifest;
        private string packageDirectory;
        private string sharedDirectory;
        private Transform container;
        private PackageTextures textures;
        private PackageAssets assets;
        private PackageLoadBudget budget;
        private readonly List<Cell> cells = new List<Cell>();
        private readonly List<Cell> wanted = new List<Cell>();
        private int generation;
        private bool running;
        private int loading;
        private float nextEvaluateTime;
        private float nextLoadTime;

        public bool IsRunning => running;
        public string LevelName => manifest != null ? manifest.level : string.Empty;
        public int CellCount => cells.Count;
        public int LoadedCount { get; private set; }
        public int PendingCount => wanted.Count;
        public long LoadedTriangles { get; private set; }
        public int TextureCount => textures != null ? textures.Count : 0;
        public int AssetCount => assets != null ? assets.Count : 0;
        public int LoadedAssetCount => assets != null ? assets.LoadedCount : 0;

        public Bounds FocusBounds => manifest != null ? manifest.GetFocusBounds() : new Bounds(Vector3.zero, Vector3.one * 100f);

        public string StatusLine =>
            running
                ? $"{LevelName}: {LoadedCount}/{CellCount} cells, {PendingCount} queued, " +
                  $"{LoadedTriangles:N0} tris, {LoadedAssetCount}/{AssetCount} assets, {TextureCount} textures"
                : "No package loaded.";

        public void Initialize(PackageManifest packageManifest, string directory)
        {
            Shutdown();

            manifest = packageManifest;
            packageDirectory = directory;
            sharedDirectory = packageManifest.GetSharedDirectory(directory);
            textures = new PackageTextures(Path.Combine(sharedDirectory, "textures"));
            assets = new PackageAssets(sharedDirectory, packageManifest, textures);
            budget = new PackageLoadBudget(LoadFrameBudget);

            GameObject cellsRoot = new GameObject(packageManifest.level + " cells");
            cellsRoot.transform.SetParent(transform, false);
            container = cellsRoot.transform;

            cells.Clear();
            foreach (PackageCellManifest cellManifest in packageManifest.cells)
            {
                cells.Add(new Cell
                {
                    Data = cellManifest,
                    Center = cellManifest.UnityCenter,
                });
            }

            LoadedCount = 0;
            LoadedTriangles = 0;
            wanted.Clear();
            generation++;
            running = true;
            nextEvaluateTime = 0f;
            nextLoadTime = 0f;
        }

        public void Shutdown()
        {
            generation++;
            running = false;
            loading = 0;

            foreach (Cell cell in cells)
            {
                ReleaseCell(cell);
            }

            cells.Clear();
            wanted.Clear();
            LoadedCount = 0;
            LoadedTriangles = 0;

            if (container != null)
            {
                Destroy(container.gameObject);
                container = null;
            }

            assets?.Dispose();
            assets = null;
            textures?.Dispose();
            textures = null;
            budget = null;
            manifest = null;
            packageDirectory = null;
            sharedDirectory = null;
        }

        private void OnDestroy()
        {
            Shutdown();
        }

        private void Update()
        {
            if (!running)
            {
                return;
            }

            budget?.BeginFrame();

            if (Time.unscaledTime >= nextEvaluateTime)
            {
                Evaluate();
                nextEvaluateTime = Time.unscaledTime + 0.25f;
            }

            if (loading == 0 && Time.unscaledTime >= nextLoadTime)
            {
                TryStartLoad();
            }

            // Cell and asset imports draw from the same frame allowance.
            assets?.Update(budget);
        }

        private void Evaluate()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                return;
            }

            Vector3 position = camera.transform.position;
            wanted.Clear();

            foreach (Cell cell in cells)
            {
                if (cell.State == CellState.Loading)
                {
                    continue;
                }

                float distance = Vector3.Distance(position, cell.Center);

                if (cell.State == CellState.Loaded)
                {
                    // Loaded cells are never released; a different LOD is
                    // swapped into the same cell instead. The hysteresis keeps
                    // the camera from thrashing across a LOD boundary.
                    int desiredLevel = DesiredLod(distance);
                    PackageLodManifest target = cell.Data.SelectLod(desiredLevel);
                    bool lodChanged = target != null && target.level != cell.LoadedLod
                        && Mathf.Abs(distance - cell.LoadedDistance) > GetLodStep() * 0.25f;
                    if (lodChanged && cell.NextRetryTime <= Time.unscaledTime)
                    {
                        cell.PendingDistance = distance;
                        wanted.Add(cell);
                    }
                }
                else if (cell.NextRetryTime <= Time.unscaledTime)
                {
                    // Every cell of the package is loaded, nearest first; there
                    // is no streaming radius to fly into.
                    cell.PendingDistance = distance;
                    wanted.Add(cell);
                }
            }

            foreach (Cell cell in cells)
            {
                if (cell.State == CellState.Loaded)
                {
                    UpdateInstanceLods(cell, position);
                }
            }

            wanted.Sort((a, b) => a.PendingDistance.CompareTo(b.PendingDistance));
            nextLoadTime = 0f;
        }

        private int DesiredLod(float distance)
        {
            if (manifest == null || manifest.lodCount <= 1)
            {
                return 0;
            }

            float step = GetLodStep();
            int level = Mathf.FloorToInt(distance / step);
            return Mathf.Clamp(level, 0, manifest.lodCount - 1);
        }

        private float GetLodStep() => Mathf.Max(manifest != null ? manifest.cellSize : 256f, 1f) * LodDistanceCells;

        private void TryStartLoad()
        {
            if (wanted.Count == 0)
            {
                return;
            }

            Cell cell = wanted[0];
            wanted.RemoveAt(0);
            if (cell.State == CellState.Loading)
            {
                return;
            }

            loading++;
            _ = LoadCellAsync(cell);
        }

        private async Task LoadCellAsync(Cell cell)
        {
            int loadGeneration = generation;

            // A loaded cell is never torn down before its replacement is ready:
            // a LOD swap builds the coarser variant alongside the old one, so
            // the level never shows a hole while the file is read.
            bool replacing = cell.State == CellState.Loaded;
            GameObject previousRoot = cell.Root;
            GltfImport previousImport = cell.Import;
            int previousLod = cell.LoadedLod;
            float previousDistance = cell.LoadedDistance;
            long previousTriangles = cell.LoadedTriangles;
            InstanceRuntime[] previousInstances = cell.Instances.ToArray();

            cell.State = CellState.Loading;

            GameObject root = null;
            GltfImport import = null;

            try
            {
                root = new GameObject(cell.Data.id);
                root.transform.SetParent(container, false);

                PackageLodManifest lod = cell.Data.SelectLod(DesiredLod(cell.PendingDistance));
                PreparedCell prepared = await Task.Run(() => PrepareCell(cell, lod));

                if (loadGeneration != generation)
                {
                    Destroy(root);
                    return;
                }

                if (prepared.Json != null)
                {
                    // Read the PNGs beside the main-thread import. The budgeted
                    // apply below falls back to disk for any that lose the race.
                    _ = Task.Run(() => textures.PrefetchFromJson(prepared.Json));
                }

                if (prepared.InstancesError != null)
                {
                    Debug.LogWarning($"[PackageStreamer] {cell.Data.id}: {prepared.InstancesError}");
                }

                if (prepared.Json != null || prepared.Bytes != null)
                {
                    ViewerDownloadProvider provider = new ViewerDownloadProvider(
                        prepared.BinName,
                        prepared.BinBytes);
                    import = new GltfImport(provider, budget);
                    bool loaded = prepared.Json != null
                        ? await import.LoadGltfJson(prepared.Json, new Uri(prepared.Path))
                        : await import.Load(prepared.Bytes, new Uri(prepared.Path));
                    if (!loaded)
                    {
                        throw new InvalidOperationException($"glTFast could not parse {prepared.Path}.");
                    }

                    if (loadGeneration != generation)
                    {
                        import.Dispose();
                        Destroy(root);
                        return;
                    }

                    if (!await import.InstantiateMainSceneAsync(root.transform))
                    {
                        throw new InvalidOperationException($"glTFast could not instantiate {prepared.Path}.");
                    }

                    await textures.ApplyAsync(import, budget);
                }

                if (loadGeneration != generation)
                {
                    import?.Dispose();
                    Destroy(root);
                    return;
                }

                cell.Instances.Clear();
                long instanceTriangles = await LoadInstancesAsync(
                    cell,
                    root,
                    prepared.InstancesJson,
                    loadGeneration);

                if (loadGeneration != generation)
                {
                    import?.Dispose();
                    Destroy(root);
                    return;
                }

                if (replacing)
                {
                    // Retire the old geometry now that the new one is complete.
                    // Hiding it first keeps the last frame from showing a
                    // renderer whose meshes were just disposed.
                    if (previousRoot != null)
                    {
                        previousRoot.SetActive(false);
                        Destroy(previousRoot);
                    }

                    previousImport?.Dispose();
                    LoadedCount = Mathf.Max(0, LoadedCount - 1);
                    LoadedTriangles -= previousTriangles;
                }

                cell.Root = root;
                cell.Import = import;
                cell.LoadedLod = lod?.level ?? -1;
                cell.LoadedDistance = cell.PendingDistance;
                cell.LoadedTriangles = (lod?.triangles ?? 0) + instanceTriangles;
                cell.State = CellState.Loaded;
                LoadedCount++;
                LoadedTriangles += cell.LoadedTriangles;

                Camera camera = Camera.main;
                if (camera != null)
                {
                    UpdateInstanceLods(cell, camera.transform.position);
                }
            }
            catch (Exception exception)
            {
                if (root != null)
                {
                    Destroy(root);
                }

                import?.Dispose();
                cell.Instances.Clear();

                if (replacing)
                {
                    // Keep the previous variant on screen and try the swap again
                    // after a pause instead of leaving a gap in the level.
                    cell.Instances.AddRange(previousInstances);
                    cell.LoadedLod = previousLod;
                    cell.LoadedDistance = previousDistance;
                    cell.LoadedTriangles = previousTriangles;
                    cell.State = CellState.Loaded;
                }
                else
                {
                    cell.Root = null;
                    cell.Import = null;
                    cell.State = CellState.Idle;
                }

                cell.NextRetryTime = Time.unscaledTime + 5f;
                Debug.LogWarning($"[PackageStreamer] {cell.Data.id}: {exception.Message}");
            }
            finally
            {
                loading = Mathf.Max(0, loading - 1);
            }
        }

        /// <summary>
        /// Runs on a worker thread: reads the cell glTF (or GLB), its buffer
        /// and <c>instances.json</c> so the main thread only has to import.
        /// </summary>
        private PreparedCell PrepareCell(Cell cell, PackageLodManifest lod)
        {
            PreparedCell prepared = new PreparedCell();
            if (lod != null && !string.IsNullOrEmpty(lod.file))
            {
                string path = Path.Combine(
                    packageDirectory,
                    lod.file.Replace('/', Path.DirectorySeparatorChar));
                prepared.Path = path;

                if (path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
                {
                    prepared.Json = File.ReadAllText(path);
                }
                else
                {
                    prepared.Bytes = File.ReadAllBytes(path);
                }

                string binPath = Path.ChangeExtension(path, ".bin");
                if (File.Exists(binPath))
                {
                    prepared.BinName = Path.GetFileName(binPath);
                    prepared.BinBytes = File.ReadAllBytes(binPath);
                }
            }

            if (!string.IsNullOrEmpty(cell.Data.instances))
            {
                string path = Path.Combine(
                    packageDirectory,
                    cell.Data.instances.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    prepared.InstancesJson = File.ReadAllText(path);
                }
                else
                {
                    prepared.InstancesError = $"missing {cell.Data.instances}";
                }
            }

            return prepared;
        }

        /// <summary>
        /// Creates the empty instance roots for a cell. Assets are filled in as
        /// soon as they have loaded, so terrain is visible immediately. Long
        /// instance lists yield to the next frame when the budget is spent.
        /// </summary>
        private async Task<long> LoadInstancesAsync(
            Cell cell,
            GameObject cellRoot,
            string json,
            int loadGeneration)
        {
            if (string.IsNullOrEmpty(json))
            {
                return 0;
            }

            PackageInstancesFile file;
            try
            {
                file = JsonUtility.FromJson<PackageInstancesFile>(json);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PackageStreamer] {cell.Data.id}: {exception.Message}");
                return 0;
            }

            long triangles = 0;
            if (file?.instances == null)
            {
                return 0;
            }

            foreach (PackageInstance data in file.instances)
            {
                if (data == null)
                {
                    continue;
                }

                if (budget != null && budget.ShouldDefer())
                {
                    await Task.Yield();
                    if (loadGeneration != generation)
                    {
                        return 0;
                    }
                }

                GameObject root = new GameObject(string.IsNullOrEmpty(data.name) ? "instance" : data.name);
                root.transform.SetParent(cellRoot.transform, false);
                root.transform.localPosition = data.UnityPosition;
                root.transform.localRotation = data.UnityRotation;
                root.transform.localScale = data.UnityScale;

                PackageInstanceLod lod0 = data.SelectLod(0);
                if (lod0 != null)
                {
                    triangles += lod0.triangles;
                }

                cell.Instances.Add(new InstanceRuntime
                {
                    Data = data,
                    Root = root,
                    Position = root.transform.position,
                });
            }

            return triangles;
        }

        private void UpdateInstanceLods(Cell cell, Vector3 cameraPosition)
        {
            foreach (InstanceRuntime instance in cell.Instances)
            {
                float distance = Vector3.Distance(cameraPosition, instance.Position);
                int desired = DesiredLod(distance);
                PackageInstanceLod variant = instance.Data.SelectLod(desired);
                if (variant == null || variant.level == instance.AppliedLevel)
                {
                    continue;
                }

                if (!TryApplyVariant(instance, variant))
                {
                    continue;
                }

                instance.AppliedLevel = variant.level;
            }
        }

        /// <summary>
        /// Requests every asset of a variant and swaps the instance children
        /// once all of them are resident. Returns false while assets are still
        /// loading, leaving the previous LOD visible instead of popping empty.
        /// </summary>
        private bool TryApplyVariant(InstanceRuntime instance, PackageInstanceLod variant)
        {
            if (variant.assets == null || variant.assets.Length == 0)
            {
                return false;
            }

            PackageAsset[] loaded = new PackageAsset[variant.assets.Length];
            for (int i = 0; i < variant.assets.Length; i++)
            {
                string id = variant.assets[i];
                PackageAsset asset = assets.Get(id);
                if (asset == null)
                {
                    assets.Request(id);
                    return false;
                }

                loaded[i] = asset;
            }

            while (instance.Children.Count > loaded.Length)
            {
                int last = instance.Children.Count - 1;
                Destroy(instance.Children[last]);
                instance.Children.RemoveAt(last);
            }

            for (int i = 0; i < loaded.Length; i++)
            {
                GameObject child;
                if (i < instance.Children.Count)
                {
                    child = instance.Children[i];
                }
                else
                {
                    child = new GameObject($"{instance.Data.name}_{i}");
                    child.transform.SetParent(instance.Root.transform, false);
                    child.AddComponent<MeshFilter>();
                    child.AddComponent<MeshRenderer>();
                    instance.Children.Add(child);
                }

                child.SetActive(true);
                child.GetComponent<MeshFilter>().sharedMesh = loaded[i].Mesh;

                MeshRenderer renderer = child.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = loaded[i].Material;
                renderer.enabled = true;
            }

            return true;
        }

        private void ReleaseCell(Cell cell)
        {
            if (cell.State == CellState.Loaded)
            {
                LoadedCount = Mathf.Max(0, LoadedCount - 1);
                LoadedTriangles -= cell.LoadedTriangles;
                if (LoadedTriangles < 0)
                {
                    LoadedTriangles = 0;
                }
            }

            if (cell.Root != null)
            {
                Destroy(cell.Root);
            }

            cell.Import?.Dispose();
            cell.Root = null;
            cell.Import = null;
            cell.Instances.Clear();
            cell.LoadedLod = -1;
            cell.LoadedDistance = 0f;
            cell.LoadedTriangles = 0;
            cell.State = CellState.Idle;
        }

        private sealed class PreparedCell
        {
            public string Path;
            public string Json;
            public byte[] Bytes;
            public string BinName;
            public byte[] BinBytes;
            public string InstancesJson;
            public string InstancesError;
        }

        private enum CellState
        {
            Idle,
            Loading,
            Loaded,
        }

        private sealed class Cell
        {
            public PackageCellManifest Data;
            public Vector3 Center;
            public CellState State;
            public GameObject Root;
            public GltfImport Import;
            public int LoadedLod = -1;
            public float LoadedDistance;
            public long LoadedTriangles;
            public float PendingDistance;
            public float NextRetryTime;
            public readonly List<InstanceRuntime> Instances = new List<InstanceRuntime>();
        }

        private sealed class InstanceRuntime
        {
            public PackageInstance Data;
            public GameObject Root;
            public Vector3 Position;
            public int AppliedLevel = -1;
            public readonly List<GameObject> Children = new List<GameObject>();
        }
    }
}
