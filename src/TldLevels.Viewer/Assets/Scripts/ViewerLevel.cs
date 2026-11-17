using System;

namespace TldLevels.Viewer
{

/// <summary>One level file found in the game data folder.</summary>
public sealed class ViewerLevel
{
    public int Id;
    public string SceneName;
    public string SourcePath;
    public long SizeBytes;
    public bool Exported;

    public double Megabytes => SizeBytes / (1024.0 * 1024.0);

    public string Label =>
        $"{SceneName}  ·  {(Exported ? "exported" : "convert")}  ·  {Megabytes:F1} MB";

    public bool Matches(string filter) =>
        string.IsNullOrEmpty(filter)
        || SceneName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
}
}
