using System.Text.Json;

namespace TldLevels.Exporter.Manifest;

/// <summary>
/// The previous run's manifest, read only to answer one question on resume:
/// does an existing art GLB already contain the design overlays we found now?
/// <para>
/// Merge metadata is versioned. A manifest written before art/design merging
/// existed has no <c>mergeVersion</c>, so its art GLBs are always re-exported;
/// this makes the first run after upgrading repair the old prop-only outputs
/// automatically.
/// </para>
/// </summary>
public sealed class ManifestState
{
	public const int CurrentMergeVersion = 2;

	private readonly Dictionary<string, Entry> _entries;

	private ManifestState(int mergeVersion, Dictionary<string, Entry> entries)
	{
		MergeVersion = mergeVersion;
		_entries = entries;
	}

	public int MergeVersion { get; }

	public static ManifestState Load(string outputDirectory)
	{
		string path = Path.Combine(outputDirectory, "manifest.json");
		if (!File.Exists(path))
		{
			return new ManifestState(0, new(StringComparer.OrdinalIgnoreCase));
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
			JsonElement root = document.RootElement;

			int mergeVersion = 0;
			if (root.TryGetProperty("run", out JsonElement run)
				&& run.TryGetProperty("mergeVersion", out JsonElement versionElement)
				&& versionElement.TryGetInt32(out int parsedVersion))
			{
				mergeVersion = parsedVersion;
			}

			Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
			if (root.TryGetProperty("levels", out JsonElement levels))
			{
				foreach (JsonElement level in levels.EnumerateArray())
				{
					string? scene = level.TryGetProperty("scene", out JsonElement sceneElement) ? sceneElement.GetString() : null;
					if (string.IsNullOrEmpty(scene))
					{
						continue;
					}

					string status = level.TryGetProperty("status", out JsonElement statusElement)
						? statusElement.GetString() ?? "pending"
						: "pending";
					string? mergedInto = level.TryGetProperty("mergedInto", out JsonElement mergedElement)
						? mergedElement.GetString()
						: null;

					HashSet<string> includes = new(StringComparer.OrdinalIgnoreCase);
					if (level.TryGetProperty("includes", out JsonElement includesElement))
					{
						foreach (JsonElement include in includesElement.EnumerateArray())
						{
							string? name = include.GetString();
							if (!string.IsNullOrEmpty(name))
							{
								includes.Add(name);
							}
						}
					}

					entries[scene] = new Entry(status, mergedInto, includes);
				}
			}

			return new ManifestState(mergeVersion, entries);
		}
		catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
		{
			Console.WriteLine($"[manifest] could not read the previous manifest: {exception.Message}");
			return new ManifestState(0, new(StringComparer.OrdinalIgnoreCase));
		}
	}

	/// <summary>
	/// True when the previous run recorded this art GLB as already containing
	/// exactly these overlays and the GLB still exists.
	/// <para>
	/// A missing preview must not invalidate the export: enabling previews on a
	/// later run should render the PNG, not rebuild an 80 MB mesh merge.
	/// </para>
	/// </summary>
	public bool IsComplete(
		string artScene,
		IReadOnlyCollection<string> overlays,
		string glbPath)
	{
		if (MergeVersion < CurrentMergeVersion || !_entries.TryGetValue(artScene, out Entry? entry))
		{
			return false;
		}

		if (entry.Status is not ("ok" or "existing" or "empty"))
		{
			return false;
		}

		if (!File.Exists(glbPath))
		{
			return false;
		}

		return entry.Includes.SetEquals(overlays);
	}

	private sealed record Entry(string Status, string? MergedInto, HashSet<string> Includes);
}
