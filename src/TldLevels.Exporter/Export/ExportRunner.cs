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
/// Loads one batch of levels with AssetRipper and exports each requested scene
/// as a single GLB, then runs the Blender preview.
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

	public IReadOnlyList<LevelResult> Run(IReadOnlyList<LevelEntry> levels)
	{
		List<LevelResult> results = levels.Select(level => new LevelResult { Level = level }).ToList();
		Dictionary<string, LevelResult> bySource = results.ToDictionary(
			result => result.Level.SourcePath,
			StringComparer.OrdinalIgnoreCase);

		foreach (List<LevelEntry> batch in Chunk(levels, _options.BatchSize))
		{
			List<LevelEntry> pending = [];

			foreach (LevelEntry level in batch)
			{
				LevelResult result = bySource[level.SourcePath];
				result.GlbPath = GlbPath(level);

				if (!_options.Force && File.Exists(result.GlbPath))
				{
					result.Status = "existing";
					result.Bytes = new FileInfo(result.GlbPath).Length;
					TryInspect(result);
					Console.WriteLine($"[{level.SceneName}] exists  {Megabytes(result.Bytes):F1} MB (use --force to re-export)");
					continue;
				}

				pending.Add(level);
			}

			if (pending.Count > 0)
			{
				LoadAndExport(pending, bySource);
			}

			foreach (LevelEntry level in batch)
			{
				RenderPreview(bySource[level.SourcePath]);
			}
		}

		return results;
	}

	private void LoadAndExport(List<LevelEntry> batch, IReadOnlyDictionary<string, LevelResult> bySource)
	{
		Directory.CreateDirectory(_options.OutputDirectory);

		Stopwatch loadWatch = Stopwatch.StartNew();
		FullConfiguration settings = new();
		ExportHandler handler = new(settings);
		GameData gameData = handler.LoadAndProcess([.. batch.Select(level => level.SourcePath)], LocalFileSystem.Instance);
		loadWatch.Stop();

		Console.WriteLine($"[load] {batch.Count} level(s) in {loadWatch.Elapsed.TotalSeconds:F1}s");

		Dictionary<string, LevelEntry> byName = new(StringComparer.OrdinalIgnoreCase);
		foreach (LevelEntry level in batch)
		{
			byName[level.SceneName] = level;
		}

		int matched = 0;
		foreach (IUnityObjectBase asset in gameData.GameBundle.FetchAssets())
		{
			if (asset is not SceneHierarchyObject scene)
			{
				continue;
			}

			if (!byName.TryGetValue(scene.Name.String, out LevelEntry? level))
			{
				continue;
			}

			matched++;
			LevelResult result = bySource[level.SourcePath];
			string glbPath = result.GlbPath ?? GlbPath(level);

			Stopwatch watch = Stopwatch.StartNew();
			try
			{
				bool success = GlbModelExporter.ExportModel(scene.Assets, glbPath, true, LocalFileSystem.Instance);
				watch.Stop();
				result.ExportSeconds = watch.Elapsed.TotalSeconds;

				if (!success || !File.Exists(glbPath))
				{
					result.Status = "failed";
					result.Error = "GlbModelExporter returned false";
					Console.WriteLine($"[{level.SceneName}] FAILED: exporter returned false");
					continue;
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
			}
			catch (Exception exception)
			{
				watch.Stop();
				result.ExportSeconds = watch.Elapsed.TotalSeconds;
				result.Status = "failed";
				result.Error = exception.Message;
				Console.WriteLine($"[{level.SceneName}] FAILED: {exception.Message}");
			}
		}

		foreach (LevelEntry level in batch)
		{
			LevelResult result = bySource[level.SourcePath];
			if (result.Status != "pending")
			{
				continue;
			}

			result.Status = "failed";
			result.Error = "AssetRipper did not produce a scene for this level";
			Console.WriteLine($"[{level.SceneName}] FAILED: no scene produced");
		}

		if (matched == 0)
		{
			Console.WriteLine("[load] warning: no requested scenes were found in the loaded data");
		}
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

		try
		{
			PreviewResult preview = _renderer.Render(result.GlbPath, pngPath, _options.PreviewSize, _options.PreviewSamples, _options.PreviewMargin);
			result.PreviewSeconds = preview.Seconds;
			result.AlphaMaterials = preview.AlphaMaterials;

			if (!preview.Success)
			{
				result.Warnings.Add($"Preview failed: {preview.Error}");
				Console.WriteLine($"[{result.Level.SceneName}] preview FAILED: {preview.Error}");
				return;
			}

			Console.WriteLine($"[{result.Level.SceneName}] preview {preview.Seconds:F1}s (alphaMats={preview.AlphaMaterials})");
		}
		catch (Exception exception)
		{
			result.Warnings.Add($"Preview failed: {exception.Message}");
			Console.WriteLine($"[{result.Level.SceneName}] preview FAILED: {exception.Message}");
		}
	}

	private void TryInspect(LevelResult result)
	{
		if (!_options.Validate || result.GlbPath is null)
		{
			return;
		}

		try
		{
			result.Stats = GlbInspector.Inspect(result.GlbPath);
		}
		catch (Exception exception)
		{
			result.Warnings.Add($"Inspection failed: {exception.Message}");
		}
	}

	private string GlbPath(LevelEntry level) =>
		Path.Combine(_options.OutputDirectory, level.SceneName + ".glb");

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
}
