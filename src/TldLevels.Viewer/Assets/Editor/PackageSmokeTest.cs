using System;
using System.IO;
using System.Threading.Tasks;
using GLTFast;
using TldLevels.Viewer;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Batch-mode smoke test for the streamed viewer package path and the shared
/// texture cache. Run with:
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
        GameObject root = null;
        PackageTextures textures = null;
        GltfImport import = null;
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

            PackageCellManifest cell = null;
            PackageLodManifest lod = null;
            long bestTriangles = -1;
            foreach (PackageCellManifest candidate in manifest.cells)
            {
                PackageLodManifest candidateLod = candidate.SelectLod(0);
                if (candidateLod != null && candidateLod.triangles > bestTriangles)
                {
                    bestTriangles = candidateLod.triangles;
                    cell = candidate;
                    lod = candidateLod;
                }
            }

            if (cell == null || lod == null)
            {
                throw new InvalidOperationException("No cell with an available LOD.");
            }

            string path = Path.Combine(package, lod.file.Replace('/', Path.DirectorySeparatorChar));
            Debug.Log($"[smoke] loading {cell.id} lod{lod.level} ({lod.triangles:N0} tris) from {path}");

            textures = new PackageTextures(package);
            import = new GltfImport(new ViewerDownloadProvider(), new UninterruptedDeferAgent());
            if (!await import.LoadFile(path, new Uri(path)))
            {
                throw new InvalidOperationException("glTFast failed to load the cell.");
            }

            root = new GameObject("SmokeCell");
            if (!await import.InstantiateMainSceneAsync(root.transform))
            {
                throw new InvalidOperationException("glTFast failed to instantiate the cell.");
            }

            textures.Apply(import);

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>();
            int materialSlots = 0;
            int baseTextured = 0;
            int placeholderTextures = 0;
            foreach (Renderer renderer in renderers)
            {
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        continue;
                    }

                    materialSlots++;
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
                        placeholderTextures++;
                    }
                    else
                    {
                        baseTextured++;
                    }
                }
            }

            Debug.Log($"[smoke] cell instantiated: {renderers.Length} renderers, {materialSlots} material slots, " +
                $"{baseTextured} shared base textures, {placeholderTextures} placeholders, " +
                $"{textures.Count} cached textures");
            Debug.Log($"[smoke] bounds: {GetBounds(root)}");

            if (renderers.Length == 0 || materialSlots == 0 || textures.Count == 0 || baseTextured == 0)
            {
                throw new InvalidOperationException("Cell loaded but shared textures were not applied.");
            }

            exitCode = 0;
            Debug.Log("[smoke] PASS");
        }
        catch (Exception exception)
        {
            Debug.LogError($"[smoke] FAIL: {exception}");
        }
        finally
        {
            if (root != null)
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            import?.Dispose();
            textures?.Dispose();
            EditorApplication.update -= Watchdog;
            EditorApplication.Exit(exitCode);
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
}
