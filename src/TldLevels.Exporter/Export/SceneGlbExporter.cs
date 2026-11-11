using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.IO.Files;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;
using System.Numerics;

namespace TldLevels.Exporter.Export;

/// <summary>
/// Writes a whole Unity scene to a GLB, including Unity Terrain landscapes that
/// <see cref="GlbModelExporter"/> does not attach to the scene.
/// </summary>
public static class SceneGlbExporter
{
	public static bool Export(
		IEnumerable<IUnityObjectBase> assets,
		string path,
		FileSystem fileSystem,
		bool allLods,
		out int terrainCount)
	{
		List<IUnityObjectBase> assetList = [.. assets];
		GlbLevelBuilder.IncludeAllLods = allLods;
		SceneBuilder sceneBuilder = GlbLevelBuilder.Build(assetList, isScene: true);
		terrainCount = TerrainBuilder.AddTerrains(sceneBuilder, assetList);

		using Stream stream = fileSystem.File.Create(path);
		bool success = GlbWriter.TryWrite(sceneBuilder, stream, out _);
		stream.Dispose();
		if (success)
		{
			RemoveDuplicatePlacements(path);
		}

		return success;
	}

	/// <summary>
	/// Removes earlier mesh nodes with the same name and final glTF world
	/// position. This runs on the generated graph because nested Unity prefab
	/// transforms can have different source pivots while resolving identically
	/// after glTF hierarchy conversion.
	/// </summary>
	private static void RemoveDuplicatePlacements(string path)
	{
		ModelRoot model = ModelRoot.Load(path);
		Dictionary<string, Node> last = new(StringComparer.Ordinal);
		int removed = 0;

		foreach (Node node in model.LogicalNodes)
		{
			if (node.Mesh is null)
			{
				continue;
			}

			Vector3 position = node.WorldMatrix.Translation;
			string key = $"{node.Name}\0{MathF.Round(position.X, 1)}\0{MathF.Round(position.Y, 1)}\0{MathF.Round(position.Z, 1)}";
			if (last.TryGetValue(key, out Node? previous))
			{
				previous.Mesh = null;
				removed++;
			}

			last[key] = node;
		}

		if (removed > 0)
		{
			model.SaveGLB(path);
			Console.WriteLine($"[dedup] removed {removed} overlapping mesh placement(s), kept later instances");
		}
	}
}
