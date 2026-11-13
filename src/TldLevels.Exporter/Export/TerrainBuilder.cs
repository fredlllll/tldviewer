using AssetRipper.Assets;
using AssetRipper.Assets.Generics;
using AssetRipper.SourceGenerated.Classes.ClassID_156;
using AssetRipper.SourceGenerated.Classes.ClassID_1953259897;
using AssetRipper.SourceGenerated.Classes.ClassID_218;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using AssetRipper.SourceGenerated.Subclasses.PPtr_TerrainLayer;
using AssetRipper.Export.Modules.Models;
using AssetRipper.Export.Modules.Textures;
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

			bool clipZeroHeightCells = IsAuxiliaryWaterTerrain(terrainName);
			Console.WriteLine(
				$"[terrain] {terrainName}: '{data.Name}' {data.Heightmap.Width}x{data.Heightmap.Height} " +
				$"samples {data.Heightmap.Heights.Count} res {data.Heightmap.Resolution} scale {data.Heightmap.Scale.X:F1},{data.Heightmap.Scale.Y:F1},{data.Heightmap.Scale.Z:F1}" +
				(clipZeroHeightCells ? " (zero-height cells clipped)" : string.Empty));

			if (TryAdd(sceneBuilder, terrain, data, clipZeroHeightCells))
			{
				added++;
			}
		}

		return added;
	}

	private static bool IsAuxiliaryWaterTerrain(string name) =>
		name.Contains("water", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("river", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("lake", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("pond", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("marsh", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("creek", StringComparison.OrdinalIgnoreCase)
		|| name.Contains("ice", StringComparison.OrdinalIgnoreCase);

	private static bool TryAdd(SceneBuilder sceneBuilder, ITerrain terrain, ITerrainData data, bool clipZeroHeightCells)
	{
		if (!TryBuild(terrain, data, clipZeroHeightCells, out MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>? meshBuilder, out AffineTransform worldTransform, out string name))
		{
			return false;
		}

		NodeBuilder node = new(name)
		{
			LocalTransform = worldTransform,
		};

		sceneBuilder.AddRigidMesh(meshBuilder, node);
		return true;
	}

	/// <summary>
	/// Yields terrain landscapes as standalone draw items, matching the meshes
	/// that <see cref="AddTerrains"/> adds to a whole-scene export.
	/// </summary>
	public static IEnumerable<GlbDrawMesh> BuildDrawMeshes(IEnumerable<IUnityObjectBase> assets)
	{
		HashSet<ITerrainData> seen = [];
		foreach (IUnityObjectBase asset in assets)
		{
			if (asset is not ITerrain terrain)
			{
				continue;
			}

			ITerrainData? data = terrain.TerrainDataP;
			string terrainName = terrain.GameObject_C2P?.Name.String ?? "<unnamed>";
			if (data is null || data.Heightmap.Heights.Count == 0 || !seen.Add(data))
			{
				continue;
			}

			bool clipZeroHeightCells = IsAuxiliaryWaterTerrain(terrainName);
			if (TryBuild(terrain, data, clipZeroHeightCells, out MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>? meshBuilder, out AffineTransform worldTransform, out string name))
			{
				yield return new GlbDrawMesh(name, meshBuilder!, worldTransform.Matrix, 0);
			}
		}
	}

	private static bool TryBuild(
		ITerrain terrain,
		ITerrainData data,
		bool clipZeroHeightCells,
		out MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty>? meshBuilder,
		out AffineTransform worldTransform,
		out string name)
	{
		meshBuilder = null;
		worldTransform = AffineTransform.Identity;
		name = terrain.GameObject_C2P?.Name.String ?? data.Name.String;

		int width = data.Heightmap.Width;
		int height = data.Heightmap.Height;
		if (width < 2 || height < 2 || width * height != data.Heightmap.Heights.Count)
		{
			int inferred = (int)MathF.Sqrt(data.Heightmap.Heights.Count);
			if (inferred * inferred != data.Heightmap.Heights.Count)
			{
				return false;
			}

			width = inferred;
			height = inferred;
		}
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
		if (TryGetFirstLayerImage(data, out ImageBuilder? baseImage))
		{
			material.WithBaseColor(baseImage);
		}
		MeshBuilder<VertexPosition, VertexTexture1, VertexEmpty> builder = new();
		var primitive = builder.UsePrimitive(material);
		VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty>[] vertices =
			new VertexBuilder<VertexPosition, VertexTexture1, VertexEmpty>[w * h];

		for (int y = 0; y < h; y++)
		{
			for (int x = 0; x < w; x++)
			{
				float elevation = (float)heights[x + y * width] / short.MaxValue;
				// Unity terrain data uses x across the first dimension and z across
				// the second. Do not swap those axes: the X handedness conversion is
				// already carried by meshScale, and swapping them rotates the whole
				// landscape by 90 degrees relative to scene objects.
				Vector3 position = new Vector3(x, elevation, y) * meshScale;
				Vector2 uv = new Vector2(x, y) * uvScale;
				vertices[y * w + x] = new(new VertexPosition(position), new VertexTexture1(uv));
			}
		}

		for (int y = 0; y < h - 1; y++)
		{
			for (int x = 0; x < w - 1; x++)
			{
				if (clipZeroHeightCells
					&& (heights[x + y * width] == 0
						|| heights[x + 1 + y * width] == 0
						|| heights[x + (y + 1) * width] == 0
						|| heights[x + 1 + (y + 1) * width] == 0))
				{
					continue;
				}

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

		meshBuilder = builder;
		worldTransform = WorldTransform(terrain);
		return true;
	}

	private static bool TryGetFirstLayerImage(ITerrainData data, out ImageBuilder? image)
	{
		image = null;
		if (data.SplatDatabase.TerrainLayers is not { } layers || layers.Count == 0)
		{
			return false;
		}

		IPPtr_TerrainLayer pointer = layers[0];
		if (!pointer.TryGetAsset(data.Collection, out ITerrainLayer? layer)
			|| layer?.DiffuseTextureP is not { } texture
			|| !TextureConverter.TryConvertToBitmap(texture, out var bitmap))
		{
			return false;
		}

		using MemoryStream stream = new();
		bitmap.SaveAsPng(stream);
		image = ImageBuilder.From(stream.ToArray(), texture.Name.String);
		return image is not null;
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
