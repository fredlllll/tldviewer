using System.Diagnostics;
using AssetRipper.Assets;
using AssetRipper.Export.Configuration;
using AssetRipper.Export.UnityProjects;
using AssetRipper.IO.Files;
using AssetRipper.Processing;
using AssetRipper.Processing.Prefabs;
using TldLevels.Exporter.Cli;
using TldLevels.Exporter.Levels;
using TldLevels.Exporter.Manifest;

namespace TldLevels.Exporter.Export;

public sealed class LevelResult
{
	public required LevelEntry Level { get; init; }

	public string? PackagePath { get; set; }

	public long Bytes { get; set; }

	public double ExportSeconds { get; set; }

	public int Cells { get; set; }

	public int Instances { get; set; }

	public int Assets { get; set; }

	public int Lods { get; set; }

	public long Triangles { get; set; }

	/// <summary>Art scenes merged into this package's scene.</summary>
	public List<string> Includes { get; } = [];

	/// <summary>pending, ok, empty, existing or failed.</summary>
	public string Status { get; set; } = "pending";

	public string? Error { get; set; }

	public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads levels with AssetRipper in configurable batches and exports each
/// requested scene as a streamed viewer package (shared asset library + cells).
/// <para>
/// Wintermute often splits an interior into an art scene (room shell) and a
/// design overlay (loose props) stored in the next level file. A design scene
/// has no mesh larger than <see cref="SceneProfile.OverlayMaxExtent"/>, so the
/// runner exports it as a copy of the preceding art scene plus its own props
/// instead of leaving items floating in a void. The package keeps the overlay's
/// name; <see cref="LevelResult.Includes"/> records the base scene.
/// </para>
/// <para>
/// Everything is checkpointed per level: the package is written to a
/// <c>.viewer.part</c> directory and moved into place by the exporter, and
/// <c>onLevelCompleted</c> fires immediately afterwards. Killing the process
/// therefore never loses completed work or leaves a half-written package that a
/// later run would mistake for a finished one.
/// </para>
/// </summary>
public sealed class ExportRunner
{
	private readonly Options _options;
	private readonly Dictionary<int, LevelEntry> _allById;
	private readonly Dictionary<string, LevelEntry> _allByName;

	public ExportRunner(Options options, IReadOnlyList<LevelEntry> allLevels)
	{
		_options = options;
		_allById = allLevels.ToDictionary(level => level.Id);
		_allByName = allLevels.ToDictionary(level => level.SceneName, StringComparer.OrdinalIgnoreCase);
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
		foreach (LevelEntry level in batch)
		{
			if (_options.SceneMerges.TryGetValue(level.Id, out int baseId))
			{
				AddNeighbour(baseId);
			}
		}
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

			if (!requested)
			{
				continue;
			}

			if (_options.MergeOverlays
				&& profiles.TryGetValue(level.SceneName, out SceneProfile? profile)
				&& (profile.IsOverlay || _options.SceneMerges.ContainsKey(level.Id))
				&& TryGetArtScene(level, scenes, profiles, out LevelEntry? art, out SceneHierarchyObject? artScene)
				&& art is not null
				&& artScene is not null)
			{
				ExportDesignVariant(level, art, artScene, scene, result, progress);
			}
			else
			{
				ExportSingle(level, scene.Assets, result, progress);
			}
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

	private bool TryGetArtScene(
		LevelEntry overlay,
		IReadOnlyDictionary<string, SceneHierarchyObject> scenes,
		IReadOnlyDictionary<string, SceneProfile> profiles,
		out LevelEntry? art,
		out SceneHierarchyObject? artScene)
	{
		art = null;
		artScene = null;
		int candidateId = _options.SceneMerges.GetValueOrDefault(overlay.Id, overlay.Id - 1);
		if (!_allById.TryGetValue(candidateId, out LevelEntry? candidate)
			|| !scenes.TryGetValue(candidate.SceneName, out SceneHierarchyObject? candidateScene)
			|| !profiles.TryGetValue(candidate.SceneName, out SceneProfile? profile)
			|| profile.IsOverlay
			|| profile.MeshCount == 0)
		{
			return false;
		}

		art = candidate;
		artScene = candidateScene;
		return true;
	}

	private void ExportSingle(
		LevelEntry level,
		IEnumerable<IUnityObjectBase> assets,
		LevelResult? result,
		Progress progress)
	{
		string packagePath = PackagePath(level);
		if (!_options.Force && IsPackageComplete(packagePath))
		{
			if (result is not null)
			{
				result.Status = "existing";
				result.PackagePath = packagePath;
				result.Bytes = DirectoryBytes(packagePath);
				Console.WriteLine($"[{level.SceneName}] package exists  {Megabytes(result.Bytes):F1} MB (use --force to re-export)");
				progress.Report();
			}

			return;
		}

		ExportPackageAssets(level, assets, result, progress, []);
	}

	/// <summary>Exports a design level as a copy of its preceding art scene plus its own props.</summary>
	private bool ExportDesignVariant(
		LevelEntry overlay,
		LevelEntry art,
		SceneHierarchyObject artScene,
		SceneHierarchyObject overlayScene,
		LevelResult? result,
		Progress progress)
	{
		string packagePath = PackagePath(overlay);
		string[] packageIncludes = [art.SceneName];
		if (!_options.Force && IsPackageComplete(packagePath))
		{
			if (result is not null)
			{
				result.Status = "existing";
				result.PackagePath = packagePath;
				result.Bytes = DirectoryBytes(packagePath);
				result.Includes.Clear();
				result.Includes.AddRange(packageIncludes);
				Console.WriteLine($"[{overlay.SceneName}] package exists  {Megabytes(result.Bytes):F1} MB (base {art.SceneName})");
				progress.Report();
			}

			return true;
		}

		if (result is not null)
		{
			result.Includes.Clear();
			result.Includes.AddRange(packageIncludes);
		}

		IEnumerable<IUnityObjectBase> packageAssets = artScene.Assets.Concat(overlayScene.Assets);
		ExportPackageAssets(overlay, packageAssets, result, progress, packageIncludes);
		return true;
	}

	private bool ExportPackageAssets(
		LevelEntry level,
		IEnumerable<IUnityObjectBase> assets,
		LevelResult? result,
		Progress progress,
		IReadOnlyList<string> includes)
	{
		string packagePath = PackagePath(level);
		Stopwatch watch = Stopwatch.StartNew();

		try
		{
			ViewerPackageResult package = ViewerPackageExporter.Export(
				assets,
				packagePath,
				_options.CellSize,
				includeAllLods: true);
			watch.Stop();

			if (result is not null)
			{
				result.ExportSeconds = watch.Elapsed.TotalSeconds;
				result.PackagePath = package.Path;
				result.Bytes = package.Bytes;
				result.Cells = package.Cells;
				result.Instances = package.Instances;
				result.Assets = package.Assets;
				result.Lods = package.Lods;
				result.Triangles = package.Triangles;
				result.Status = package.Triangles == 0 ? "empty" : "ok";
				if (package.Triangles == 0)
				{
					result.Warnings.Add("Viewer package contains no triangles");
				}

				if (package.SkippedNodes > 0)
				{
					result.Warnings.Add($"{package.SkippedNodes} node transform(s) skipped (invalid matrix)");
				}
			}

			string suffix = includes.Count > 0 ? $" + {string.Join(" + ", includes)}" : string.Empty;
			Console.WriteLine(
				$"[{level.SceneName}] package {Megabytes(package.Bytes):F1} MB + " +
				$"{Megabytes(package.SharedBytes):F1} MB shared in {watch.Elapsed.TotalSeconds:F1}s " +
				$"({package.Cells} cells, {package.Instances:N0} instances, {package.Assets:N0} shared assets, " +
				$"{package.MeshCount} meshes, {package.Triangles:N0} tris, {package.Lods} LODs" +
				$"{(package.SharedNodes > 0 ? $", {package.SharedNodes:N0} cell-local shared" : string.Empty)}" +
				$"{(package.SkippedNodes > 0 ? $", {package.SkippedNodes} skipped" : string.Empty)}){suffix}");
			progress.Report();
			return true;
		}
		catch (Exception exception)
		{
			watch.Stop();
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

	private string PackagePath(LevelEntry level) =>
		Path.Combine(_options.OutputDirectory, level.SceneName + ".viewer");

	private static bool IsPackageComplete(string packagePath) =>
		File.Exists(Path.Combine(packagePath, "manifest.json"));

	private static long DirectoryBytes(string path) =>
		Directory.Exists(path)
			? Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length)
			: 0;

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
