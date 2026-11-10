using System.Globalization;
using System.Text.RegularExpressions;

namespace TldLevels.Exporter.Levels;

/// <summary>A serialized Unity level file such as <c>level9</c>.</summary>
public sealed record LevelEntry(int Id, string SceneName, string SourcePath, long SizeBytes, bool IsStub);

public static partial class LevelCatalog
{
	/// <summary>Files below this size are chapter stubs with no usable geometry.</summary>
	public const long StubThresholdBytes = 20 * 1024;

	[GeneratedRegex(@"^level(\d+)$", RegexOptions.CultureInvariant)]
	private static partial Regex LevelFilePattern();

	public static IReadOnlyList<LevelEntry> Discover(string inputDirectory, string? levelSpec, bool skipStubs)
	{
		if (!Directory.Exists(inputDirectory))
		{
			throw new DirectoryNotFoundException($"Input directory not found: {inputDirectory}");
		}

		HashSet<int>? selected = ParseSpec(levelSpec);
		bool includeMenuLevel = selected?.Contains(0) == true;

		List<LevelEntry> levels = [];
		foreach (string file in Directory.EnumerateFiles(inputDirectory))
		{
			string name = Path.GetFileName(file);
			Match match = LevelFilePattern().Match(name);
			if (!match.Success)
			{
				continue;
			}

			int id = int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture);
			if (id == 0 && !includeMenuLevel)
			{
				continue;
			}

			if (selected is not null && !selected.Contains(id))
			{
				continue;
			}

			long size = new FileInfo(file).Length;
			bool isStub = size < StubThresholdBytes;
			if (isStub && skipStubs)
			{
				continue;
			}

			levels.Add(new LevelEntry(id, name, file, size, isStub));
		}

		levels.Sort(static (a, b) => a.Id.CompareTo(b.Id));
		return levels;
	}

	/// <summary>Parses "all", "9", or "2-40,76,120" into a set of ids. Null means all.</summary>
	public static HashSet<int>? ParseSpec(string? levelSpec)
	{
		if (string.IsNullOrWhiteSpace(levelSpec) || levelSpec.Equals("all", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		HashSet<int> ids = [];
		string[] tokens = levelSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (tokens.Length == 0)
		{
			return null;
		}

		foreach (string token in tokens)
		{
			int dash = token.IndexOf('-', StringComparison.Ordinal);
			if (dash < 0)
			{
				ids.Add(ParseId(token));
				continue;
			}

			int from = ParseId(token[..dash]);
			int to = ParseId(token[(dash + 1)..]);
			if (to < from)
			{
				(from, to) = (to, from);
			}

			for (int id = from; id <= to; id++)
			{
				ids.Add(id);
			}
		}

		return ids;
	}

	private static int ParseId(string text)
	{
		if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) && id >= 0)
		{
			return id;
		}

		throw new FormatException($"Invalid level id: '{text}'");
	}
}
