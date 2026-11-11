using System.Diagnostics;
using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.PrimaryContent.Models;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using TldLevels.Exporter.Cli;
using TldLevels.Exporter.Levels;
using TldLevels.Exporter.Manifest;
using TldLevels.Exporter.Preview;

namespace TldLevels.Exporter.Export;

public sealed class LevelResult
{
	public required LevelEntry Level { get; init; }

	public string? GlbPath { get; set; }

	public long Bytes { get; set; }

	public double ExportSeconds { get; set; }

	public double PreviewSeconds { get; set; }

	public string? PreviewPath { get; set; }

	public int AlphaMaterials { get; set; }

	public GlbStats? Stats { get; set; }

	/// <summary>Name of the art scene this design overlay was merged into.</summary>
	public string? MergedInto { get; set; }

	/// <summary>Design overlays that were merged into this art scene's GLB.</summary>
	public List<string> Includes { get; } = [];

	/// <summary>pending, ok, empty, existing, merged or failed.</summary>
	public string Status { get; set; } = "pending";

	public string? Error { get; set; }

	public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads levels with AssetRipper in configurable batches and exports each
/// requested scene as a single GLB, then renders its preview.
/// <para>
/// Wintermute often splits an interior into an art scene (room shell) and a
/// design overlay (loose props) stored in the next level file. A design scene
/// has no mesh larger than <see cref="SceneProfile.OverlayMaxExtent"/>, so the
/// runner merges it into the preceding art scene instead of exporting items
/// floating in a void. The art GLB keeps its own name; overlays are marked
/// <c>merged</c> in the manifest.
/// </para>
/// <para>
/// Everything is checkpointed per level: the GLB is written to a temp file and
/// moved into place, and <c>onLevelCompleted</c> fires immediately afterwards.
/// Killing the process therefore never loses completed work or leaves a
/// half-written GLB that a later run would mistake for a finished one.
/// </para>
/// </summary>
public sealed class ExportRunner
{
	private readonly Options _options;
	private readonly BlenderRenderer? _renderer;
	private readonly Dictionary<int, LevelEntry> _allById;
	private readonly Dictionary<string, LevelEntry> _allByName;
	private readonly ManifestState _previousManifest;

	public ExportRunner(Options options, BlenderRenderer? renderer, IReadOnlyList<LevelEntry> allLevels)
	{
		_options = options;
		_renderer = renderer;
		_allById = allLevels.ToDictionary(level => level.Id);
		_allByName = allLevels.ToDictionary(level => level.SceneName, StringComparer.OrdinalIgnoreCase);
		_previousManifest = ManifestState.Load(options.OutputDirectory);
	}

	public IReadOnlyList<LevelResult> Run(
		IReadOnlyList<LevelEntry> levels,
		Action<IReadOnlyList<LevelResult>>? onLevelCompleted,
		CancellationToken cancellationToken)
	{
		List<LevelResult> results = levels.Select(level => new LevelResult { Level = level }).ToList();
		Dictionary<string, LevelResult> bySource = results.ToDictionary(
			result => result.Level.SourcePath,
			StringComparer.OrdinalIgnoreCase);
		Progress progress = new(results, onLevelCompleted, cancellationToken);

		foreach (List<LevelEntry> batch in Chunk(levels, _options.BatchSize))
		{
			if (progress.Cancelled)
			{
				break;
			}

			List<LevelEntry> loadLevels = BuildLoadSet(batch);
			try
			{
				LoadAndExport(batch, loadLevels, bySource, progress);
			}
			catch (Exception exception) when (exception is not OutOfMemoryException)
			{
				Console.WriteLine($"[load] batch failed: {exception.Message}");
				foreach (LevelEntry level in batch)
				{
					LevelResult result = bySource[level.SourcePath];
					if (result.Status == "pending")
					{
						result.Status = "failed";
						result.Error = exception.Message;
						Console.WriteLine($"[{level.SceneName}] FAILED: {exception.Message}");
					}
				}

				progress.Report();
			}
		}

		if (progress.Cancelled)
		{
			Console.WriteLine("[cancel] stopped after the current level; run the same command again to resume");
		}

		return results;
	}

	/// <summary>
	/// The level itself plus its immediate neighbours. A design overlay needs
	/// one level before it for its art scene, and an art scene needs one level
	/// after it for its overlay. These neighbours are context only; they are
	/// exported only when they are the requested art scene or when the merge
	/// needs them.
	/// </summary>
	private List<LevelEntry> BuildLoadSet(List<LevelEntry> batch)
	{
		List<LevelEntry> loadLevels = [.. batch];
		HashSet<int> ids = [.. batch.Select(level => level.Id)];

		void AddNeighbour(int id)
		{
			if (_allById.TryGetValue(id, out LevelEntry? level) && !level.IsStub && ids.Add(id))
			{
				loadLevels.Add(level);
			}
		}

		AddNeighbour(batch.Min(level => level.Id) - 1);
		AddNeighbour(batch.Max(level => level.Id) + 1);
		loadLevels.Sort(static (a, b) => a.Id.CompareTo(b.Id));
		return loadLevels;
	}

	private void LoadAndExport(
		List<LevelEntry> batch,
		List<LevelEntry> loadLevels,
		IReadOnlyDictionary<string, LevelResult> bySource,
		Progress progress)
	{
		Directory.CreateDirectory(_options.OutputDirectory);

		Stopwatch loadWatch = Stopwatch.StartNew();
		FullConfiguration settings = new();
		ExportHandler handler = new(settings);
		GameData gameData;

		try
		{
			gameData = handler.LoadAndProcess([.. loadLevels.Select(level => level.SourcePath)], LocalFileSystem.Instance);
		}
		catch (OutOfMemoryException) when (loadLevels.Count > 1)
		{
			GC.Collect();
			Console.WriteLine("[load] out of memory for the batch; retrying one level at a time");
			foreach (LevelEntry level in batch)
			{
				if (progress.Cancelled)
				{
					return;
				}

				LoadAndExport([level], BuildLoadSet([level]), bySource, progress);
			}

			return;
		}

		loadWatch.Stop();
		Console.WriteLine(
			$"[load] {loadLevels.Count} level(s) in {loadWatch.Elapsed.TotalSeconds:F1}s, " +
			$"peak working set {PeakWorkingSetMegabytes():F0} MB");

		Dictionary<string, SceneHierarchyObject> scenes = new(StringComparer.OrdinalIgnoreCase);
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (asset is SceneHierarchyObject scene && _allByName.ContainsKey(scene.Name.String))
			{
				scenes[scene.Name.String] = scene;
			}
		}

		Dictionary<string, SceneProfile> profiles = new(StringComparer.OrdinalIgnoreCase);
		foreach ((string name, SceneHierarchyObject scene) in scenes)
		{
			SceneProfile profile = SceneProfile.Analyze(scene);
			profiles[name] = profile;
			Console.WriteLine(
				$"[scene] {name}: {profile.MeshCount} meshes, max {profile.MaxHorizontalExtent:F1} m" +
				(profile.IsOverlay ? " (design overlay)" : string.Empty));
		}

		Dictionary<string, List<LevelEntry>> overlaysByArt = BuildOverlayMap(loadLevels, scenes, profiles);
		HashSet<string> handled = new(StringComparer.OrdinalIgnoreCase);

		foreach (LevelEntry level in loadLevels)
		{
			if (progress.Cancelled)
			{
				return;
			}

			if (!scenes.TryGetValue(level.SceneName, out SceneHierarchyObject? scene))
			{
				continue;
			}

			bool requested = batch.Any(candidate => candidate.Id == level.Id);
			LevelResult? result = bySource.GetValueOrDefault(level.SourcePath);

			if (overlaysByArt.TryGetValue(level.SceneName, out List<LevelEntry>? overlays))
			{
				handled.Add(level.SceneName);
				bool exported = ExportCombined(level, scene, overlays, scenes, result, progress);

				foreach (LevelEntry overlay in overlays)
				{
					handled.Add(overlay.SceneName);
					LevelResult? overlayResult = bySource.GetValueOrDefault(overlay.SourcePath);
					if (!exported)
					{
						if (overlayResult is not null)
						{
							overlayResult.Status = "failed";
							overlayResult.Error = $"Could not export art scene {level.SceneName}";
							Console.WriteLine($"[{overlay.SceneName}] FAILED: art scene {level.SceneName} did not export");
							progress.Report();
						}

						continue;
					}

					DeleteOverlayArtifacts(overlay);
					if (overlayResult is not null)
					{
						overlayResult.Status = "merged";
						overlayResult.MergedInto = level.SceneName;
						overlayResult.GlbPath = GlbPath(level);
						overlayResult.PreviewPath = Path.ChangeExtension(GlbPath(level), ".png");
						overlayResult.Bytes = 0;
						overlayResult.Warnings.Add($"Design overlay merged into {level.SceneName}");
						Console.WriteLine($"[{overlay.SceneName}] merged into {level.SceneName}");
						progress.Report();
					}
				}

				continue;
			}

			if (handled.Contains(level.SceneName) || !requested)
			{
				continue;
			}

			ExportSingle(level, scene.Assets, result, progress);
		}

		foreach (LevelEntry level in batch)
		{
			LevelResult result = bySource[level.SourcePath];
			if (result.Status != "pending")
			{
				continue;
			}

			if (progress.Cancelled)
			{
				continue;
			}

			result.Status = "failed";
			result.Error = "AssetRipper did not produce a scene for this level";
			Console.WriteLine($"[{level.SceneName}] FAILED: no scene produced");
			progress.Report();
		}
	}

	private Dictionary<string, List<LevelEntry>> BuildOverlayMap(
		List<LevelEntry> loadLevels,
		IReadOnlyDictionary<string, SceneHierarchyObject> scenes,
		IReadOnlyDictionary<string, SceneProfile> profiles)
	{
		Dictionary<string, List<LevelEntry>> overlaysByArt = new(StringComparer.OrdinalIgnoreCase);
		if (!_options.MergeOverlays)
		{
			return overlaysByArt;
		}

		foreach (LevelEntry level in loadLevels)
		{
			if (!profiles.TryGetValue(level.SceneName, out SceneProfile? profile) || !profile.IsOverlay)
			{
				continue;
			}

			if (!_allById.TryGetValue(level.Id - 1, out LevelEntry? art)
				|| !scenes.ContainsKey(art.SceneName)
				|| !profiles.TryGetValue(art.SceneName, out SceneProfile? artProfile)
				|| artProfile.IsOverlay
				|| artProfile.MeshCount == 0)
			{
				continue;
			}

			if (!overlaysByArt.TryGetValue(art.SceneName, out List<LevelEntry>? list))
			{
				list = [];
				overlaysByArt.Add(art.SceneName, list);
			}

			list.Add(level);
		}

		return overlaysByArt;
	}

	private void ExportSingle(
		LevelEntry level,
		IEnumerable<IUnityObjectBase> assets,
		LevelResult? result,
		Progress progress)
	{
		string glbPath = GlbPath(level);

		if (!_options.Force && File.Exists(glbPath))
		{
			if (_options.Validate && result is not null && !TryInspect(result))
			{
				result.Warnings.Add("Existing GLB was unreadable and is being re-exported");
			}
			else
			{
				if (result is not null)
				{
					result.Status = "existing";
					result.GlbPath = glbPath;
					result.Bytes = new FileInfo(glbPath).Length;
					result.PreviewPath = Path.ChangeExtension(glbPath, ".png");
					Console.WriteLine($"[{level.SceneName}] exists  {Megabytes(result.Bytes):F1} MB (use --force to re-export)");
					RenderPreview(level, glbPath, result, force: false);
					progress.Report();
				}

				return;
			}
		}

		ExportAssets(level, assets, result, progress, [], forcePreview: true);
	}

	/// <summary>Exports an art scene together with its design overlays.</summary>
	private bool ExportCombined(
		LevelEntry art,
		SceneHierarchyObject artScene,
		IReadOnlyList<LevelEntry> overlays,
		IReadOnlyDictionary<string, SceneHierarchyObject> scenes,
		LevelResult? result,
		Progress progress)
	{
		string glbPath = GlbPath(art);
		string pngPath = Path.ChangeExtension(glbPath, ".png");
		string[] overlayNames = [.. overlays.Select(overlay => overlay.SceneName)];

		if (!_options.Force
			&& _previousManifest.IsComplete(art.SceneName, overlayNames, glbPath))
		{
			if (result is not null)
			{
				result.Status = "existing";
				result.GlbPath = glbPath;
				result.Bytes = new FileInfo(glbPath).Length;
				result.PreviewPath = pngPath;
				result.Includes.Clear();
				result.Includes.AddRange(overlayNames);
				TryInspect(result);
				Console.WriteLine(
					$"[{art.SceneName}] exists  {Megabytes(result.Bytes):F1} MB " +
					$"(includes {string.Join(", ", overlayNames)})");
				RenderPreview(art, glbPath, result, force: false);
				progress.Report();
			}

			return true;
		}

		if (result is not null)
		{
			result.Includes.Clear();
			result.Includes.AddRange(overlayNames);
		}

		IEnumerable<IUnityObjectBase> assets = artScene.Assets;
		foreach (LevelEntry overlay in overlays)
		{
			assets = assets.Concat(scenes[overlay.SceneName].Assets);
		}

		return ExportAssets(art, assets, result, progress, overlayNames, forcePreview: true);
	}

	private bool ExportAssets(
		LevelEntry level,
		IEnumerable<IUnityObjectBase> assets,
		LevelResult? result,
		Progress progress,
		IReadOnlyList<string> includes,
		bool forcePreview)
	{
		string glbPath = GlbPath(level);
		string tempPath = glbPath + ".part";
		Stopwatch watch = Stopwatch.StartNew();

		try
		{
			bool success = SceneGlbExporter.Export(assets, tempPath, LocalFileSystem.Instance, out int terrainCount);
			if (terrainCount > 0)
			{
				Console.WriteLine($"[{level.SceneName}] added {terrainCount} terrain mesh(es)");
			}

			if (success && File.Exists(tempPath))
			{
				File.Move(tempPath, glbPath, overwrite: true);
			}

			watch.Stop();
			if (result is not null)
			{
				result.ExportSeconds = watch.Elapsed.TotalSeconds;
			}

			if (!success || !File.Exists(glbPath))
			{
				DeleteQuietly(tempPath);
				if (result is not null)
				{
					result.Status = "failed";
					result.Error = "GlbModelExporter returned false";
					Console.WriteLine($"[{level.SceneName}] FAILED: exporter returned false");
					progress.Report();
				}
				else
				{
					Console.WriteLine($"[{level.SceneName}] FAILED: context art scene did not export");
				}

				return false;
			}

			long bytes = new FileInfo(glbPath).Length;
			string suffix = includes.Count > 0 ? $" + {string.Join(" + ", includes)}" : string.Empty;

			if (result is null)
			{
				Console.WriteLine($"[{level.SceneName}] {Megabytes(bytes):F1} MB in {watch.Elapsed.TotalSeconds:F1}s (context art){suffix}");
				RenderPreview(level, glbPath, null, force: forcePreview);
				return true;
			}

			result.GlbPath = glbPath;
			result.Bytes = bytes;
			TryInspect(result);

			bool empty = result.Stats is { Meshes: 0 };
			result.Status = empty ? "empty" : "ok";
			if (empty)
			{
				result.Warnings.Add("GLB contains no meshes");
			}
			else if (result.Stats?.HasNonFiniteMeshBounds == true)
			{
				result.Warnings.Add("GLB position bounds contain NaN/Inf");
			}

			Console.WriteLine(
				$"[{level.SceneName}] {Megabytes(result.Bytes):F1} MB in {result.ExportSeconds:F1}s " +
				$"({result.Stats?.Meshes ?? 0} meshes, {result.Stats?.Triangles ?? 0:N0} tris, {result.Stats?.Materials ?? 0} materials){suffix}");

			RenderPreview(level, glbPath, result, force: forcePreview);
			progress.Report();
			return true;
		}
		catch (Exception exception)
		{
			watch.Stop();
			DeleteQuietly(tempPath);
			if (result is not null)
			{
				result.ExportSeconds = watch.Elapsed.TotalSeconds;
				result.Status = "failed";
				result.Error = exception.Message;
				Console.WriteLine($"[{level.SceneName}] FAILED: {exception.Message}");
				progress.Report();
			}
			else
			{
				Console.WriteLine($"[{level.SceneName}] FAILED (context art): {exception.Message}");
			}

			return false;
		}
	}

	private void RenderPreview(LevelEntry level, string glbPath, LevelResult? result, bool force)
	{
		if (_renderer is null || !File.Exists(glbPath))
		{
			return;
		}

		string pngPath = Path.ChangeExtension(glbPath, ".png");
		if (result is not null)
		{
			result.PreviewPath = pngPath;
		}

		if (!force && !_options.Force && File.Exists(pngPath))
		{
			return;
		}

		string tempPath = pngPath + ".part.png";

		try
		{
			PreviewResult preview = _renderer.Render(glbPath, tempPath, _options.PreviewSize, _options.PreviewSamples, _options.PreviewMargin);
			if (result is not null)
			{
				result.PreviewSeconds = preview.Seconds;
				result.AlphaMaterials = preview.AlphaMaterials;
			}

			if (!preview.Success)
			{
				DeleteQuietly(tempPath);
				if (result is not null)
				{
					result.Warnings.Add($"Preview failed: {preview.Error}");
				}

				Console.WriteLine($"[{level.SceneName}] preview FAILED: {preview.Error}");
				return;
			}

			File.Move(tempPath, pngPath, overwrite: true);
			Console.WriteLine($"[{level.SceneName}] preview {preview.Seconds:F1}s (alphaMats={preview.AlphaMaterials})");
		}
		catch (Exception exception)
		{
			DeleteQuietly(tempPath);
			if (result is not null)
			{
				result.Warnings.Add($"Preview failed: {exception.Message}");
			}

			Console.WriteLine($"[{level.SceneName}] preview FAILED: {exception.Message}");
		}
	}

	/// <summary>Returns false when the file exists but is not a readable GLB.</summary>
	private bool TryInspect(LevelResult result)
	{
		if (!_options.Validate || result.GlbPath is null)
		{
			return true;
		}

		try
		{
			result.Stats = GlbInspector.Inspect(result.GlbPath);
			return true;
		}
		catch (Exception exception)
		{
			result.Warnings.Add($"Inspection failed: {exception.Message}");
			return false;
		}
	}

	private void DeleteOverlayArtifacts(LevelEntry overlay)
	{
		string glbPath = GlbPath(overlay);
		string pngPath = Path.ChangeExtension(glbPath, ".png");
		DeleteQuietly(glbPath);
		DeleteQuietly(pngPath);
		DeleteQuietly(glbPath + ".part");
		DeleteQuietly(pngPath + ".part.png");
	}

	private string GlbPath(LevelEntry level) =>
		Path.Combine(_options.OutputDirectory, level.SceneName + ".glb");

	private static void DeleteQuietly(string path)
	{
		try
		{
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (IOException)
		{
			// A stale temp file is harmless; the next run overwrites it.
		}
		catch (UnauthorizedAccessException)
		{
			// Same as above.
		}
	}

	private static double PeakWorkingSetMegabytes()
	{
		using Process process = Process.GetCurrentProcess();
		return process.PeakWorkingSet64 / 1024.0 / 1024.0;
	}

	private static IEnumerable<List<LevelEntry>> Chunk(IReadOnlyList<LevelEntry> levels, int size)
	{
		for (int index = 0; index < levels.Count; index += size)
		{
			int count = Math.Min(size, levels.Count - index);
			List<LevelEntry> batch = new(count);
			for (int offset = 0; offset < count; offset++)
			{
				batch.Add(levels[index + offset]);
			}

			yield return batch;
		}
	}

	private static double Megabytes(long bytes) => bytes / 1024.0 / 1024.0;

	private sealed class Progress
	{
		private readonly IReadOnlyList<LevelResult> _results;
		private readonly Action<IReadOnlyList<LevelResult>>? _onLevelCompleted;
		private readonly CancellationToken _cancellationToken;
		private readonly Stopwatch _watch = Stopwatch.StartNew();

		public Progress(
			IReadOnlyList<LevelResult> results,
			Action<IReadOnlyList<LevelResult>>? onLevelCompleted,
			CancellationToken cancellationToken)
		{
			_results = results;
			_onLevelCompleted = onLevelCompleted;
			_cancellationToken = cancellationToken;
		}

		public bool Cancelled => _cancellationToken.IsCancellationRequested;

		public void Report()
		{
			_onLevelCompleted?.Invoke(_results);

			int total = _results.Count;
			int done = _results.Count(result => result.Status != "pending");
			if (done == 0 || done == total)
			{
				return;
			}

			TimeSpan elapsed = _watch.Elapsed;
			double perLevel = elapsed.TotalSeconds / done;
			TimeSpan remaining = TimeSpan.FromSeconds(perLevel * (total - done));
			Console.WriteLine(
				$"[progress] {done}/{total} ({100.0 * done / total:F0}%), " +
				$"elapsed {Format(elapsed)}, eta {Format(remaining)}");
		}

		private static string Format(TimeSpan span) =>
			span.TotalHours >= 1
				? $"{(int)span.TotalHours}h{span.Minutes:D2}m"
				: $"{span.Minutes}m{span.Seconds:D2}s";
	}
}
