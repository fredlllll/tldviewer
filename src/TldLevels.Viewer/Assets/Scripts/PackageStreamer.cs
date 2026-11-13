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
    /// Cells are loaded nearest-first with a frame-time budget, unloaded once
    /// the camera is far enough away, and resolved to a LOD file listed in the
    /// manifest. The viewer never guesses which LODs exist on disk.
    /// </para>
    /// </summary>
    public sealed class PackageStreamer : MonoBehaviour
    {
        [Tooltip("Cells whose centre is inside this radius are loaded (metres).")]
        public float LoadRadius = 1200f;

        [Tooltip("Loaded cells beyond this radius are released (metres).")]
        public float UnloadRadius = 1700f;

        [Tooltip("Distance between LOD switches, in cell widths.")]
        public float LodDistanceCells = 1.5f;

        [Range(0.05f, 1f)]
        [Tooltip("Fraction of the frame time glTFast may spend importing a cell.")]
        public float ImportFrameBudget = 0.25f;

        private PackageManifest manifest;
        private string packageDirectory;
        private Transform container;
        private PackageTextures textures;
        private ViewerDownloadProvider downloadProvider;
        private TimeBudgetPerFrameDeferAgent deferAgent;
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

        public Bounds FocusBounds => manifest != null ? manifest.GetFocusBounds() : new Bounds(Vector3.zero, Vector3.one * 100f);

        public string StatusLine =>
            running
                ? $"{LevelName}: {LoadedCount}/{CellCount} cells, {PendingCount} queued, " +
                  $"{LoadedTriangles:N0} tris, {TextureCount} shared texture(s)"
                : "No package loaded.";

        public void Initialize(PackageManifest packageManifest, string directory)
        {
            Shutdown();

            manifest = packageManifest;
            packageDirectory = directory;
            textures = new PackageTextures(directory);
            downloadProvider = new ViewerDownloadProvider();
            deferAgent = GetComponent<TimeBudgetPerFrameDeferAgent>();
            if (deferAgent == null)
            {
                deferAgent = gameObject.AddComponent<TimeBudgetPerFrameDeferAgent>();
            }

            deferAgent.SetFrameBudget(ImportFrameBudget);

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

            textures?.Dispose();
            textures = null;
            manifest = null;
            packageDirectory = null;
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

            if (Time.unscaledTime >= nextEvaluateTime)
            {
                Evaluate();
                nextEvaluateTime = Time.unscaledTime + 0.25f;
            }

            if (loading == 0 && Time.unscaledTime >= nextLoadTime)
            {
                TryStartLoad();
            }
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
                    int desiredLevel = DesiredLod(distance);
                    PackageLodManifest target = cell.Data.SelectLod(desiredLevel);
                    bool lodChanged = target != null && target.level != cell.LoadedLod
                        && Mathf.Abs(distance - cell.LoadedDistance) > GetLodStep() * 0.25f;
                    if (distance > UnloadRadius || lodChanged)
                    {
                        ReleaseCell(cell);
                    }
                }

                if (cell.State == CellState.Idle && cell.NextRetryTime <= Time.unscaledTime && distance <= LoadRadius)
                {
                    cell.PendingDistance = distance;
                    wanted.Add(cell);
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
            if (cell.State != CellState.Idle)
            {
                return;
            }

            loading++;
            _ = LoadCellAsync(cell);
        }

        private async Task LoadCellAsync(Cell cell)
        {
            int loadGeneration = generation;
            cell.State = CellState.Loading;

            PackageLodManifest lod = cell.Data.SelectLod(DesiredLod(cell.PendingDistance));
            if (lod == null)
            {
                cell.State = CellState.Idle;
                loading--;
                return;
            }

            string path = Path.Combine(packageDirectory, lod.file.Replace('/', Path.DirectorySeparatorChar));
            GltfImport import = null;
            GameObject root = null;

            try
            {
                import = new GltfImport(downloadProvider, deferAgent);
                if (!await import.LoadFile(path, new Uri(path)))
                {
                    throw new InvalidOperationException($"glTFast could not parse {lod.file}.");
                }

                if (loadGeneration != generation)
                {
                    import.Dispose();
                    return;
                }

                root = new GameObject(cell.Data.id);
                root.transform.SetParent(container, false);
                if (!await import.InstantiateMainSceneAsync(root.transform))
                {
                    throw new InvalidOperationException($"glTFast could not instantiate {lod.file}.");
                }

                if (loadGeneration != generation)
                {
                    Destroy(root);
                    import.Dispose();
                    return;
                }

                textures.Apply(import);
                cell.Root = root;
                cell.Import = import;
                cell.LoadedLod = lod.level;
                cell.LoadedDistance = cell.PendingDistance;
                cell.LoadedTriangles = lod.triangles;
                cell.State = CellState.Loaded;
                LoadedCount++;
                LoadedTriangles += lod.triangles;
            }
            catch (Exception exception)
            {
                if (root != null)
                {
                    Destroy(root);
                }

                import?.Dispose();
                cell.State = CellState.Idle;
                cell.NextRetryTime = Time.unscaledTime + 5f;
                Debug.LogWarning($"[PackageStreamer] {cell.Data.id}: {exception.Message}");
            }
            finally
            {
                loading = Mathf.Max(0, loading - 1);
            }
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
            cell.LoadedLod = -1;
            cell.LoadedDistance = 0f;
            cell.LoadedTriangles = 0;
            cell.State = CellState.Idle;
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
        }
    }
}
