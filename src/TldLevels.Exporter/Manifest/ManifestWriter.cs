using System.Globalization;
using System.Text;
using System.Text.Json;
using TldLevels.Exporter.Export;

namespace TldLevels.Exporter.Manifest;

/// <summary>
/// Writes <c>manifest.json</c> and <c>manifest.csv</c> describing one run.
/// Paths inside the manifest are relative to the manifest file.
/// </summary>
public static class ManifestWriter
{
	private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

	public static string Write(
		string outputDirectory,
		string inputDirectory,
		DateTimeOffset startedUtc,
		TimeSpan elapsed,
		IReadOnlyList<LevelResult> results,
		bool cancelled = false)
	{
		Directory.CreateDirectory(outputDirectory);

		object run = new
		{
			startedUtc = startedUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
			completedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
			assetRipper = "2.0.0",
			mergeVersion = ManifestState.CurrentMergeVersion,
			input = inputDirectory,
			levels = results.Count,
			exportedBytes = results.Sum(result => result.Bytes),
			seconds = Math.Round(elapsed.TotalSeconds, 1),
			succeeded = results.Count(result => result.Status is "ok" or "existing"),
			merged = results.Count(result => result.Status == "merged"),
			empty = results.Count(result => result.Status == "empty"),
			failed = results.Count(result => result.Status == "failed"),
			pending = results.Count(result => result.Status == "pending"),
			cancelled,
		};

		object[] levels = results.Select(result => new
		{
			id = result.Level.Id,
			scene = result.Level.SceneName,
			source = result.Level.SourcePath,
			sourceBytes = result.Level.SizeBytes,
			stub = result.Level.IsStub,
			glb = Relative(outputDirectory, result.GlbPath),
			bytes = result.Bytes,
			nodes = result.Stats?.Nodes,
			meshes = result.Stats?.Meshes,
			triangles = result.Stats?.Triangles,
			materials = result.Stats?.Materials,
			textures = result.Stats?.Textures,
			meshBoundsMin = result.Stats?.MeshBoundsMin,
			meshBoundsMax = result.Stats?.MeshBoundsMax,
			exportSeconds = Math.Round(result.ExportSeconds, 1),
			preview = Relative(outputDirectory, result.PreviewPath),
			previewSeconds = Math.Round(result.PreviewSeconds, 1),
			alphaMaterials = result.AlphaMaterials,
			status = result.Status,
			mergedInto = result.MergedInto,
			includes = result.Includes,
			error = result.Error,
			warnings = result.Warnings,
		}).ToArray();

		string jsonPath = Path.Combine(outputDirectory, "manifest.json");
		File.WriteAllText(jsonPath, JsonSerializer.Serialize(new { run, levels }, JsonOptions), Encoding.UTF8);

		string csvPath = Path.Combine(outputDirectory, "manifest.csv");
		File.WriteAllText(csvPath, BuildCsv(outputDirectory, results), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		return jsonPath;
	}

	private static string? Relative(string root, string? path)
	{
		if (path is null)
		{
			return null;
		}

		return Path.GetRelativePath(root, path).Replace('\\', '/');
	}

	private static string BuildCsv(string outputDirectory, IReadOnlyList<LevelResult> results)
	{
		StringBuilder builder = new();
		builder.AppendLine("id,scene,status,bytes,nodes,meshes,triangles,materials,textures,exportSeconds,previewSeconds,glb,preview,mergedInto,includes,warnings");

		foreach (LevelResult result in results)
		{
			builder.Append(result.Level.Id.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(Escape(result.Level.SceneName)).Append(',');
			builder.Append(Escape(result.Status)).Append(',');
			builder.Append(result.Bytes.ToString(CultureInfo.InvariantCulture)).Append(',');
			builder.Append(Number(result.Stats?.Nodes)).Append(',');
			builder.Append(Number(result.Stats?.Meshes)).Append(',');
			builder.Append(Number(result.Stats?.Triangles)).Append(',');
			builder.Append(Number(result.Stats?.Materials)).Append(',');
			builder.Append(Number(result.Stats?.Textures)).Append(',');
			builder.Append(result.ExportSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
			builder.Append(result.PreviewSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
			builder.Append(Escape(Relative(outputDirectory, result.GlbPath) ?? string.Empty)).Append(',');
			builder.Append(Escape(Relative(outputDirectory, result.PreviewPath) ?? string.Empty)).Append(',');
			builder.Append(Escape(result.MergedInto ?? string.Empty)).Append(',');
			builder.Append(Escape(string.Join("; ", result.Includes))).Append(',');
			builder.Append(Escape(string.Join("; ", result.Warnings))).AppendLine();
		}

		return builder.ToString();
	}

	private static string Number(long? value) =>
		value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

	private static string Number(int? value) =>
		value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

	private static string Escape(string value) =>
		value.Contains(',') || value.Contains('"') || value.Contains('\n')
			? '"' + value.Replace("\"", "\"\"") + '"'
			: value;
}
