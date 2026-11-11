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

	/// <summary>pending, ok, empty, existing or failed.</summary>
	public string Status { get; set; } = "pending";

	public string? Error { get; set; }

	public List<string> Warnings { get; } = [];
}

/// <summary>
/// Loads levels with AssetRipper in configurable batches and exports each
/// requested scene as a single GLB, then renders its preview.
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

	public ExportRunner(Options options, BlenderRenderer? renderer)
	{
		_options = options;
		_renderer = renderer;
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

			List<LevelEntry> pending = [];

			foreach (LevelEntry level in batch)
			{
				LevelResult result = bySource[level.SourcePath];
				result.GlbPath = GlbPath(level);

				if (_options.Force || !File.Exists(result.GlbPath))
				{
					pending.Add(level);
					continue;
				}

				if (_options.Validate && !TryInspect(result))
				{
					result.Warnings.Add("Existing GLB was unreadable and is being re-exported");
					pending.Add(level);
					continue;
				}

				result.Status = "existing";
				result.Bytes = new FileInfo(result.GlbPath).Length;
				Console.WriteLine($"[{level.SceneName}] exists  {Megabytes(result.Bytes):F1} MB (use --force to re-export)");
				CompleteLevel(result, progress);
			}

			if (pending.Count > 0 && !progress.Cancelled)
			{
				LoadAndExport(pending, bySource, progress);
			}
		}

		if (progress.Cancelled)
		{
			Console.WriteLine("[cancel] stopped after the current level; run the same command again to resume");
		}

		return results;
	}

	private void LoadAndExport(List<LevelEntry> batch, IReadOnlyDictionary<string, LevelResult> bySource, Progress progress)
	{
		Directory.CreateDirectory(_options.OutputDirectory);

		Stopwatch loadWatch = Stopwatch.StartNew();
		FullConfiguration settings = new();
		ExportHandler handler = new(settings);
		GameData gameData;

		try
		{
			gameData = handler.LoadAndProcess([.. batch.Select(level => level.SourcePath)], LocalFileSystem.Instance);
		}
		catch (OutOfMemoryException) when (batch.Count > 1)
		{
			GC.Collect();
			Console.WriteLine("[load] out of memory for the batch; retrying one level at a time");
			foreach (LevelEntry level in batch)
			{
				if (progress.Cancelled)
				{
					return;
				}

				LoadAndExport([level], bySource, progress);
			}

			return;
		}

		loadWatch.Stop();
		Console.WriteLine(
			$"[load] {batch.Count} level(s) in {loadWatch.Elapsed.TotalSeconds:F1}s, " +
			$"peak working set {PeakWorkingSetMegabytes():F0} MB");

		Dictionary<string, LevelEntry> byName = new(StringComparer.OrdinalIgnoreCase);
		foreach (LevelEntry level in batch)
		{
			byName[level.SceneName] = level;
		}

		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (progress.Cancelled)
			{
				return;
			}

			if (asset is not SceneHierarchyObject scene)
			{
				continue;
			}

			if (!byName.TryGetValue(scene.Name.String, out LevelEntry? level))
			{
				continue;
			}

			ExportScene(scene, level, bySource[level.SourcePath], progress);
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

	private void ExportScene(SceneHierarchyObject scene, LevelEntry level, LevelResult result, Progress progress)
	{
		string glbPath = result.GlbPath ?? GlbPath(level);
		string tempPath = glbPath + ".part";
		Stopwatch watch = Stopwatch.StartNew();

		try
		{
			bool success = GlbModelExporter.ExportModel(scene.Assets, tempPath, true, LocalFileSystem.Instance);
			if (success && File.Exists(tempPath))
			{
				File.Move(tempPath, glbPath, overwrite: true);
			}

			watch.Stop();
			result.ExportSeconds = watch.Elapsed.TotalSeconds;

			if (!success || !File.Exists(glbPath))
			{
				DeleteQuietly(tempPath);
				result.Status = "failed";
				result.Error = "GlbModelExporter returned false";
				Console.WriteLine($"[{level.SceneName}] FAILED: exporter returned false");
				progress.Report();
				return;
			}

			result.Bytes = new FileInfo(glbPath).Length;
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
				$"({result.Stats?.Meshes ?? 0} meshes, {result.Stats?.Triangles ?? 0:N0} tris, {result.Stats?.Materials ?? 0} materials)");

			CompleteLevel(result, progress);
		}
		catch (Exception exception)
		{
			watch.Stop();
			DeleteQuietly(tempPath);
			result.ExportSeconds = watch.Elapsed.TotalSeconds;
			result.Status = "failed";
			result.Error = exception.Message;
			Console.WriteLine($"[{level.SceneName}] FAILED: {exception.Message}");
			progress.Report();
		}
	}

	private void CompleteLevel(LevelResult result, Progress progress)
	{
		if (!progress.Cancelled)
		{
			RenderPreview(result);
		}

		progress.Report();
	}

	private void RenderPreview(LevelResult result)
	{
		if (_renderer is null || result.GlbPath is null || !File.Exists(result.GlbPath))
		{
			return;
		}

		if (result.Status is not ("ok" or "empty" or "existing"))
		{
			return;
		}

		string pngPath = Path.ChangeExtension(result.GlbPath, ".png");
		result.PreviewPath = pngPath;

		if (!_options.Force && File.Exists(pngPath))
		{
			return;
		}

		string tempPath = pngPath + ".part.png";

		try
		{
			PreviewResult preview = _renderer.Render(result.GlbPath, tempPath, _options.PreviewSize, _options.PreviewSamples, _options.PreviewMargin);
			result.PreviewSeconds = preview.Seconds;
			result.AlphaMaterials = preview.AlphaMaterials;

			if (!preview.Success)
			{
				DeleteQuietly(tempPath);
				result.Warnings.Add($"Preview failed: {preview.Error}");
				Console.WriteLine($"[{result.Level.SceneName}] preview FAILED: {preview.Error}");
				return;
			}

			File.Move(tempPath, pngPath, overwrite: true);
			Console.WriteLine($"[{result.Level.SceneName}] preview {preview.Seconds:F1}s (alphaMats={preview.AlphaMaterials})");
		}
		catch (Exception exception)
		{
			DeleteQuietly(tempPath);
			result.Warnings.Add($"Preview failed: {exception.Message}");
			Console.WriteLine($"[{result.Level.SceneName}] preview FAILED: {exception.Message}");
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
