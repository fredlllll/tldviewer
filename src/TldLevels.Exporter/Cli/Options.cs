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

	public bool Validate { get; set; } = true;

	public bool Preview { get; set; } = true;

	/// <summary>Merge design-overlay scenes into their preceding art scene.</summary>
	public bool MergeOverlays { get; set; } = true;

	public string? BlenderPath { get; set; }

	public string? PreviewScript { get; set; }

	public int PreviewSize { get; set; } = 2048;

	public int PreviewSamples { get; set; } = 16;

	public double PreviewMargin { get; set; } = 1.05;

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
				case "--validate":
					options.Validate = RequireBool(args, ref i, arg);
					break;
				case "--no-validate":
					options.Validate = false;
					break;
				case "--preview":
					options.Preview = RequireBool(args, ref i, arg);
					break;
				case "--no-preview":
					options.Preview = false;
					break;
				case "--merge-overlays":
					options.MergeOverlays = RequireBool(args, ref i, arg);
					break;
				case "--no-merge-overlays":
					options.MergeOverlays = false;
					break;
				case "--blender":
					options.BlenderPath = RequireValue(args, ref i, arg);
					break;
				case "--preview-script":
					options.PreviewScript = RequireValue(args, ref i, arg);
					break;
				case "--preview-size":
					options.PreviewSize = RequirePositiveInt(args, ref i, arg);
					break;
				case "--preview-samples":
					options.PreviewSamples = RequirePositiveInt(args, ref i, arg);
					break;
				case "--preview-margin":
					options.PreviewMargin = RequirePositiveDouble(args, ref i, arg);
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
		TldLevels.Exporter - export The Long Dark Wintermute levels to GLB (+ previews)

		Usage: TldLevels.Exporter [options]

		  --input <dir>            Campaign data directory
		                           default: \\VBoxSvr\TheLongDark\tld_dlc\wintermute\tld_Data
		  --output <dir>           Where GLBs, PNGs, manifest and logs are written
		                           default: \\VBoxSvr\TLDLevels\tldlevels\levels
		  --levels <spec>          all | 9 | 2-40,76,120        default: all (level0 excluded)
		  --batch-size <n>         Levels per AssetRipper load     default: 1
		  --skip-stubs             Skip levels smaller than 20 KB  default: false
		  --force                  Re-export levels whose GLB exists
		  --validate <bool>        Inspect each GLB after writing   default: true
		  --no-validate            Same as --validate false
		  --preview <bool>         Render a top-down PNG           default: true
		  --no-preview             Same as --preview false
		  --merge-overlays <bool>  Merge prop-only design scenes into their art scene  default: true
		  --no-merge-overlays      Export each level file on its own instead
		  --blender <exe>          Blender executable (auto-detected when omitted)
		  --preview-script <path>  Render script (auto-detected when omitted)
		  --preview-size <px>      Preview resolution               default: 2048
		  --preview-samples <n>    Cycles samples per preview        default: 16
		  --preview-margin <f>     Fraction of frame margin          default: 1.05
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

	private static double RequirePositiveDouble(string[] args, ref int index, string option)
	{
		string text = RequireValue(args, ref index, option);
		if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value <= 0)
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
}
