using System.Globalization;

namespace TldLevels.Exporter.Cli;

/// <summary>
/// Command-line options. Defaults use the VirtualBox shared-folder UNC paths
/// (not drive letters) because the guest maps them as Y:/Z: and the letters
/// have been observed to swap between sessions.
/// </summary>
public sealed class Options
{
	public const string DefaultInputDirectory = @"\\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data";
	public const string DefaultOutputDirectory = @"\\VBoxSvr\TLDLevels\tldlevels\levels";

	public string InputDirectory { get; set; } = DefaultInputDirectory;

	public string OutputDirectory { get; set; } = DefaultOutputDirectory;

	/// <summary>"all", "9", "2-40,76,120". Null or "all" means every level.</summary>
	public string? LevelSpec { get; set; }

	/// <summary>Levels loaded per AssetRipper instance.</summary>
	public int BatchSize { get; set; } = 1;

	public bool SkipStubs { get; set; }

	public bool Force { get; set; }

	/// <summary>Merge design-overlay scenes into their preceding art scene.</summary>
	public bool MergeOverlays { get; set; } = true;

	/// <summary>Cell edge length in metres.</summary>
	public float CellSize { get; set; } = 256f;

	/// <summary>Explicit design/base scene associations, expressed as overlayId=baseId.</summary>
	public Dictionary<int, int> SceneMerges { get; } = [];

	public bool DryRun { get; set; }

	public bool ShowHelp { get; set; }

	public static Options Parse(string[] args)
	{
		Options options = new();

		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			switch (arg)
			{
				case "-h":
				case "--help":
					options.ShowHelp = true;
					break;
				case "--input":
					options.InputDirectory = RequireValue(args, ref i, arg);
					break;
				case "--output":
					options.OutputDirectory = RequireValue(args, ref i, arg);
					break;
				case "--levels":
					options.LevelSpec = RequireValue(args, ref i, arg);
					break;
				case "--batch-size":
					options.BatchSize = RequirePositiveInt(args, ref i, arg);
					break;
				case "--skip-stubs":
					options.SkipStubs = true;
					break;
				case "--force":
					options.Force = true;
					break;
				case "--merge-overlays":
					options.MergeOverlays = RequireBool(args, ref i, arg);
					break;
				case "--no-merge-overlays":
					options.MergeOverlays = false;
					break;
				case "--cell-size":
					options.CellSize = RequirePositiveFloat(args, ref i, arg);
					break;
				case "--merge-scene":
					ParseSceneMerge(options, RequireValue(args, ref i, arg), arg);
					break;
				case "--dry-run":
					options.DryRun = true;
					break;
				default:
					throw new ArgumentException($"Unknown option: {arg}");
			}
		}

		return options;
	}

	public static string Usage => """
		TldLevels.Exporter - export The Long Dark Wintermute levels as streamed viewer packages

		Usage: TldLevels.Exporter [options]

		  --input <dir>            Campaign data directory
		                           default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
		  --output <dir>           Where level.viewer packages, shared assets, manifest and logs are written
		                           default: \\VBoxSvr\TLDLevels\tldlevels\levels
		  --levels <spec>          all | 9 | 2-40,76,120        default: all (level0 excluded)
		  --batch-size <n>         Levels per AssetRipper load     default: 1
		  --skip-stubs             Skip levels smaller than 20 KB  default: false
		  --force                  Re-export packages that already exist
		  --merge-overlays <bool>  Merge prop-only design scenes into their art scene  default: true
		  --no-merge-overlays      Export each level file on its own instead
		  --cell-size <m>          Cell edge length               default: 256
		  --merge-scene <a=b>      Export scene a with base scene b (repeatable)
		  --dry-run                List what would be exported, then exit
		  -h, --help               Show this text

		Exit codes: 0 = success, 1 = invalid arguments / nothing to do, 2 = some levels failed.
		""";

	private static string RequireValue(string[] args, ref int index, string option)
	{
		if (index + 1 >= args.Length)
		{
			throw new ArgumentException($"Missing value for {option}");
		}

		return args[++index];
	}

	private static int RequirePositiveInt(string[] args, ref int index, string option)
	{
		string text = RequireValue(args, ref index, option);
		if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) || value <= 0)
		{
			throw new ArgumentException($"Invalid positive integer for {option}: '{text}'");
		}

		return value;
	}

	private static float RequirePositiveFloat(string[] args, ref int index, string option)
	{
		string text = RequireValue(args, ref index, option);
		if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value <= 0)
		{
			throw new ArgumentException($"Invalid positive number for {option}: '{text}'");
		}

		return value;
	}

	private static bool RequireBool(string[] args, ref int index, string option)
	{
		string text = RequireValue(args, ref index, option);
		return text.ToLowerInvariant() switch
		{
			"true" or "1" or "yes" or "on" => true,
			"false" or "0" or "no" or "off" => false,
			_ => throw new ArgumentException($"Invalid boolean for {option}: '{text}'"),
		};
	}

	private static void ParseSceneMerge(Options options, string text, string option)
	{
		int separator = text.IndexOf('=', StringComparison.Ordinal);
		if (separator <= 0 || separator == text.Length - 1
			|| !int.TryParse(text[..separator], NumberStyles.Integer, CultureInfo.InvariantCulture, out int overlay)
			|| !int.TryParse(text[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int baseLevel)
			|| overlay < 0 || baseLevel < 0)
		{
			throw new ArgumentException($"Invalid scene merge for {option}; expected overlayId=baseId, got '{text}'");
		}

		options.SceneMerges[overlay] = baseLevel;
	}
}
