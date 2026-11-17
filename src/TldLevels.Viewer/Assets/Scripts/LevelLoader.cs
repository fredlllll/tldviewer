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
/// exporter before streaming them. The UI itself lives in <see cref="ViewerUi"/>.
/// </summary>
public sealed class LevelLoader : MonoBehaviour
{
    private const string ExportFolderKey = "TldLevels.Viewer.ExportFolder";
    private const string GameFolderKey = "TldLevels.Viewer.GameFolder";
    private const string ExporterPathKey = "TldLevels.Viewer.ExporterPath";
    private const string DefaultExportFolder = @"\\VBoxSvr\TLDLevels\tldlevels\levels";
    private const string DefaultGameFolder = @"\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data";

    private ViewerUi ui;
    private string exportFolder = DefaultExportFolder;
    private string gameFolder = DefaultGameFolder;
    private string exporterPath = string.Empty;
    private string status = "Scanning game files...";
    private List<ViewerLevel> levels = new List<ViewerLevel>();
    private PackageStreamer packageStreamer;
    private bool isLoading;
    private bool uiPopulated;

    private void Start()
    {
        ui = GetComponent<ViewerUi>();
        if (ui == null)
        {
            ui = gameObject.AddComponent<ViewerUi>();
        }

        ui.Initialize();
        ui.RescanRequested += RefreshLevels;
        ui.ClearFilterRequested += ClearFilter;
        ui.FilterChanged += _ => RebuildLevelList();
        ui.LevelSelected += LoadLevelById;
        ui.LoadPathRequested += () => Load(ui.PackagePath);
        ui.FrameRequested += FrameLoadedLevel;
        ui.RadiusChanged += SetRadius;

        exportFolder = PlayerPrefs.GetString(ExportFolderKey, DefaultExportFolder);
        gameFolder = PlayerPrefs.GetString(GameFolderKey, DefaultGameFolder);
        exporterPath = PlayerPrefs.GetString(ExporterPathKey, DefaultExporterPath());

        ui.ExportFolder = exportFolder;
        ui.GameFolder = gameFolder;
        ui.ExporterPath = exporterPath;

        RefreshLevels();
    }

    private void Update()
    {
        if (!uiPopulated)
        {
            // The UI tree can be one frame behind scene load; push the level
            // list as soon as the document has bound its elements.
            if (!ui.IsReady)
            {
                return;
            }

            uiPopulated = true;
            ui.ExportFolder = exportFolder;
            ui.GameFolder = gameFolder;
            ui.ExporterPath = exporterPath;
            RebuildLevelList();
            UpdateExporterWarning();
        }

        if (Input.GetKeyDown(KeyCode.F) && IsSomethingLoaded() && !ui.IsTextInputFocused)
        {
            FrameLoadedLevel();
        }

        if (packageStreamer != null && packageStreamer.IsRunning)
        {
            ui.SetStreamer(true, packageStreamer.StatusLine, packageStreamer.LoadRadius, packageStreamer.UnloadRadius);
        }
        else
        {
            ui.SetStreamer(false, string.Empty, 0f, 0f);
        }

        ui.SetFrameEnabled(IsSomethingLoaded() && !isLoading);

        var flyCamera = Camera.main != null ? Camera.main.GetComponent<FlyCamera>() : null;
        if (flyCamera != null)
        {
            ui.SetHint(
                $"WASD move · Q/E vertical · RMB look · wheel speed ({flyCamera.MoveSpeed:F0} m/s) · Shift boost");
        }

        ui.SetStatus(status);
    }

    private bool IsSomethingLoaded() =>
        packageStreamer != null && packageStreamer.IsRunning;

    private void RefreshLevels()
    {
        if (ui == null)
        {
            return;
        }

        exportFolder = ui.IsReady ? ui.ExportFolder : exportFolder;
        gameFolder = ui.IsReady ? ui.GameFolder : gameFolder;
        exporterPath = ui.IsReady ? ui.ExporterPath : exporterPath;

        PlayerPrefs.SetString(ExportFolderKey, exportFolder);
        PlayerPrefs.SetString(GameFolderKey, gameFolder);
        PlayerPrefs.SetString(ExporterPathKey, exporterPath);

        levels = new List<ViewerLevel>();
        if (!Directory.Exists(gameFolder))
        {
            status = $"Game data folder not found: {gameFolder}";
            UpdateExporterWarning();
            RebuildLevelList();
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

                levels.Add(new ViewerLevel
                {
                    Id = id,
                    SceneName = name,
                    SourcePath = file,
                    SizeBytes = new FileInfo(file).Length,
                });
            }
        }
        catch (Exception exception)
        {
            levels = new List<ViewerLevel>();
            status = $"Could not scan {gameFolder}: {exception.Message}";
            UpdateExporterWarning();
            RebuildLevelList();
            return;
        }

        levels.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (ViewerLevel level in levels)
        {
            level.Exported = File.Exists(Path.Combine(exportFolder, level.SceneName + ".viewer", "manifest.json"));
        }

        int exported = levels.Count(level => level.Exported);
        status = levels.Count == 0
            ? $"No level files found in {gameFolder}"
            : $"Found {levels.Count} levels: {exported} exported, {levels.Count - exported} to convert.";

        UpdateExporterWarning();
        RebuildLevelList();
    }

    private void UpdateExporterWarning()
    {
        ui.SetExporterWarning(string.IsNullOrWhiteSpace(exporterPath) || !File.Exists(exporterPath));
    }

    private void RebuildLevelList()
    {
        if (ui == null)
        {
            return;
        }

        string filter = ui.Filter;
        List<ViewerLevel> visible = levels.Where(level => level.Matches(filter)).ToList();
        ui.SetLevels(visible, !isLoading);

        if (levels.Count == 0)
        {
            ui.SetLevelEmpty("No level files found in the game data folder.");
        }
        else if (visible.Count == 0)
        {
            ui.SetLevelEmpty("No levels match the filter.");
        }
        else
        {
            ui.SetLevelEmpty(null);
        }

        ui.SetStatus(status);
    }

    private void ClearFilter()
    {
        ui.Filter = string.Empty;
        RebuildLevelList();
    }

    private void SetRadius(float radius)
    {
        if (packageStreamer == null)
        {
            return;
        }

        packageStreamer.LoadRadius = radius;
        packageStreamer.UnloadRadius = radius * 1.4f;
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

        if (!Directory.Exists(path))
        {
            status = $"Package not found: {path}";
            ui.SetStatus(status);
            return;
        }

        SetBusy(true);
        try
        {
            LoadPackage(path);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void LoadLevelById(int id)
    {
        ViewerLevel level = levels.FirstOrDefault(candidate => candidate.Id == id);
        if (level != null)
        {
            LoadLevel(level);
        }
    }

    private async void LoadLevel(ViewerLevel level)
    {
        if (isLoading || level == null)
            return;

        SetBusy(true);
        try
        {
            if (!level.Exported)
            {
                if (!await ConvertLevelAsync(level))
                    return;

                level.Exported = true;
                status = $"Converted {level.SceneName}.";
                RebuildLevelList();
            }

            string packagePath = Path.Combine(exportFolder, level.SceneName + ".viewer");
            ui.PackagePath = packagePath;
            LoadPackage(packagePath);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        isLoading = busy;
        ui.SetBusy(busy);
        if (!busy)
        {
            RebuildLevelList();
        }
    }

    private async Task<bool> ConvertLevelAsync(ViewerLevel level)
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

    private ProcessStartInfo CreateExporterStartInfo(ViewerLevel level)
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
}
}
