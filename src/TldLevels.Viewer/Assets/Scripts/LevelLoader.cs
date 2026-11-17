using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace TldLevels.Viewer
{

/// <summary>
/// Lists every level file in the game data folder, marks which ones already
/// have a <c>.viewer</c> package, and converts missing packages with the
/// exporter before streaming them.
/// </summary>
public sealed class LevelLoader : MonoBehaviour
{
    private const string ExportFolderKey = "TldLevels.Viewer.ExportFolder";
    private const string GameFolderKey = "TldLevels.Viewer.GameFolder";
    private const string ExporterPathKey = "TldLevels.Viewer.ExporterPath";
    private const string DefaultExportFolder = @"\\VBoxSvr\TLDLevels\tldlevels\levels";
    private const string DefaultGameFolder = @"\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data";

    private static readonly Color ExportedColor = new Color(0.62f, 0.90f, 0.62f);
    private static readonly Color ConvertColor = new Color(1.00f, 0.82f, 0.52f);

    private string exportFolder = DefaultExportFolder;
    private string gameFolder = DefaultGameFolder;
    private string exporterPath = string.Empty;
    private string pathInput = string.Empty;
    private string filter = string.Empty;
    private string status = "Scanning game files...";
    private List<Level> levels = new List<Level>();
    private Vector2 levelListScroll;
    private PackageStreamer packageStreamer;
    private bool isLoading;

    private void Start()
    {
        exportFolder = PlayerPrefs.GetString(ExportFolderKey, DefaultExportFolder);
        gameFolder = PlayerPrefs.GetString(GameFolderKey, DefaultGameFolder);
        exporterPath = PlayerPrefs.GetString(ExporterPathKey, DefaultExporterPath());
        RefreshLevels();
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(16, 16, 760, 660), GUI.skin.box);
        GUILayout.Label("TLD Level Viewer", GUI.skin.label);

        GUILayout.Label("Export folder");
        exportFolder = GUILayout.TextField(exportFolder);
        GUILayout.Label("Game data folder");
        gameFolder = GUILayout.TextField(gameFolder);
        GUILayout.Label("Exporter (exe, dll or .csproj)");
        exporterPath = GUILayout.TextField(exporterPath);

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Rescan", GUILayout.Width(100))) RefreshLevels();
        if (GUILayout.Button("Clear filter", GUILayout.Width(100))) filter = string.Empty;
        GUILayout.Label("Filter");
        filter = GUILayout.TextField(filter);
        GUILayout.EndHorizontal();

        bool exporterReady = !string.IsNullOrWhiteSpace(exporterPath) && File.Exists(exporterPath);
        if (!exporterReady)
        {
            GUILayout.Label("Exporter not found. Build src\\TldLevels.Exporter or set its path above.");
        }

        List<Level> visible = levels
            .Where(level => filter.Length == 0
                || level.SceneName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (visible.Count == 0)
        {
            GUILayout.Label(levels.Count == 0
                ? "No level files found in the game data folder."
                : "No levels match the filter.");
        }
        else
        {
            levelListScroll = GUILayout.BeginScrollView(levelListScroll, GUILayout.Height(330));
            const int columns = 2;
            GUI.enabled = !isLoading;
            for (int i = 0; i < visible.Count; i += columns)
            {
                GUILayout.BeginHorizontal();
                for (int column = 0; column < columns && i + column < visible.Count; column++)
                {
                    Level level = visible[i + column];
                    Color previous = GUI.backgroundColor;
                    GUI.backgroundColor = level.Exported ? ExportedColor : ConvertColor;
                    if (GUILayout.Button(level.ButtonLabel, GUILayout.Height(26)))
                        LoadLevel(level);
                    GUI.backgroundColor = previous;
                }

                GUILayout.EndHorizontal();
            }

            GUI.enabled = true;
            GUILayout.EndScrollView();
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
        PlayerPrefs.SetString(GameFolderKey, gameFolder);
        PlayerPrefs.SetString(ExporterPathKey, exporterPath);

        levels = new List<Level>();
        if (!Directory.Exists(gameFolder))
        {
            status = $"Game data folder not found: {gameFolder}";
            return;
        }

        try
        {
            foreach (string file in Directory.GetFiles(gameFolder))
            {
                // Same naming rule as the exporter's LevelCatalog.
                string name = Path.GetFileName(file);
                if (!name.StartsWith("level", StringComparison.OrdinalIgnoreCase))
                    continue;

                string idText = name.Substring("level".Length);
                if (!int.TryParse(idText, out int id))
                    continue;

                long size = new FileInfo(file).Length;
                levels.Add(new Level
                {
                    Id = id,
                    SceneName = name,
                    SourcePath = file,
                    SizeBytes = size,
                });
            }
        }
        catch (Exception exception)
        {
            levels = new List<Level>();
            status = $"Could not scan {gameFolder}: {exception.Message}";
            return;
        }
        levels.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (Level level in levels)
        {
            level.Exported = File.Exists(Path.Combine(exportFolder, level.SceneName + ".viewer", "manifest.json"));
            level.UpdateLabel();
        }

        int exported = levels.Count(level => level.Exported);
        status = levels.Count == 0
            ? $"No level files found in {gameFolder}"
            : $"Found {levels.Count} levels: {exported} exported, {levels.Count - exported} to convert.";
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

    private async void LoadLevel(Level level)
    {
        if (isLoading || level == null)
            return;

        isLoading = true;
        try
        {
            if (!level.Exported)
            {
                if (!await ConvertLevelAsync(level))
                    return;

                level.Exported = true;
                level.UpdateLabel();
                status = $"Converted {level.SceneName}.";
            }

            string packagePath = Path.Combine(exportFolder, level.SceneName + ".viewer");
            pathInput = packagePath;
            LoadPackage(packagePath);
        }
        finally
        {
            isLoading = false;
        }
    }

    private async Task<bool> ConvertLevelAsync(Level level)
    {
        if (string.IsNullOrWhiteSpace(exporterPath) || !File.Exists(exporterPath))
        {
            status = "Exporter not found. Build src\\TldLevels.Exporter or set its path above.";
            return false;
        }

        status = $"Converting {level.SceneName} ({level.Megabytes:F1} MB source)...";
        try
        {
            ProcessStartInfo startInfo = CreateExporterStartInfo(level);
            using Process process = new Process { StartInfo = startInfo };
            process.Start();

            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            await Task.Run(() => process.WaitForExit());

            string output = await stdout;
            string error = await stderr;
            string packagePath = Path.Combine(exportFolder, level.SceneName + ".viewer");
            if (process.ExitCode != 0 || !File.Exists(Path.Combine(packagePath, "manifest.json")))
            {
                string detail = LastLine(error) ?? LastLine(output) ?? "see export.log";
                status = $"Conversion failed (exit {process.ExitCode}): {detail}";
                Debug.LogError($"[exporter] {error}\n{output}");
                return false;
            }

            return true;
        }
        catch (Exception exception)
        {
            status = $"Could not run the exporter: {exception.Message}";
            Debug.LogException(exception);
            return false;
        }
    }

    private ProcessStartInfo CreateExporterStartInfo(Level level)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (exporterPath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add("run");
            startInfo.ArgumentList.Add("--project");
            startInfo.ArgumentList.Add(exporterPath);
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add("Release");
            startInfo.ArgumentList.Add("--");
        }
        else if (exporterPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "dotnet";
            startInfo.ArgumentList.Add(exporterPath);
        }
        else
        {
            startInfo.FileName = exporterPath;
        }

        startInfo.ArgumentList.Add("--input");
        startInfo.ArgumentList.Add(gameFolder);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(exportFolder);
        startInfo.ArgumentList.Add("--levels");
        startInfo.ArgumentList.Add(level.Id.ToString());
        return startInfo;
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

    private static string DefaultExporterPath()
    {
        string root = FindRepositoryRoot();
        foreach (string configuration in new[] { "Release", "Debug" })
        {
            string directory = Path.Combine(root, "src", "TldLevels.Exporter", "bin", configuration, "net10.0");
            string exe = Path.Combine(directory, "TldLevels.Exporter.exe");
            if (File.Exists(exe))
                return exe;

            string dll = Path.Combine(directory, "TldLevels.Exporter.dll");
            if (File.Exists(dll))
                return dll;
        }

        string project = Path.Combine(root, "src", "TldLevels.Exporter", "TldLevels.Exporter.csproj");
        return File.Exists(project) ? project : string.Empty;
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo directory = new DirectoryInfo(Application.dataPath);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TldLevels.slnx")))
                return directory.FullName;

            directory = directory.Parent;
        }

        return Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
    }

    private static string LastLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string[] lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            string line = lines[i].Trim();
            if (line.Length > 0)
                return line.Length > 240 ? line.Substring(0, 240) : line;
        }

        return null;
    }

    private sealed class Level
    {
        public int Id;
        public string SceneName;
        public string SourcePath;
        public long SizeBytes;
        public bool Exported;
        public string ButtonLabel { get; private set; }

        public double Megabytes => SizeBytes / (1024.0 * 1024.0);

        public void UpdateLabel()
        {
            ButtonLabel = $"{SceneName}   {(Exported ? "exported" : "convert")}   {Megabytes:F1} MB";
        }
    }
}
}
