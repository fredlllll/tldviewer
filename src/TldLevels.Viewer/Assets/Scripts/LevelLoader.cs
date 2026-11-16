using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace TldLevels.Viewer
{

/// <summary>Streams a <c>.viewer</c> cell package.</summary>
public sealed class LevelLoader : MonoBehaviour
{
    private const string ExportFolderKey = "TldLevels.Viewer.ExportFolder";
    private const string DefaultExportFolder = @"\\VBoxSvr\TLDLevels\tldlevels\levels";

    private string exportFolder = DefaultExportFolder;
    private string pathInput = string.Empty;
    private string status = "Choose a level package to load.";
    private string[] levelEntries = Array.Empty<string>();
    private int selectedIndex;
    private Vector2 levelListScroll;
    private PackageStreamer packageStreamer;
    private bool isLoading;

    private void Start()
    {
        exportFolder = PlayerPrefs.GetString(ExportFolderKey, DefaultExportFolder);
        RefreshLevels();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 16, 520, 420), GUI.skin.box);
        GUILayout.Label("TLD Level Viewer", GUI.skin.label);
        GUILayout.Label("Export folder");
        exportFolder = GUILayout.TextField(exportFolder);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh", GUILayout.Width(100))) RefreshLevels();
        GUI.enabled = levelEntries.Length > 0 && !isLoading;
        if (GUILayout.Button("Load selected", GUILayout.Width(130))) Load(levelEntries[selectedIndex]);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (levelEntries.Length > 0)
        {
            levelListScroll = GUILayout.BeginScrollView(levelListScroll, GUILayout.Height(120));
            string[] labels = new string[levelEntries.Length];
            for (int i = 0; i < levelEntries.Length; i++)
                labels[i] = Path.GetFileName(levelEntries[i]);
            int newIndex = GUILayout.SelectionGrid(selectedIndex, labels, 3);
            GUILayout.EndScrollView();
            newIndex = Mathf.Clamp(newIndex, 0, levelEntries.Length - 1);
            if (newIndex != selectedIndex)
            {
                selectedIndex = newIndex;
                pathInput = levelEntries[selectedIndex];
            }
        }
        else
        {
            GUILayout.Label("No .viewer packages found. Paste a path below.");
        }

        GUILayout.Label("Package path (.viewer)");
        pathInput = GUILayout.TextField(pathInput);
        GUILayout.BeginHorizontal();
        GUI.enabled = !isLoading && Directory.Exists(pathInput);
        if (GUILayout.Button("Load path")) Load(pathInput);
        GUI.enabled = IsSomethingLoaded() && !isLoading;
        if (GUILayout.Button("Frame level (F)", GUILayout.Width(140))) FrameLoadedLevel();
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (packageStreamer != null && packageStreamer.IsRunning)
        {
            GUILayout.Label($"Load radius {packageStreamer.LoadRadius:F0} m (unload {packageStreamer.UnloadRadius:F0} m)");
            float radius = GUILayout.HorizontalSlider(packageStreamer.LoadRadius, 256f, 4000f);
            if (Mathf.Abs(radius - packageStreamer.LoadRadius) > 0.5f)
            {
                packageStreamer.LoadRadius = radius;
                packageStreamer.UnloadRadius = radius * 1.4f;
            }

            GUILayout.Label(packageStreamer.StatusLine);
        }

        GUILayout.Label(status);
        GUILayout.EndArea();

        var flyCamera = Camera.main != null ? Camera.main.GetComponent<FlyCamera>() : null;
        if (flyCamera != null)
            GUI.Label(new Rect(16, Screen.height - 30, 650, 24), $"WASD move · Q/E vertical · RMB look · wheel speed ({flyCamera.MoveSpeed:F0} m/s) · Shift boost");
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F) && IsSomethingLoaded())
            FrameLoadedLevel();
    }

    private bool IsSomethingLoaded() =>
        packageStreamer != null && packageStreamer.IsRunning;

    private void RefreshLevels()
    {
        PlayerPrefs.SetString(ExportFolderKey, exportFolder);
        if (!Directory.Exists(exportFolder))
        {
            levelEntries = Array.Empty<string>();
            status = $"Folder not found: {exportFolder}";
            return;
        }

        string[] packages = Directory.GetDirectories(exportFolder, "*.viewer")
            .Where(directory => File.Exists(Path.Combine(directory, "manifest.json")))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        levelEntries = packages;
        selectedIndex = 0;
        pathInput = levelEntries.FirstOrDefault() ?? pathInput;
        levelListScroll = Vector2.zero;
        status = levelEntries.Length == 0
            ? $"Folder contains no .viewer packages: {exportFolder}"
            : $"Found {packages.Length} package(s).";
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
        camera.farClipPlane = ViewerBootstrap.FarClipPlane;

        FlyCamera flyCamera = camera.GetComponent<FlyCamera>();
        if (flyCamera != null)
            flyCamera.SetMoveSpeed(Mathf.Clamp(radius * 0.5f, 5f, 500f));
    }

    private bool TryGetContentBounds(out Bounds result)
    {
        result = default;
        if (packageStreamer != null && packageStreamer.IsRunning)
        {
            result = packageStreamer.FocusBounds;
            return true;
        }

        return false;
    }

    private void Load(string path)
    {
        if (isLoading)
            return;

        if (Directory.Exists(path))
        {
            LoadPackage(path);
            return;
        }

        status = $"Package not found: {path}";
    }

    private void LoadPackage(string path)
    {
        if (!PackageManifest.TryLoad(path, out PackageManifest manifest, out string error))
        {
            status = error;
            return;
        }

        isLoading = true;
        status = $"Opening {Path.GetFileName(path)}...";
        try
        {
            UnloadCurrent();
            if (packageStreamer == null)
            {
                var streamerObject = new GameObject("Package Streamer");
                packageStreamer = streamerObject.AddComponent<PackageStreamer>();
            }

            packageStreamer.Initialize(manifest, path);
            FrameLoadedLevel();
            status = $"Streaming {manifest.level} ({manifest.cells.Length} cells). Use F to reframe.";
        }
        catch (Exception exception)
        {
            status = $"Package load failed: {exception.Message}";
            Debug.LogException(exception);
        }
        finally
        {
            isLoading = false;
        }
    }

    private void UnloadCurrent()
    {
        packageStreamer?.Shutdown();
    }
}
}
