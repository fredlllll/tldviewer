using System;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using TldLevels.Viewer;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-mode smoke test for the streamed viewer package path, shared texture
/// cache and per-LOD cell completeness. Run with:
/// <c>Unity.exe -batchmode -nographics -projectPath src/TldLevels.Viewer
/// -executeMethod PackageSmokeTest.Run -logFile smoke.log</c>
/// </summary>
public static class PackageSmokeTest
{
    private const string DefaultPackage = @"\\VBoxSvr\TLDLevels\tldlevels\levels\level1.viewer";
    private static readonly DateTime Deadline = DateTime.UtcNow.AddMinutes(5);
    private static bool running;

    [MenuItem("Tools/TLD Levels/Smoke Test Package")]
    public static void RunMenu() => Run();

    public static void Run()
    {
        if (running)
        {
            return;
        }

        running = true;
        EditorApplication.update += Watchdog;
        _ = RunAsync();
    }

    private static void Watchdog()
    {
        if (DateTime.UtcNow < Deadline)
        {
            return;
        }

        Debug.LogError("[smoke] timed out");
        EditorApplication.update -= Watchdog;
        EditorApplication.Exit(2);
    }

    private static async Task RunAsync()
    {
        int exitCode = 1;
        PackageTextures textures = null;
        try
        {
            string package = Environment.GetEnvironmentVariable("TLD_SMOKE_PACKAGE");
            if (string.IsNullOrEmpty(package))
            {
                package = DefaultPackage;
            }

            if (!PackageManifest.TryLoad(package, out PackageManifest manifest, out string error))
            {
                throw new InvalidOperationException(error);
            }

            Bounds focus = manifest.GetFocusBounds();
            Debug.Log($"[smoke] manifest: {manifest.level}, {manifest.cells.Length} cells, " +
                $"{manifest.lodCount} LOD(s), focus {focus.center} size {focus.size}");

            // Prefer the cell with the most authored LODs: that is the case where
            // incomplete LOD files (missing terrain or LOD0-only props) show up.
            PackageCellManifest cell = null;
            long bestTriangles = -1;
            foreach (PackageCellManifest candidate in manifest.cells)
            {
                PackageLodManifest candidateLod = candidate.SelectLod(0);
                if (candidateLod == null)
                {
                    continue;
                }

                int candidateLods = candidate.lods == null ? 0 : candidate.lods.Length;
                int bestLods = cell?.lods == null ? 0 : cell.lods.Length;
                if (cell == null || candidateLods > bestLods
                    || candidateLods == bestLods && candidateLod.triangles > bestTriangles)
                {
                    bestTriangles = candidateLod.triangles;
                    cell = candidate;
                }
            }

            if (cell == null)
            {
                throw new InvalidOperationException("No cell with an available LOD.");
            }

            // A cell holds geometry near its own grid position. A transform that
            // swaps scale and translation (or otherwise explodes) shows up here
            // long before anyone notices in the renderer.
            Bounds world = manifest.GetWorldBounds();
            Bounds allowed = new Bounds(world.center, world.size + Vector3.one * manifest.cellSize * 2f);

            textures = new PackageTextures(package);
            int expectedRenderers = -1;
            foreach (PackageLodManifest lod in cell.lods)
            {
                LodResult result = await LoadLodAsync(package, textures, cell, lod, allowed, manifest.cellSize);
                Debug.Log($"[smoke] {cell.id} lod{lod.level}: {result.Renderers} renderers, " +
                    $"{result.MaterialSlots} material slots, {result.BaseTextured} shared base textures, " +
                    $"{result.Placeholders} placeholders, {lod.triangles:N0} tris, bounds {result.Bounds}");

                if (expectedRenderers < 0)
                {
                    expectedRenderers = result.Renderers;
                    if (result.MaterialSlots == 0 || result.BaseTextured == 0)
                    {
                        throw new InvalidOperationException("Cell loaded but shared textures were not applied.");
                    }
                }
                else if (result.Renderers != expectedRenderers)
                {
                    throw new InvalidOperationException(
                        $"lod{lod.level} has {result.Renderers} renderers, lod{cell.lods[0].level} has {expectedRenderers}: " +
                        "the cell LOD is missing LOD0-only geometry.");
                }

                if (result.Placeholders != 0)
                {
                    throw new InvalidOperationException($"lod{lod.level} still uses placeholder textures.");
                }
            }

            Debug.Log($"[smoke] {textures.Count} cached textures across {cell.lods.Length} LOD(s)");
            exitCode = 0;
            Debug.Log("[smoke] PASS");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[smoke] FAIL: {exception}");
        }
        finally
        {
            textures?.Dispose();
            EditorApplication.update -= Watchdog;
            EditorApplication.Exit(exitCode);
        }
    }

    private static async Task<LodResult> LoadLodAsync(
        string package,
        PackageTextures textures,
        PackageCellManifest cell,
        PackageLodManifest lod,
        Bounds allowed,
        float cellSize)
    {
        string path = Path.Combine(package, lod.file.Replace('/', Path.DirectorySeparatorChar));
        GameObject root = null;
        GltfImport import = null;
        try
        {
            import = new GltfImport(new ViewerDownloadProvider(), new UninterruptedDeferAgent());
            if (!await import.LoadFile(path, new Uri(path)))
            {
                throw new InvalidOperationException($"glTFast failed to load {path}.");
            }

            root = new GameObject("SmokeCell");
            if (!await import.InstantiateMainSceneAsync(root.transform))
            {
                throw new InvalidOperationException($"glTFast failed to instantiate {path}.");
            }

            textures.Apply(import);

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            LodResult result = new LodResult { Renderers = renderers.Length };
            foreach (Renderer renderer in renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    result.MaterialSlots++;
                    if (!material.HasProperty("baseColorTexture"))
                    {
                        continue;
                    }

                    Texture texture = material.GetTexture("baseColorTexture");
                    if (texture == null)
                    {
                        continue;
                    }

                    if (texture.width == 1 && texture.height == 1)
                    {
                        result.Placeholders++;
                    }
                    else
                    {
                        result.BaseTextured++;
                    }
                }
            }

            result.Bounds = GetBounds(root);
            if (renderers.Length == 0)
            {
                throw new InvalidOperationException($"{path} contains no renderers.");
            }

            if (!allowed.Contains(result.Bounds.center))
            {
                throw new InvalidOperationException(
                    $"{path} bounds center {result.Bounds.center} is outside the level bounds {allowed}.");
            }

            float extent = Mathf.Max(result.Bounds.size.x, Mathf.Max(result.Bounds.size.y, result.Bounds.size.z));
            if (extent > Mathf.Max(cellSize, 1f) * 8f)
            {
                throw new InvalidOperationException(
                    $"{path} bounds {result.Bounds.size} are implausibly large for the grid.");
            }

            return result;
        }
        finally
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            import?.Dispose();
        }
    }

    private static Bounds GetBounds(GameObject root)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
        {
            return new Bounds();
        }

        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private sealed class LodResult
    {
        public int Renderers;
        public int MaterialSlots;
        public int BaseTextured;
        public int Placeholders;
        public Bounds Bounds;
    }
}
