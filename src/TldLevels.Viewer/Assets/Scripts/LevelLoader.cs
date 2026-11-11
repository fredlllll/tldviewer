using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GLTFast;
using UnityEngine;

namespace TldLevels.Viewer;

/// <summary>Loads one complete local GLB. Future cell streaming belongs here.</summary>
public sealed class LevelLoader : MonoBehaviour
{
    private const string ExportFolderKey = "TldLevels.Viewer.ExportFolder";
    private const string DefaultExportFolder = @"\\VBoxSvr\TLDLevels\tldlevels\levels";

    private string exportFolder = DefaultExportFolder;
    private string selectedPath = string.Empty;
    private string status = "Choose a GLB to load.";
    private string[] levelFiles = Array.Empty<string>();
    private int selectedIndex;
    private GameObject loadedLevel;
    private bool isLoading;

    private void Start()
    {
        exportFolder = PlayerPrefs.GetString(ExportFolderKey, DefaultExportFolder);
        RefreshLevels();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 16, 520, 210), GUI.skin.box);
        GUILayout.Label("TLD Level Viewer", GUI.skin.label);
        GUILayout.Label("Export folder");
        exportFolder = GUILayout.TextField(exportFolder);
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Refresh", GUILayout.Width(100))) RefreshLevels();
        GUI.enabled = levelFiles.Length > 0 && !isLoading;
        if (GUILayout.Button("Load selected", GUILayout.Width(130))) _ = LoadAsync(selectedPath);
        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (levelFiles.Length > 0)
        {
            selectedIndex = Mathf.Clamp(GUILayout.SelectionGrid(selectedIndex, levelFiles.Select(Path.GetFileNameWithoutExtension).ToArray(), 4), 0, levelFiles.Length - 1);
            selectedPath = levelFiles[selectedIndex];
        }
        else
        {
            GUILayout.Label("No GLB files found. Paste a path below.");
        }

        GUILayout.Label("GLB path");
        selectedPath = GUILayout.TextField(selectedPath);
        GUI.enabled = !isLoading && File.Exists(selectedPath);
        if (GUILayout.Button("Load path")) _ = LoadAsync(selectedPath);
        GUI.enabled = true;
        GUILayout.Label(status);
        GUILayout.EndArea();

        var flyCamera = Camera.main != null ? Camera.main.GetComponent<FlyCamera>() : null;
        if (flyCamera != null)
            GUI.Label(new Rect(16, Screen.height - 30, 650, 24), $"WASD move · Q/E vertical · RMB look · wheel speed ({flyCamera.MoveSpeed:F0} m/s) · Shift boost");
    }

    private void RefreshLevels()
    {
        PlayerPrefs.SetString(ExportFolderKey, exportFolder);
        levelFiles = Directory.Exists(exportFolder)
            ? Directory.EnumerateFiles(exportFolder, "*.glb").OrderBy(Path.GetFileName).ToArray()
            : Array.Empty<string>();
        selectedIndex = 0;
        selectedPath = levelFiles.FirstOrDefault() ?? selectedPath;
        status = levelFiles.Length == 0 ? $"Folder not found or contains no GLBs: {exportFolder}" : $"Found {levelFiles.Length} level GLB(s).";
    }

    private async Task LoadAsync(string path)
    {
        if (isLoading || !File.Exists(path)) return;
        isLoading = true;
        status = $"Loading {Path.GetFileName(path)}...";

        try
        {
            var import = new GltfImport();
            if (!await import.Load(path))
                throw new InvalidOperationException("glTFast could not parse this GLB.");

            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            if (!await import.InstantiateMainSceneAsync(root.transform))
            {
                Destroy(root);
                throw new InvalidOperationException("glTFast could not instantiate this GLB.");
            }

            if (loadedLevel != null) Destroy(loadedLevel);
            loadedLevel = root;
            status = $"Loaded {Path.GetFileName(path)}.";
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
