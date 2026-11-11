using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.IO.Files;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

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
		HashSet<int> removedNodeIndices = [];
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
				removedNodeIndices.Add(previous.LogicalIndex);
				removed++;
			}

			last[key] = node;
		}

		if (removed > 0)
		{
			RewriteGlbWithoutMeshes(path, removedNodeIndices);
			Console.WriteLine($"[dedup] removed {removed} overlapping mesh placement(s), kept later instances");
		}
	}

	private static void RewriteGlbWithoutMeshes(string path, IReadOnlySet<int> nodeIndices)
	{
		string tempPath = path + ".dedup.part";
		using FileStream input = File.OpenRead(path);
		using BinaryReader reader = new(input, Encoding.UTF8, leaveOpen: true);
		uint magic = reader.ReadUInt32();
		uint version = reader.ReadUInt32();
		uint length = reader.ReadUInt32();
		uint jsonLength = reader.ReadUInt32();
		uint jsonType = reader.ReadUInt32();
		if (magic != 0x46546C67 || version != 2 || jsonType != 0x4E4F534A)
		{
			throw new InvalidDataException("Invalid GLB while applying duplicate filter");
		}

		JsonObject root = JsonNode.Parse(reader.ReadBytes(checked((int)jsonLength)))?.AsObject()
			?? throw new InvalidDataException("GLB JSON chunk is invalid");
		JsonArray nodes = root["nodes"]?.AsArray()
			?? throw new InvalidDataException("GLB has no nodes array");
		foreach (int index in nodeIndices)
		{
			if (index >= 0 && index < nodes.Count)
			{
				nodes[index]?.AsObject().Remove("mesh");
			}
		}

		byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
		int paddedLength = (json.Length + 3) & ~3;
		Array.Resize(ref json, paddedLength);
		for (int i = json.Length - 1; i >= json.Length - (paddedLength - json.Length); i--)
		{
			json[i] = 0x20;
		}

		using FileStream output = File.Create(tempPath);
		using BinaryWriter writer = new(output, Encoding.UTF8, leaveOpen: true);
		writer.Write(magic);
		writer.Write(version);
		writer.Write(length + (uint)(json.Length - jsonLength));
		writer.Write((uint)json.Length);
		writer.Write(jsonType);
		writer.Write(json);
		input.CopyTo(output);
		output.Flush();
		File.Move(tempPath, path, overwrite: true);
	}
}
