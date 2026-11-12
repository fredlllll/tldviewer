using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace TldLevels.Viewer
{

/// <summary>Loads one complete local GLB. Future cell streaming belongs here.</summary>
public sealed class LevelLoader : MonoBehaviour
{
    private const string ExportFolderKey = "TldLevels.Viewer.ExportFolder";
    private const string DefaultExportFolder = @"\\VBoxSvr\TLDLevels\tldlevels\levels";

    private string exportFolder = DefaultExportFolder;
    private string pathInput = string.Empty;
    private string status = "Choose a GLB to load.";
    private string[] levelFiles = Array.Empty<string>();
    private int selectedIndex;
    private Vector2 levelListScroll;
    private GameObject loadedLevel;
    private GltfImport loadedImport;
    private bool isLoading;

    private void Start()
    {
        exportFolder = PlayerPrefs.GetString(ExportFolderKey, DefaultExportFolder);
        RefreshLevels();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 16, 520, 340), GUI.skin.box);
        GUILayout.Label("TLD Level Viewer", GUI.skin.label);
        GUILayout.Label("Export folder");
        exportFolder = GUILayout.TextField(exportFolder);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh", GUILayout.Width(100))) RefreshLevels();
        GUI.enabled = levelFiles.Length > 0 && !isLoading;
        if (GUILayout.Button("Load selected", GUILayout.Width(130))) _ = LoadAsync(levelFiles[selectedIndex]);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (levelFiles.Length > 0)
        {
            levelListScroll = GUILayout.BeginScrollView(levelListScroll, GUILayout.Height(120));
            int newIndex = GUILayout.SelectionGrid(selectedIndex, levelFiles.Select(Path.GetFileNameWithoutExtension).ToArray(), 4);
            GUILayout.EndScrollView();
            newIndex = Mathf.Clamp(newIndex, 0, levelFiles.Length - 1);
            if (newIndex != selectedIndex)
            {
                selectedIndex = newIndex;
                pathInput = levelFiles[selectedIndex];
            }
        }
        else
        {
            GUILayout.Label("No GLB files found. Paste a path below.");
        }

        GUILayout.Label("GLB path");
        pathInput = GUILayout.TextField(pathInput);
        GUILayout.BeginHorizontal();
        GUI.enabled = !isLoading && File.Exists(pathInput);
        if (GUILayout.Button("Load path")) _ = LoadAsync(pathInput);
        GUI.enabled = loadedLevel != null && !isLoading;
        if (GUILayout.Button("Frame level (F)", GUILayout.Width(140))) FrameLoadedLevel();
        GUI.enabled = true;
        GUILayout.EndHorizontal();
        GUILayout.Label(status);
        GUILayout.EndArea();

        var flyCamera = Camera.main != null ? Camera.main.GetComponent<FlyCamera>() : null;
        if (flyCamera != null)
            GUI.Label(new Rect(16, Screen.height - 30, 650, 24), $"WASD move · Q/E vertical · RMB look · wheel speed ({flyCamera.MoveSpeed:F0} m/s) · Shift boost");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F) && loadedLevel != null)
            FrameLoadedLevel();
    }

    private void RefreshLevels()
    {
        PlayerPrefs.SetString(ExportFolderKey, exportFolder);
        levelFiles = Directory.Exists(exportFolder)
            ? Directory.EnumerateFiles(exportFolder, "*.glb").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        selectedIndex = 0;
        pathInput = levelFiles.FirstOrDefault() ?? pathInput;
        levelListScroll = Vector2.zero;
        status = levelFiles.Length == 0 ? $"Folder not found or contains no GLBs: {exportFolder}" : $"Found {levelFiles.Length} level GLB(s).";
    }

    private void FrameLoadedLevel()
    {
        if (!TryGetContentBounds(out Bounds bounds))
        {
            status = "Loaded level has no renderable bounds.";
            return;
        }

        Camera camera = Camera.main;
        if (camera == null)
            return;

        float radius = Mathf.Max(bounds.extents.magnitude, 5f);
        Vector3 viewDirection = new Vector3(0.6f, 0.45f, -0.65f).normalized;
        camera.transform.position = bounds.center + viewDirection * (radius * 1.5f);
        camera.transform.LookAt(bounds.center);
        camera.nearClipPlane = Mathf.Clamp(radius * 0.001f, 0.05f, 5f);
        camera.farClipPlane = Mathf.Max(radius * 20f, 1000f);

        FlyCamera flyCamera = camera.GetComponent<FlyCamera>();
        if (flyCamera != null)
            flyCamera.SetMoveSpeed(Mathf.Clamp(radius * 0.5f, 5f, 500f));
    }

    private bool TryGetContentBounds(out Bounds result)
    {
        result = default;
        Renderer[] renderers = loadedLevel.GetComponentsInChildren<Renderer>();
        var entries = new List<(Bounds Bounds, float Size, string Name)>();
        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled)
                continue;

            Bounds bounds = renderer.bounds;
            if (!IsFinite(bounds.center) || !IsFinite(bounds.size))
                continue;

            float size = Mathf.Max(bounds.size.x, bounds.size.y, bounds.size.z);
            if (size > 0f)
                entries.Add((bounds, size, renderer.name));
        }

        if (entries.Count == 0)
            return false;

        // Prefer terrain bounds when present: distant sky or water shells can
        // otherwise dwarf the playable area and make automatic framing useless.
        var terrainEntries = entries
            .Where(entry => entry.Name.Contains("terrain", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        IEnumerable<(Bounds Bounds, float Size, string Name)> selected = terrainEntries.Length > 0
            ? terrainEntries
            : entries.Where(entry => entry.Size <= GetSizeCutoff(entries));

        bool hasBounds = false;
        foreach ((Bounds bounds, _, _) in selected)
        {
            if (!hasBounds)
            {
                result = bounds;
                hasBounds = true;
            }
            else
            {
                result.Encapsulate(bounds);
            }
        }

        return hasBounds;
    }

    private static string SummarizeMaterials(GameObject root)
    {
        HashSet<Material> materials = new();
        foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>())
        {
            foreach (Material material in renderer.sharedMaterials)
            {
                if (material != null)
                    materials.Add(material);
            }
        }

        int textured = materials.Count(material =>
            material.HasProperty("baseColorTexture") && material.GetTexture("baseColorTexture") != null);
        return $"{materials.Count} material(s), {textured} with a base texture.";
    }

    private static float GetSizeCutoff(List<(Bounds Bounds, float Size, string Name)> entries)
    {
        float[] sizes = entries.Select(entry => entry.Size).OrderBy(size => size).ToArray();
        int index = Mathf.Clamp(Mathf.FloorToInt(sizes.Length * 0.95f), 0, sizes.Length - 1);
        return Mathf.Max(sizes[index] * 2f, 20f);
    }

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private async Task LoadAsync(string path)
    {
        if (isLoading || !File.Exists(path)) return;
        isLoading = true;
        status = $"Loading {Path.GetFileName(path)}...";

        try
        {
            var import = new GltfImport();
            if (!await import.LoadFile(path))
                throw new InvalidOperationException("glTFast could not parse this GLB.");

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            if (!await import.InstantiateMainSceneAsync(root.transform))
            {
                Destroy(root);
                import.Dispose();
                throw new InvalidOperationException("glTFast could not instantiate this GLB.");
            }

            if (loadedLevel != null) Destroy(loadedLevel);
            loadedImport?.Dispose();
            loadedLevel = root;
            loadedImport = import;
            FrameLoadedLevel();
            status = $"Loaded {Path.GetFileName(path)}. {SummarizeMaterials(root)}";
        }
        catch (Exception exception)
        {
            status = $"Load failed: {exception.Message}";
            Debug.LogException(exception);
        }
        finally
        {
            isLoading = false;
        }
    }
}
}
