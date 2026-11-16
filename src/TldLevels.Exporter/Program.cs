using System.Diagnostics;
using System.Globalization;
using AssetRipper.Import.Logging;
using TldLevels.Exporter.Cli;
using TldLevels.Exporter.Export;
using TldLevels.Exporter.Levels;
using TldLevels.Exporter.Manifest;

namespace TldLevels.Exporter;

internal static class Program
{
	private static int Main(string[] args)
	{
		Options options;
		try
		{
			options = Options.Parse(args);
		}
		catch (ArgumentException exception)
		{
			Console.Error.WriteLine(exception.Message);
			Console.Error.WriteLine();
			Console.Error.WriteLine(Options.Usage);
			return 1;
		}

		if (options.ShowHelp)
		{
			Console.WriteLine(Options.Usage);
			return 0;
		}

		IReadOnlyList<LevelEntry> levels;
		IReadOnlyList<LevelEntry> allLevels;
		try
		{
			levels = LevelCatalog.Discover(options.InputDirectory, options.LevelSpec, options.SkipStubs);
			allLevels = LevelCatalog.Discover(options.InputDirectory, levelSpec: null, skipStubs: false);
		}
		catch (Exception exception) when (exception is DirectoryNotFoundException or FormatException or IOException or UnauthorizedAccessException)
		{
			Console.Error.WriteLine(exception.Message);
			return 1;
		}

		if (levels.Count == 0)
		{
			Console.Error.WriteLine("No levels matched the requested selection.");
			return 1;
		}

		if (options.DryRun)
		{
			PrintPlan(levels, options);
			return 0;
		}

		Directory.CreateDirectory(options.OutputDirectory);

		Logger.Add(new ConsoleLogger(true));
		Logger.Add(new FileLogger(Path.Combine(options.OutputDirectory, "export.log")));

		Console.WriteLine($"[run] {levels.Count} level(s) from {options.InputDirectory}");
		Console.WriteLine($"[run] output {options.OutputDirectory}");
		Console.WriteLine($"[run] batch size {options.BatchSize}, press Ctrl+C to stop after the current level");

		using CancellationTokenSource cancellation = new();
		int cancelRequests = 0;
		ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
		{
			if (Interlocked.Increment(ref cancelRequests) > 1)
			{
				Console.WriteLine("[cancel] aborting immediately");
				return;
			}

			eventArgs.Cancel = true;
			Console.WriteLine("[cancel] finishing the current level (Ctrl+C again to abort now)");
			cancellation.Cancel();
		};
		Console.CancelKeyPress += cancelHandler;

		DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
		Stopwatch watch = Stopwatch.StartNew();
		ExportRunner runner = new(options, allLevels);

		// The manifest is rewritten after every completed level, so an
		// interruption never loses the record of what has already been done.
		IReadOnlyList<LevelResult> results = runner.Run(
			levels,
			snapshot => ManifestWriter.Write(
				options.OutputDirectory,
				options.InputDirectory,
				startedUtc,
				watch.Elapsed,
				snapshot,
				cancellation.IsCancellationRequested),
			cancellation.Token);

		watch.Stop();
		Console.CancelKeyPress -= cancelHandler;

		string manifestPath = ManifestWriter.Write(
			options.OutputDirectory,
			options.InputDirectory,
			startedUtc,
			watch.Elapsed,
			results,
			cancellation.IsCancellationRequested);

		PrintSummary(results, watch.Elapsed, manifestPath, cancellation.IsCancellationRequested);

		if (cancellation.IsCancellationRequested)
		{
			return 130;
		}

		return results.Any(result => result.Status == "failed") ? 2 : 0;
	}

	private static void PrintPlan(IReadOnlyList<LevelEntry> levels, Options options)
	{
		long totalSource = levels.Sum(level => level.SizeBytes);
		int stubs = levels.Count(level => level.IsStub);

		Console.WriteLine($"Would export {levels.Count} level(s) from {options.InputDirectory}");
		Console.WriteLine($"Output: {options.OutputDirectory}");
		Console.WriteLine($"Source size: {Megabytes(totalSource):F1} MB, stubs: {stubs}");

		foreach (LevelEntry level in levels)
		{
			Console.WriteLine($"  {level.SceneName,-10} {Megabytes(level.SizeBytes),8:F1} MB{(level.IsStub ? "  (stub)" : string.Empty)}");
		}
	}

	private static void PrintSummary(IReadOnlyList<LevelResult> results, TimeSpan elapsed, string manifestPath, bool cancelled)
	{
		int ok = results.Count(result => result.Status == "ok");
		int existing = results.Count(result => result.Status == "existing");
		int empty = results.Count(result => result.Status == "empty");
		int failed = results.Count(result => result.Status == "failed");
		int pending = results.Count(result => result.Status == "pending");
		long bytes = results.Sum(result => result.Bytes);

		Console.WriteLine();
		Console.WriteLine(
			$"[done] ok={ok} existing={existing} empty={empty} failed={failed} pending={pending} " +
			$"bytes={Megabytes(bytes):F1} MB time={elapsed.TotalSeconds:F1}s");

		foreach (LevelResult result in results.Where(result => result.Status == "failed"))
		{
			Console.WriteLine($"  FAILED {result.Level.SceneName}: {result.Error}");
		}

		int warned = results.Count(result => result.Warnings.Count > 0);
		if (warned > 0)
		{
			Console.WriteLine($"  warnings: {warned} level(s), see manifest");
		}

		if (cancelled)
		{
			Console.WriteLine($"  interrupted: {pending} level(s) still pending");
		}

		Console.WriteLine($"[done] manifest {manifestPath}");
	}

	private static double Megabytes(long bytes) => bytes / 1024.0 / 1024.0;
}
