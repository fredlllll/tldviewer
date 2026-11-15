using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GLTFast;
using TldLevels.Viewer;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-mode smoke test for the streamed viewer package path: shared asset
/// library, per-instance LOD references, shared texture cache and per-LOD cell
/// completeness. Run with:
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
        PackageAssets assets = null;
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
            string shared = manifest.GetSharedDirectory(package);
            Debug.Log($"[smoke] manifest v{manifest.formatVersion}: {manifest.level}, {manifest.cells.Length} cells, " +
                $"{manifest.lodCount} LOD(s), {manifest.assets?.Length ?? 0} shared assets, " +
                $"focus {focus.center} size {focus.size}");

            if (manifest.formatVersion >= 2)
            {
                if (manifest.assets == null)
                {
                    throw new InvalidOperationException("Version 2 manifest has no asset list.");
                }

                if (!Directory.Exists(shared))
                {
                    throw new InvalidOperationException($"Shared asset root does not exist: {shared}");
                }
            }

            // Prefer the cell with the most authored LODs and instances: that
            // is where incomplete cell files and bad instance transforms show up.
            PackageCellManifest cell = ChooseCell(manifest);
            if (cell == null)
            {
                throw new InvalidOperationException("No cell with an available LOD or instances.");
            }

            // A cell holds geometry near its own grid position. A transform that
            // swaps scale and translation (or otherwise explodes) shows up here
            // long before anyone notices in the renderer.
            Bounds world = manifest.GetWorldBounds();
            Bounds allowed = new Bounds(world.center, world.size + Vector3.one * manifest.cellSize * 2f);

            textures = new PackageTextures(Path.Combine(shared, "textures"));
            assets = new PackageAssets(shared, manifest, textures);

            int expectedRenderers = -1;
            if (cell.lods != null)
            {
                foreach (PackageLodManifest lod in cell.lods)
                {
                    LodResult result = await LoadLodAsync(package, textures, cell, lod, allowed, manifest.cellSize);
                    Debug.Log($"[smoke] {cell.id} lod{lod.level}: {result.Renderers} renderers, " +
                        $"{result.MaterialSlots} material slots, {result.BaseTextured} shared base textures, " +
                        $"{result.Placeholders} placeholders, {lod.triangles:N0} tris, bounds {result.Bounds}");

                    if (expectedRenderers < 0)
                    {
                        expectedRenderers = result.Renderers;
                        if (result.MaterialSlots > 0 && result.BaseTextured == 0)
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
            }

            int instanceCount = 0;
            if (!string.IsNullOrEmpty(cell.instances))
            {
                instanceCount = await CheckInstancesAsync(
                    package,
                    manifest,
                    assets,
                    cell,
                    allowed);
            }

            Debug.Log($"[smoke] {cell.id}: {instanceCount} instance(s), " +
                $"{assets.LoadedCount}/{assets.Count} shared asset(s) loaded, {textures.Count} cached textures");
            exitCode = 0;
            Debug.Log("[smoke] PASS");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[smoke] FAIL: {exception}");
        }
        finally
        {
            assets?.Dispose();
            textures?.Dispose();
            EditorApplication.update -= Watchdog;
            EditorApplication.Exit(exitCode);
        }
    }

    private static PackageCellManifest ChooseCell(PackageManifest manifest)
    {
        PackageCellManifest best = null;
        int bestScore = -1;
        foreach (PackageCellManifest candidate in manifest.cells)
        {
            int score = (candidate.lods == null ? 0 : candidate.lods.Length) * 100000
                + candidate.instanceCount;
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static async Task<int> CheckInstancesAsync(
        string package,
        PackageManifest manifest,
        PackageAssets assets,
        PackageCellManifest cell,
        Bounds allowed)
    {
        string path = Path.Combine(package, cell.instances.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Cell {cell.id} references missing {cell.instances}.");
        }

        PackageInstancesFile file = JsonUtility.FromJson<PackageInstancesFile>(File.ReadAllText(path));
        if (file?.instances == null || file.instances.Length == 0)
        {
            throw new InvalidOperationException($"Cell {cell.id} has an empty instance list.");
        }

        HashSet<string> manifestIds = new HashSet<string>(
            (manifest.assets ?? Array.Empty<PackageAssetManifest>())
                .Where(asset => asset != null && !string.IsNullOrEmpty(asset.id))
                .Select(asset => asset.id));

        HashSet<string> referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (PackageInstance instance in file.instances)
        {
            if (instance?.lods == null)
            {
                continue;
            }

            foreach (PackageInstanceLod lod in instance.lods)
            {
                if (lod?.assets == null)
                {
                    continue;
                }

                foreach (string id in lod.assets)
                {
                    if (string.IsNullOrEmpty(id))
                    {
                        continue;
                    }

                    if (!manifestIds.Contains(id))
                    {
                        throw new InvalidOperationException(
                            $"Instance {instance.name} references asset {id}, which is not in the manifest.");
                    }

                    referenced.Add(id);
                }
            }
        }

        // Load a representative sample rather than the whole cell: the point is
        // to prove the ids, paths, import and texture patch all work.
        string[] sample = referenced.Take(24).ToArray();
        foreach (string id in sample)
        {
            Task<PackageAsset> task = assets.Request(id);
            if (task != null)
            {
                await task;
            }

            PackageAsset asset = assets.Get(id);
            if (asset == null)
            {
                throw new InvalidOperationException($"Shared asset {id} did not load.");
            }

            if (asset.Mesh == null || asset.Material == null)
            {
                throw new InvalidOperationException($"Shared asset {id} has no mesh or material.");
            }
        }

        int textured = 0;
        int placeholders = 0;
        foreach (string id in sample)
        {
            Material material = assets.Get(id).Material;
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
                placeholders++;
            }
            else
            {
                textured++;
            }
        }

        if (placeholders != 0)
        {
            throw new InvalidOperationException($"Sampled instance assets still use {placeholders} placeholder texture(s).");
        }

        // Build a small subset with the real transforms and verify the bounds
        // land inside the level. This mirrors what PackageStreamer does.
        GameObject root = new GameObject("SmokeInstances");
        try
        {
            int built = 0;
            foreach (PackageInstance instance in file.instances)
            {
                PackageInstanceLod lod = instance.SelectLod(0);
                if (lod?.assets == null || lod.assets.Length == 0)
                {
                    continue;
                }

                PackageAsset asset = assets.Get(lod.assets[0]);
                if (asset == null)
                {
                    continue;
                }

                GameObject child = new GameObject(instance.name);
                child.transform.SetParent(root.transform, false);
                child.transform.localPosition = instance.UnityPosition;
                child.transform.localRotation = instance.UnityRotation;
                child.transform.localScale = instance.UnityScale;
                child.AddComponent<MeshFilter>().sharedMesh = asset.Mesh;
                child.AddComponent<MeshRenderer>().sharedMaterial = asset.Material;

                if (++built >= 16)
                {
                    break;
                }
            }

            if (built > 0)
            {
                Bounds bounds = GetBounds(root);
                if (!allowed.Contains(bounds.center))
                {
                    throw new InvalidOperationException(
                        $"Instance sample bounds center {bounds.center} is outside the level bounds {allowed}.");
                }
            }
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(root);
        }

        Debug.Log($"[smoke] {cell.id}: {file.instances.Length} instance(s), {referenced.Count} referenced asset(s), " +
            $"{sample.Length} sampled, {textured} textured material(s)");

        return file.instances.Length;
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
