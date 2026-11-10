using System.Buffers.Binary;
using System.Text.Json;

namespace TldLevels.Exporter.Export;

/// <summary>
/// Quick structural facts about a produced GLB, read from its JSON chunk.
/// <para>
/// The bounds are the union of POSITION accessor min/max, which glTF stores in
/// node-local space. They are useful as a sanity check on geometry, not as a
/// world-space AABB of the level (the preview image covers that).
/// </para>
/// </summary>
public sealed record GlbStats(
	int Nodes,
	int Meshes,
	int Primitives,
	long Triangles,
	int Materials,
	int Textures,
	int Images,
	int SceneRoots,
	bool HasNonFiniteMeshBounds,
	double[]? MeshBoundsMin,
	double[]? MeshBoundsMax);

public static class GlbInspector
{
	private const uint GlbMagic = 0x46546C67;      // "glTF"
	private const uint JsonChunkType = 0x4E4F534A; // "JSON"
	private const int TrianglesMode = 4;           // glTF primitive mode TRIANGLES

	public static GlbStats Inspect(string path)
	{
		byte[] json = ReadJsonChunk(path);

		using JsonDocument document = JsonDocument.Parse(json);
		JsonElement root = document.RootElement;

		int primitives = 0;
		long triangles = 0;
		bool HasNonFiniteMeshBounds = false;
		double[]? MeshBoundsMin = null;
		double[]? MeshBoundsMax = null;

		if (root.TryGetProperty("meshes", out JsonElement meshes) && meshes.ValueKind == JsonValueKind.Array)
		{
			JsonElement accessors = root.TryGetProperty("accessors", out JsonElement acc) ? acc : default;
			foreach (JsonElement mesh in meshes.EnumerateArray())
			{
				if (!mesh.TryGetProperty("primitives", out JsonElement meshPrimitives) || meshPrimitives.ValueKind != JsonValueKind.Array)
				{
					continue;
				}

				foreach (JsonElement primitive in meshPrimitives.EnumerateArray())
				{
					primitives++;

					if (!primitive.TryGetProperty("attributes", out JsonElement attributes) || attributes.ValueKind != JsonValueKind.Object)
					{
						continue;
					}

					if (!attributes.TryGetProperty("POSITION", out JsonElement position) || !TryGetAccessor(accessors, position, out JsonElement positionAccessor))
					{
						continue;
					}

					int mode = primitive.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetInt32() : TrianglesMode;
					long vertexCount = positionAccessor.TryGetProperty("count", out JsonElement count) ? count.GetInt32() : 0;
					long indexCount = vertexCount;
					if (primitive.TryGetProperty("indices", out JsonElement indices) && TryGetAccessor(accessors, indices, out JsonElement indexAccessor))
					{
						indexCount = indexAccessor.TryGetProperty("count", out JsonElement indexCountElement) ? indexCountElement.GetInt32() : 0;
					}

					if (mode == TrianglesMode)
					{
						triangles += indexCount / 3;
					}

					AccumulateBounds(positionAccessor, ref MeshBoundsMin, ref MeshBoundsMax, ref HasNonFiniteMeshBounds);
				}
			}
		}

		return new GlbStats(
			Nodes: CountArray(root, "nodes"),
			Meshes: CountArray(root, "meshes"),
			Primitives: primitives,
			Triangles: triangles,
			Materials: CountArray(root, "materials"),
			Textures: CountArray(root, "textures"),
			Images: CountArray(root, "images"),
			SceneRoots: CountSceneRoots(root),
			HasNonFiniteMeshBounds: HasNonFiniteMeshBounds,
			MeshBoundsMin: MeshBoundsMin,
			MeshBoundsMax: MeshBoundsMax);
	}

	private static byte[] ReadJsonChunk(string path)
	{
		using FileStream stream = File.OpenRead(path);

		Span<byte> header = stackalloc byte[12];
		stream.ReadExactly(header);
		if (BinaryPrimitives.ReadUInt32LittleEndian(header) != GlbMagic)
		{
			throw new InvalidDataException($"Not a GLB file: {path}");
		}

		uint version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
		if (version != 2)
		{
			throw new InvalidDataException($"Unsupported GLB version {version}: {path}");
		}

		Span<byte> chunkHeader = stackalloc byte[8];
		stream.ReadExactly(chunkHeader);
		uint chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader);
		if (BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..]) != JsonChunkType)
		{
			throw new InvalidDataException($"First GLB chunk is not JSON: {path}");
		}

		byte[] json = new byte[chunkLength];
		stream.ReadExactly(json);
		return json;
	}

	private static bool TryGetAccessor(JsonElement accessors, JsonElement reference, out JsonElement accessor)
	{
		accessor = default;
		if (accessors.ValueKind != JsonValueKind.Array || reference.ValueKind != JsonValueKind.Number)
		{
			return false;
		}

		int index = reference.GetInt32();
		if (index < 0 || index >= accessors.GetArrayLength())
		{
			return false;
		}

		accessor = accessors[index];
		return true;
	}

	private static void AccumulateBounds(JsonElement accessor, ref double[]? min, ref double[]? max, ref bool hasNonFinite)
	{
		if (!accessor.TryGetProperty("min", out JsonElement minElement) || !accessor.TryGetProperty("max", out JsonElement maxElement))
		{
			return;
		}

		if (minElement.ValueKind != JsonValueKind.Array || maxElement.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		int dimensions = Math.Min(minElement.GetArrayLength(), maxElement.GetArrayLength());
		if (dimensions == 0)
		{
			return;
		}

		min ??= new double[dimensions];
		max ??= new double[dimensions];

		for (int i = 0; i < dimensions; i++)
		{
			double lo = minElement[i].GetDouble();
			double hi = maxElement[i].GetDouble();
			if (!double.IsFinite(lo) || !double.IsFinite(hi))
			{
				hasNonFinite = true;
				continue;
			}

			min[i] = Math.Min(min[i], lo);
			max[i] = Math.Max(max[i], hi);
		}
	}

	private static int CountArray(JsonElement root, string property) =>
		root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Array
			? value.GetArrayLength()
			: 0;

	private static int CountSceneRoots(JsonElement root)
	{
		if (!root.TryGetProperty("scenes", out JsonElement scenes) || scenes.ValueKind != JsonValueKind.Array || scenes.GetArrayLength() == 0)
		{
			return 0;
		}

		JsonElement first = scenes[0];
		return first.TryGetProperty("nodes", out JsonElement nodes) && nodes.ValueKind == JsonValueKind.Array
			? nodes.GetArrayLength()
			: 0;
	}
}
