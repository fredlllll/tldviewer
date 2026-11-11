using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Classes.ClassID_156;
using AssetRipper.SourceGenerated.Classes.ClassID_218;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;
using System.Numerics;

namespace TldLevels.Exporter.Export;

/// <summary>
/// Adds Unity Terrain components to a scene GLB.
/// <para>
/// AssetRipper exports <see cref="ITerrainData"/> only as a standalone asset; the
/// scene exporter walks GameObjects and renders MeshFilters, so a level whose
/// ground is a Unity Terrain comes out with its landscape missing (level 1 is
/// the obvious case: buildings and mountains float over nothing). This rebuilds
/// the heightmap mesh the same way AssetRipper's GlbTerrainBuilder does and
/// attaches it at the terrain's world transform.
/// </para>
/// </summary>
public static class TerrainBuilder
{
	/// <summary>
	/// Adds one mesh per distinct non-empty TerrainData referenced by a Terrain
	/// component. Returns how many terrains were added.
	/// </summary>
	public static int AddTerrains(SceneBuilder sceneBuilder, IEnumerable<IUnityObjectBase> assets)
	{
		HashSet<ITerrainData> seen = [];
		int added = 0;

		foreach (IUnityObjectBase asset in assets)
		{
			if (asset is not ITerrain terrain)
			{
				continue;
			}

			ITerrainData? data = terrain.TerrainDataP;
			string terrainName = terrain.GameObject_C2P?.Name.String ?? "<unnamed>";
			if (data is null)
			{
				Console.WriteLine($"[terrain] {terrainName}: no TerrainData");
				continue;
			}

			if (data.Heightmap.Heights.Count == 0)
			{
				Console.WriteLine($"[terrain] {terrainName}: TerrainData '{data.Name}' has no heights");
				continue;
			}

			if (!seen.Add(data))
			{
				Console.WriteLine($"[terrain] {terrainName}: shares TerrainData '{data.Name}'");
				continue;
			}

			Console.WriteLine(
				$"[terrain] {terrainName}: '{data.Name}' {data.Heightmap.Width}x{data.Heightmap.Height} " +
				$"res {data.Heightmap.Resolution} scale {data.Heightmap.Scale.X:F1},{data.Heightmap.Scale.Y:F1},{data.Heightmap.Scale.Z:F1}");

			if (TryAdd(sceneBuilder, terrain, data))
			{
				added++;
			}
		}

		return added;
	}

	private static bool TryAdd(SceneBuilder sceneBuilder, ITerrain terrain, ITerrainData data)
	{
		int width = Math.Max(data.Heightmap.Width, data.Heightmap.Resolution);
		int height = Math.Max(data.Heightmap.Height, data.Heightmap.Resolution);
		if (width < 2 || height < 2)
		{
			return false;
		}

		// Matches GlbTerrainBuilder: the scale already carries the glTF X flip.
		Vector3 meshScale = data.Heightmap.Scale.CastToStruct() * new Vector3(-1, 1, 1);
		Vector2 uvScale = new(1f / (width - 1), 1f / (height - 1));
		AssetList<short> heights = data.Heightmap.Heights;

		int w = height;
		int h = width;

		MaterialBuilder material = new("TerrainDefault");
		MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty> meshBuilder = new();
		var primitive = meshBuilder.UsePrimitive(material);
		VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty>[] vertices =
			new VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty>[w * h];

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float elevation = (float)heights[x + y * width] / short.MaxValue;
				Vector3 position = new Vector3(-y, elevation, x) * meshScale;
				Vector2 uv = new Vector2(x, y) * uvScale;
				vertices[y * w + x] = new(new VertexPosition(position), new VertexTexture1(uv));
			}
		}

		for (int y = 0; y < h - 1; y++)
		{
			for (int x = 0; x < w - 1; x++)
			{
				primitive.AddTriangle(
					vertices[y * w + x],
					vertices[y * w + x + 1],
					vertices[(y + 1) * w + x]);
				primitive.AddTriangle(
					vertices[(y + 1) * w + x],
					vertices[y * w + x + 1],
					vertices[(y + 1) * w + x + 1]);
			}
		}

		string name = terrain.GameObject_C2P?.Name.String ?? data.Name.String;
		NodeBuilder node = new(name)
		{
			LocalTransform = WorldTransform(terrain),
		};

		sceneBuilder.AddRigidMesh(meshBuilder, node);
		return true;
	}

	private static AffineTransform WorldTransform(ITerrain terrain)
	{
		Matrix4x4 global = Matrix4x4.Identity;
		ITransform? transform = terrain.GameObject_C2P?.GetTransform();
		for (ITransform? current = transform; current is not null; current = current.Father_C4P)
		{
			// Matches GlbLevelBuilder: global = local * parentGlobal.
			global *= current.ToTransformation().Matrix;
		}

		if (!Matrix4x4.Decompose(global, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
		{
			return AffineTransform.Identity;
		}

		// Unity -> glTF: mirror X, which also flips the rotation's axis.
		Vector3 gltfTranslation = new(-translation.X, translation.Y, translation.Z);
		Quaternion gltfRotation = new(rotation.X, -rotation.Y, -rotation.Z, rotation.W);
		return new AffineTransform(scale, gltfRotation, gltfTranslation);
	}
}
