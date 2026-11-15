using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Schema2;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;

namespace TldLevels.Exporter.Export;

public sealed record ViewerPackageResult(
	string Path,
	int Cells,
	int MeshCount,
	long Triangles,
	int Lods,
	long Bytes,
	long SharedBytes,
	int Assets,
	int Instances,
	int SkippedNodes,
	int SharedNodes);

/// <summary>
/// Writes a streamed viewer package from the source scene in a single pass.
/// <para>
/// Props, trees, houses and every other normal object are content-addressed
/// once into the shared library at the output root, with all of their authored
/// LODs. A cell then only stores the transforms of the objects that touch it
/// (<c>instances.json</c>) plus the geometry that cannot be shared: terrain,
/// sky shells and distant backdrop, and oversized meshes that would otherwise
/// span many cells.
/// </para>
/// <para>
/// Geometry is assigned to a fixed world-space X/Z grid by triangle centroid,
/// so large cell-local meshes are actually cut instead of being copied into
/// every overlapping cell. Every level shares the same <c>shared/assets</c> and
/// <c>shared/textures</c> folders, so identical meshes and textures are stored
/// once across the whole export.
/// </para>
/// </summary>
public static class ViewerPackageExporter
{
	private const string SharedFolder = "shared";
	private const string AssetsFolder = "assets";
	private const string TexturesFolder = "textures";
	private const string CellsFolder = "cells";

	public static ViewerPackageResult Export(
		IEnumerable<IUnityObjectBase> assets,
		string packageDirectory,
		float cellSize,
		bool includeAllLods)
	{
		if (cellSize <= 0f)
		{
			throw new ArgumentOutOfRangeException(nameof(cellSize));
		}

		List<IUnityObjectBase> assetList = [.. assets];
		string tempDirectory = packageDirectory + ".part";
		DeleteDirectory(tempDirectory);

		Directory.CreateDirectory(tempDirectory);

		string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(packageDirectory))!;
		string sharedDirectory = Path.Combine(outputDirectory, SharedFolder);
		string assetDirectory = Path.Combine(sharedDirectory, AssetsFolder);
		string textureDirectory = Path.Combine(sharedDirectory, TexturesFolder);
		Directory.CreateDirectory(assetDirectory);
		Directory.CreateDirectory(textureDirectory);

		Dictionary<(int X, int Z), PackageCell> cells = [];
		Dictionary<string, SharedAsset> sharedAssets = new(StringComparer.Ordinal);
		Dictionary<(string Name, string Matrix), SharedObject> sharedObjects = [];
		MaterialFingerprints materialFingerprints = new();
		HashSet<string> materialNames = new(StringComparer.Ordinal);
		long drawIndex = 0;
		long triangleCount = 0;
		int meshCount = 0;

		// Playable-area bounds: only Unity Terrain surfaces contribute. Sky
		// shells, distant backdrops and water planes can otherwise dwarf the
		// area a user wants to inspect when the package is first framed.
		Vector3 focusMin = new(float.PositiveInfinity);
		Vector3 focusMax = new(float.NegativeInfinity);

		IEnumerable<GlbDrawMesh> drawMeshes = GlbLevelBuilder
			.BuildDrawMeshes(assetList, includeAllLods)
			.Concat(TerrainBuilder.BuildDrawMeshes(assetList));

		foreach (GlbDrawMesh drawMesh in drawMeshes)
		{
			meshCount++;
			bool cellLocal = IsCellLocal(
				drawMesh,
				cellSize,
				out Vector3 boundsMin,
				out Vector3 boundsMax);
			if (cellLocal)
			{
				triangleCount += PartitionMesh(
					drawMesh,
					drawIndex++,
					cellSize,
					cells,
					materialNames,
					ref focusMin,
					ref focusMax);
			}
			else
			{
				triangleCount += AddSharedObject(
					drawMesh,
					cellSize,
					boundsMin,
					boundsMax,
					cells,
					sharedObjects,
					sharedAssets,
					materialFingerprints,
					materialNames,
					assetDirectory,
					textureDirectory);
			}
		}

		int skippedNodes = 0;
		int cellSharedNodes = 0;
		foreach (PackageCell cell in cells.Values)
		{
			string cellDirectory = Path.Combine(tempDirectory, CellsFolder, cell.Id);
			Directory.CreateDirectory(cellDirectory);

			foreach (int lod in cell.Lods.Keys.OrderBy(value => value))
			{
				string fileName = $"lod{lod}.gltf";
				string path = Path.Combine(cellDirectory, fileName);
				skippedNodes += WriteCellLod(
					path,
					cell,
					lod,
					textureDirectory,
					out long writtenTriangles,
					out int cellShared);
				cell.WrittenTriangles[lod] = writtenTriangles;
				cellSharedNodes += cellShared;
			}

			if (cell.Instances.Count > 0)
			{
				WriteInstances(Path.Combine(cellDirectory, "instances.json"), cell);
			}
		}

		if (skippedNodes > 0)
		{
			Console.WriteLine($"[package] skipped {skippedNodes} node(s) with an invalid world matrix");
		}

		if (cellSharedNodes > 0)
		{
			Console.WriteLine(
				$"[package] {cellSharedNodes:N0} cell-local node(s) reuse an identical mesh");
		}

		int lodCount = cells.Values
			.SelectMany(cell => cell.Lods.Keys.Concat(cell.Instances.SelectMany(item => item.Lods.Keys)))
			.DefaultIfEmpty(0)
			.Max() + 1;
		Vector3 min = cells.Values
			.Select(cell => cell.Min)
			.DefaultIfEmpty(Vector3.Zero)
			.Aggregate(Vector3.Min);
		Vector3 max = cells.Values
			.Select(cell => cell.Max)
			.DefaultIfEmpty(Vector3.Zero)
			.Aggregate(Vector3.Max);
		if (float.IsPositiveInfinity(focusMin.X) || float.IsNegativeInfinity(focusMax.X))
		{
			focusMin = min;
			focusMax = max;
		}

		long sharedBytes = sharedAssets.Values.Sum(asset => asset.Bytes);
		int instanceCount = sharedObjects.Count;

		WriteManifest(
			Path.Combine(tempDirectory, "manifest.json"),
			Path.GetFileName(packageDirectory),
			cellSize,
			includeAllLods,
			lodCount,
			min,
			max,
			focusMin,
			focusMax,
			sharedAssets.Values,
			cells.Values);

		WriteMaterials(Path.Combine(tempDirectory, "materials.json"), materialNames);

		if (Directory.Exists(packageDirectory))
		{
			DeleteDirectory(packageDirectory);
		}

		MoveDirectory(tempDirectory, packageDirectory);

		long packageBytes = Directory
			.EnumerateFiles(packageDirectory, "*", SearchOption.AllDirectories)
			.Sum(path => new FileInfo(path).Length);

		return new ViewerPackageResult(
			packageDirectory,
			cells.Count,
			meshCount,
			triangleCount,
			lodCount,
			packageBytes,
			sharedBytes,
			sharedAssets.Count,
			instanceCount,
			skippedNodes,
			cellSharedNodes);
	}

	/// <summary>
	/// Terrain, sky and distant backdrop geometry stays in the cell; so do
	/// meshes that are larger than a cell, because pulling one of those into a
	/// single cell would make it pop in and out with the streaming grid. Every
	/// other object can be instanced from the shared library.
	/// </summary>
	private static bool IsCellLocal(
		GlbDrawMesh drawMesh,
		float cellSize,
		out Vector3 boundsMin,
		out Vector3 boundsMax)
	{
		boundsMin = default;
		boundsMax = default;

		bool allTerrain = true;
		bool anyPrimitive = false;
		foreach (IPrimitiveReader<MaterialBuilder> primitive in drawMesh.Mesh.Primitives)
		{
			anyPrimitive = true;
			string name = primitive.Material.Name ?? string.Empty;
			if (!string.Equals(name, "TerrainDefault", StringComparison.Ordinal))
			{
				allTerrain = false;
			}
		}

		if (anyPrimitive && allTerrain)
		{
			return true;
		}

		foreach (IPrimitiveReader<MaterialBuilder> primitive in drawMesh.Mesh.Primitives)
		{
			if (IsSkyMaterial(primitive.Material.Name))
			{
				return true;
			}
		}

		(boundsMin, boundsMax) = GetWorldBounds(drawMesh);
		Vector3 extent = boundsMax - boundsMin;
		return float.IsFinite(extent.X)
			&& float.IsFinite(extent.Z)
			&& MathF.Max(extent.X, extent.Z) > cellSize;
	}

	private static bool IsSkyMaterial(string? name)
	{
		return name != null
			&& (name.StartsWith("TRN_Sky", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("TRN_MountainDistant", StringComparison.OrdinalIgnoreCase));
	}

	private static (Vector3 Min, Vector3 Max) GetWorldBounds(GlbDrawMesh drawMesh)
	{
		Vector3 min = new(float.PositiveInfinity);
		Vector3 max = new(float.NegativeInfinity);
		foreach (IPrimitiveReader<MaterialBuilder> primitive in drawMesh.Mesh.Primitives)
		{
			foreach (IVertexBuilder vertex in primitive.Vertices)
			{
				Vector3 position = Vector3.Transform(vertex.GetGeometry().GetPosition(), drawMesh.WorldMatrix);
				min = Vector3.Min(min, position);
				max = Vector3.Max(max, position);
			}
		}

		return (min, max);
	}

	private static long AddSharedObject(
		GlbDrawMesh drawMesh,
		float cellSize,
		Vector3 boundsMin,
		Vector3 boundsMax,
		Dictionary<(int X, int Z), PackageCell> cells,
		Dictionary<(string Name, string Matrix), SharedObject> sharedObjects,
		Dictionary<string, SharedAsset> sharedAssets,
		MaterialFingerprints materialFingerprints,
		HashSet<string> materialNames,
		string assetDirectory,
		string textureDirectory)
	{
		if (!TryCreateTransform(drawMesh.WorldMatrix, out AffineTransform transform, out string reason))
		{
			Console.WriteLine($"[package] skipping '{drawMesh.Name}': {reason}");
			return 0;
		}

		List<SharedAsset> objectAssets = [];
		HashSet<string> seen = new(StringComparer.Ordinal);
		long triangles = 0;
		foreach (IPrimitiveReader<MaterialBuilder> primitive in drawMesh.Mesh.Primitives)
		{
			string materialName = primitive.Material.Name ?? string.Empty;
			materialNames.Add(materialName);

			SharedAsset? asset = GetOrCreateAsset(
				primitive,
				drawMesh.Name,
				drawMesh.Mesh,
				assetDirectory,
				textureDirectory,
				sharedAssets,
				materialFingerprints,
				out long primitiveTriangles);
			if (asset == null)
			{
				continue;
			}

			triangles += primitiveTriangles;
			if (seen.Add(asset.Id))
			{
				objectAssets.Add(asset);
			}
		}

		if (objectAssets.Count == 0)
		{
			return 0;
		}

		Vector3 center = (boundsMin + boundsMax) * 0.5f;
		if (!float.IsFinite(center.X) || !float.IsFinite(center.Z))
		{
			center = transform.Translation;
		}

		int cellX = (int)MathF.Floor(center.X / cellSize);
		int cellZ = (int)MathF.Floor(center.Z / cellSize);
		(int X, int Z) cellKey = (cellX, cellZ);
		if (!cells.TryGetValue(cellKey, out PackageCell? cell))
		{
			cell = new PackageCell(cellX, cellZ);
			cells.Add(cellKey, cell);
		}

		(string Name, string Matrix) objectKey = (StripLodSuffix(drawMesh.Name), MatrixKey(drawMesh.WorldMatrix));
		if (!sharedObjects.TryGetValue(objectKey, out SharedObject? sharedObject))
		{
			sharedObject = new SharedObject(drawMesh.Name, drawMesh.WorldMatrix, transform, center);
			sharedObjects.Add(objectKey, sharedObject);
			cell.Instances.Add(sharedObject);
			cell.Include(boundsMin, boundsMax);
		}

		if (!sharedObject.Lods.TryGetValue(drawMesh.Lod, out List<SharedAsset>? variant))
		{
			variant = [];
			sharedObject.Lods.Add(drawMesh.Lod, variant);
		}

		foreach (SharedAsset asset in objectAssets)
		{
			if (!variant.Any(existing => existing.Id == asset.Id))
			{
				variant.Add(asset);
			}
		}

		return triangles;
	}

	private static SharedAsset? GetOrCreateAsset(
		IPrimitiveReader<MaterialBuilder> primitive,
		string name,
		IMeshBuilder<MaterialBuilder> sourceMesh,
		string assetDirectory,
		string textureDirectory,
		Dictionary<string, SharedAsset> sharedAssets,
		MaterialFingerprints materialFingerprints,
		out long triangles)
	{
		triangles = CountTriangles(primitive);
		if (triangles == 0)
		{
			return null;
		}

		HashState hash = HashPrimitive(primitive, triangles, materialFingerprints);
		string id = hash.ToHex();
		if (sharedAssets.TryGetValue(id, out SharedAsset? existing))
		{
			return existing;
		}

		string path = Path.Combine(assetDirectory, id + ".gltf");
		string binPath = Path.ChangeExtension(path, ".bin");
		long bytes;
		if (File.Exists(path) && File.Exists(binPath) && new FileInfo(binPath).Length > 0)
		{
			bytes = new FileInfo(path).Length + new FileInfo(binPath).Length;
		}
		else
		{
			IMeshBuilder<MaterialBuilder>? mesh = BuildAssetMesh(primitive, name, sourceMesh);
			if (mesh == null)
			{
				triangles = 0;
				return null;
			}

			bytes = WriteAsset(id, name, mesh, assetDirectory, textureDirectory);
		}

		SharedAsset asset = new()
		{
			Id = id,
			Name = name,
			MaterialName = primitive.Material.Name ?? string.Empty,
			Triangles = triangles,
			Bytes = bytes,
		};
		sharedAssets.Add(id, asset);
		return asset;
	}

	private static long CountTriangles(IPrimitiveReader<MaterialBuilder> primitive)
	{
		long triangles = 0;
		foreach ((int _, int _, int _) in primitive.Triangles)
		{
			triangles++;
		}

		return triangles;
	}

	private static IMeshBuilder<MaterialBuilder>? BuildAssetMesh(
		IPrimitiveReader<MaterialBuilder> primitive,
		string name,
		IMeshBuilder<MaterialBuilder> sourceMesh)
	{
		// Most props are a single primitive, so the source mesh can be written
		// as-is instead of copying every triangle into a second builder. The
		// source mesh is never mutated, and SharpGLTF only reads it.
		object? only = null;
		int primitiveCount = 0;
		foreach (object candidate in sourceMesh.Primitives)
		{
			only = candidate;
			if (++primitiveCount > 1)
			{
				break;
			}
		}

		if (primitiveCount == 1 && ReferenceEquals(only, primitive))
		{
			return sourceMesh;
		}

		IReadOnlyList<IVertexBuilder> vertices = primitive.Vertices;
		if (vertices.Count == 0)
		{
			return null;
		}

		IMeshBuilder<MaterialBuilder> mesh = vertices[0].CreateCompatibleMesh<MaterialBuilder>(name);
		IPrimitiveBuilder target = mesh.UsePrimitive(primitive.Material);
		foreach ((int a, int b, int c) in primitive.Triangles)
		{
			target.AddTriangle(vertices[a], vertices[b], vertices[c]);
		}

		return mesh;
	}

	private static long WriteAsset(
		string id,
		string name,
		IMeshBuilder<MaterialBuilder> mesh,
		string assetDirectory,
		string textureDirectory)
	{
		string path = Path.Combine(assetDirectory, id + ".gltf");
		string binPath = Path.ChangeExtension(path, ".bin");
		if (File.Exists(path) && File.Exists(binPath) && new FileInfo(binPath).Length > 0)
		{
			return new FileInfo(path).Length + new FileInfo(binPath).Length;
		}

		SceneBuilder scene = new();
		scene.AddRigidMesh(mesh, new NodeBuilder(name));
		ModelRoot model = scene.ToGltf2();
		foreach (Image image in model.LogicalImages)
		{
			image.AlternateWriteFileName = AssetTextureUri(SharedTextureName(image.Content.Content.Span));
		}

		WriteSettings settings = new()
		{
			ImageWriting = ResourceWriteMode.SatelliteFile,
			MergeBuffers = true,
			JsonIndented = true,
			ImageWriteCallback = (_, _, image) =>
			{
				string fileName = SharedTextureName(image.Content.Span);
				string fullPath = Path.Combine(textureDirectory, fileName);
				if (!File.Exists(fullPath))
				{
					File.WriteAllBytes(fullPath, image.Content.ToArray());
				}

				return AssetTextureUri(fileName);
			},
		};

		// Write to a temporary basename first so a crashed export never leaves a
		// half-written content-addressed asset that a later run would trust.
		// The buffer URI is renamed alongside the moved files.
		string tempGltf = Path.Combine(assetDirectory, id + ".part.gltf");
		string tempBin = Path.ChangeExtension(tempGltf, ".bin");
		File.Delete(tempGltf);
		File.Delete(tempBin);
		model.SaveGLTF(tempGltf, settings);
		if (!File.Exists(tempBin))
		{
			throw new InvalidOperationException($"Asset {id} did not produce a buffer.");
		}

		string json = File.ReadAllText(tempGltf);
		json = json.Replace(
			Path.GetFileName(tempBin),
			Path.GetFileName(binPath),
			StringComparison.Ordinal);
		File.WriteAllText(tempGltf, json);
		File.Move(tempBin, binPath, overwrite: true);
		File.Move(tempGltf, path, overwrite: true);

		return new FileInfo(path).Length + new FileInfo(binPath).Length;
	}

	/// <summary>
	/// Stable content fingerprint for a single primitive. Material names alone
	/// are not enough (the same name can appear with different textures), and
	/// SharpGLTF's own content hash uses the process-randomized string hash, so
	/// this feeds the material parameters and image bytes explicitly.
	/// </summary>
	private static HashState HashPrimitive(
		IPrimitiveReader<MaterialBuilder> primitive,
		long triangles,
		MaterialFingerprints materialFingerprints)
	{
		HashState hash = new();
		(ulong materialHashA, ulong materialHashB) = materialFingerprints.Get(primitive.Material);
		hash.Feed(materialHashA);
		hash.Feed(materialHashB);
		hash.Feed((ulong)triangles);
		hash.Feed(primitive.VertexType.FullName ?? primitive.VertexType.Name);

		foreach (IVertexBuilder vertex in primitive.Vertices)
		{
			FeedVertex(ref hash, vertex);
		}

		foreach ((int a, int b, int c) in primitive.Triangles)
		{
			hash.Feed((long)a);
			hash.Feed((long)b);
			hash.Feed((long)c);
		}

		return hash;
	}

	private static void FeedMaterial(ref HashState hash, MaterialBuilder material)
	{
		hash.Feed(material.Name ?? string.Empty);
		hash.Feed((int)material.AlphaMode);
		hash.Feed(material.AlphaCutoff);
		hash.Feed(material.DoubleSided ? 1UL : 0UL);
		hash.Feed(material.ShaderStyle ?? string.Empty);
		hash.Feed((ulong)material.Channels.Count);

		foreach (ChannelBuilder channel in material.Channels)
		{
			hash.Feed(channel.Key.ToString());
			Vector4 parameters = channel.Parameters.CombinedVector;
			hash.Feed(parameters.X);
			hash.Feed(parameters.Y);
			hash.Feed(parameters.Z);
			hash.Feed(parameters.W);

			TextureBuilder? texture = channel.Texture;
			if (texture == null)
			{
				hash.Feed(0UL);
				continue;
			}

			hash.Feed(1UL);
			hash.Feed(texture.CoordinateSet);
			hash.Feed((int)texture.MinFilter);
			hash.Feed((int)texture.MagFilter);
			hash.Feed((int)texture.WrapS);
			hash.Feed((int)texture.WrapT);
			FeedImage(ref hash, texture.PrimaryImage);
			FeedImage(ref hash, texture.FallbackImage);
		}
	}

	private static void FeedImage(ref HashState hash, ImageBuilder? image)
	{
		if (image == null)
		{
			hash.Feed(0UL);
			return;
		}

		ReadOnlyMemory<byte> content = image.Content.Content;
		hash.Feed((ulong)content.Length);
		foreach (byte value in content.Span)
		{
			hash.Feed(value);
		}
	}

	private static void FeedVertex(ref HashState hash, IVertexBuilder vertex)
	{
		IVertexGeometry geometry = vertex.GetGeometry();
		Vector3 position = geometry.GetPosition();
		hash.Feed(position.X);
		hash.Feed(position.Y);
		hash.Feed(position.Z);

		bool hasNormal = geometry.TryGetNormal(out Vector3 normal);
		hash.Feed(hasNormal ? 1UL : 0UL);
		if (hasNormal)
		{
			hash.Feed(normal.X);
			hash.Feed(normal.Y);
			hash.Feed(normal.Z);
		}

		bool hasTangent = geometry.TryGetTangent(out Vector4 tangent);
		hash.Feed(hasTangent ? 1UL : 0UL);
		if (hasTangent)
		{
			hash.Feed(tangent.X);
			hash.Feed(tangent.Y);
			hash.Feed(tangent.Z);
			hash.Feed(tangent.W);
		}

		IVertexMaterial material = vertex.GetMaterial();
		hash.Feed((ulong)material.MaxColors);
		for (int i = 0; i < material.MaxColors; i++)
		{
			Vector4 color = material.GetColor(i);
			hash.Feed(color.X);
			hash.Feed(color.Y);
			hash.Feed(color.Z);
			hash.Feed(color.W);
		}

		hash.Feed((ulong)material.MaxTextCoords);
		for (int i = 0; i < material.MaxTextCoords; i++)
		{
			Vector2 texCoord = material.GetTexCoord(i);
			hash.Feed(texCoord.X);
			hash.Feed(texCoord.Y);
		}
	}

	private static long PartitionMesh(
		GlbDrawMesh drawMesh,
		long drawIndex,
		float cellSize,
		Dictionary<(int X, int Z), PackageCell> cells,
		HashSet<string> materialNames,
		ref Vector3 focusMin,
		ref Vector3 focusMax)
	{
		long triangles = 0;
		foreach (IPrimitiveReader<MaterialBuilder> primitive in drawMesh.Mesh.Primitives)
		{
			MaterialBuilder material = primitive.Material;
			materialNames.Add(material.Name);
			bool isTerrain = string.Equals(material.Name, "TerrainDefault", StringComparison.Ordinal);

			IReadOnlyList<IVertexBuilder> vertices = primitive.Vertices;
			foreach ((int a, int b, int c) in primitive.Triangles)
			{
				IVertexBuilder va = vertices[a];
				IVertexBuilder vb = vertices[b];
				IVertexBuilder vc = vertices[c];

				Vector3 pa = Vector3.Transform(va.GetGeometry().GetPosition(), drawMesh.WorldMatrix);
				Vector3 pb = Vector3.Transform(vb.GetGeometry().GetPosition(), drawMesh.WorldMatrix);
				Vector3 pc = Vector3.Transform(vc.GetGeometry().GetPosition(), drawMesh.WorldMatrix);
				Vector3 centroid = (pa + pb + pc) / 3f;

				if (isTerrain)
				{
					focusMin = Vector3.Min(focusMin, Vector3.Min(pa, Vector3.Min(pb, pc)));
					focusMax = Vector3.Max(focusMax, Vector3.Max(pa, Vector3.Max(pb, pc)));
				}

				int cellX = (int)MathF.Floor(centroid.X / cellSize);
				int cellZ = (int)MathF.Floor(centroid.Z / cellSize);
				(int X, int Z) key = (cellX, cellZ);
				if (!cells.TryGetValue(key, out PackageCell? cell))
				{
					cell = new PackageCell(cellX, cellZ);
					cells.Add(key, cell);
				}

				cell.Include(pa, pb, pc);
				CellLod cellLod = cell.GetOrCreateLod(drawMesh.Lod);
				MeshEntry entry = cellLod.GetOrCreateEntry(drawIndex, primitive.VertexType, drawMesh);
				entry.AddTriangle(va, vb, vc, material);
				cellLod.Triangles++;
				triangles++;
			}
		}

		return triangles;
	}

	private static int WriteCellLod(
		string path,
		PackageCell cell,
		int lod,
		string textureDirectory,
		out long triangles,
		out int sharedNodes)
	{
		SceneBuilder scene = new();
		Dictionary<(ulong, ulong, Type), IMeshBuilder<MaterialBuilder>> meshes = [];
		int skipped = 0;
		triangles = 0;
		sharedNodes = 0;
		foreach (MeshEntry entry in cell.SelectEntries(lod))
		{
			if (!TryCreateTransform(entry.WorldMatrix, out AffineTransform transform, out string reason))
			{
				Console.WriteLine($"[package] skipping '{entry.Name}': {reason}");
				skipped++;
				continue;
			}

			if (entry.Mesh is not IMeshBuilder<MaterialBuilder> geometry)
			{
				Console.WriteLine($"[package] skipping '{entry.Name}': no geometry");
				skipped++;
				continue;
			}

			// Duplicated large meshes export as separate draw meshes with
			// identical local geometry, differing only in their world matrix.
			// One glTF mesh shared by every node keeps the buffer small, which
			// is exactly how SharpGLTF dedupes MeshBuilder references.
			(ulong, ulong, Type) key = (entry.GeometryHashA, entry.GeometryHashB, entry.VertexType);
			if (meshes.TryGetValue(key, out IMeshBuilder<MaterialBuilder>? shared))
			{
				geometry = shared;
				sharedNodes++;
			}
			else
			{
				meshes.Add(key, geometry);
			}

			NodeBuilder node = new(entry.Name)
			{
				LocalTransform = transform,
			};
			scene.AddRigidMesh(geometry, node);
			triangles += entry.Triangles;
		}

		ModelRoot model = scene.ToGltf2();
		foreach (Image image in model.LogicalImages)
		{
			image.AlternateWriteFileName = CellTextureUri(SharedTextureName(image.Content.Content.Span));
		}

		WriteSettings settings = new()
		{
			ImageWriting = ResourceWriteMode.SatelliteFile,
			MergeBuffers = true,
			JsonIndented = true,
			ImageWriteCallback = (_, _, image) =>
			{
				string name = SharedTextureName(image.Content.Span);
				string fullPath = Path.Combine(textureDirectory, name);
				if (!File.Exists(fullPath))
				{
					File.WriteAllBytes(fullPath, image.Content.ToArray());
				}

				return CellTextureUri(name);
			},
		};

		model.SaveGLTF(path, settings);
		return skipped;
	}

	/// <summary>
	/// glTF node transforms must be representable as translation/rotation/scale.
	/// A world matrix can be skewed when non-uniform scales are nested under
	/// rotated parents; in that case the same matrix cannot be stored verbatim
	/// (SharpGLTF rejects it), so the basis is re-orthonormalised. Only truly
	/// degenerate transforms (NaN or zero scale) are skipped.
	/// </summary>
	private static bool TryCreateTransform(Matrix4x4 matrix, out AffineTransform transform, out string reason)
	{
		transform = default;

		if (!IsFinite(matrix.M11) || !IsFinite(matrix.M12) || !IsFinite(matrix.M13) || !IsFinite(matrix.M14)
			|| !IsFinite(matrix.M21) || !IsFinite(matrix.M22) || !IsFinite(matrix.M23) || !IsFinite(matrix.M24)
			|| !IsFinite(matrix.M31) || !IsFinite(matrix.M32) || !IsFinite(matrix.M33) || !IsFinite(matrix.M34)
			|| !IsFinite(matrix.M41) || !IsFinite(matrix.M42) || !IsFinite(matrix.M43) || !IsFinite(matrix.M44))
		{
			reason = "world matrix contains NaN or infinity";
			return false;
		}

		if (Matrix4x4.Decompose(matrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation))
		{
			// SharpGLTF's SRT constructor is (scale, rotation, translation).
			transform = new AffineTransform(scale, rotation, translation);
			reason = string.Empty;
			return true;
		}

		Vector3 x = new(matrix.M11, matrix.M12, matrix.M13);
		Vector3 y = new(matrix.M21, matrix.M22, matrix.M23);
		Vector3 z = new(matrix.M31, matrix.M32, matrix.M33);
		float scaleX = x.Length();
		float scaleY = y.Length();
		float scaleZ = z.Length();
		if (scaleX <= 1e-6f || scaleY <= 1e-6f || scaleZ <= 1e-6f)
		{
			reason = $"world matrix has zero scale ({scaleX}, {scaleY}, {scaleZ})";
			return false;
		}

		// Mirroring has to live in the scale, but the rotation basis must stay
		// proper (determinant +1), so flip the X axis rather than only the scale.
		float xSign = matrix.GetDeterminant() < 0f ? -1f : 1f;
		Matrix4x4 rotationMatrix = new(
			xSign * x.X / scaleX, xSign * x.Y / scaleX, xSign * x.Z / scaleX, 0f,
			y.X / scaleY, y.Y / scaleY, y.Z / scaleY, 0f,
			z.X / scaleZ, z.Y / scaleZ, z.Z / scaleZ, 0f,
			0f, 0f, 0f, 1f);

		transform = new AffineTransform(
			new Vector3(xSign * scaleX, scaleY, scaleZ),
			Quaternion.CreateFromRotationMatrix(rotationMatrix),
			new Vector3(matrix.M41, matrix.M42, matrix.M43));
		reason = string.Empty;
		return true;
	}

	private static bool IsFinite(float value) => float.IsFinite(value);

	private static void DeleteDirectory(string path)
	{
		if (!Directory.Exists(path))
		{
			return;
		}

		for (int attempt = 0; ; attempt++)
		{
			try
			{
				Directory.Delete(path, true);
				return;
			}
			catch (IOException) when (attempt < 4)
			{
				Thread.Sleep(200);
			}
			catch (UnauthorizedAccessException) when (attempt < 4)
			{
				Thread.Sleep(200);
			}
		}
	}

	private static void MoveDirectory(string source, string destination)
	{
		for (int attempt = 0; ; attempt++)
		{
			try
			{
				Directory.Move(source, destination);
				return;
			}
			catch (IOException) when (attempt < 4)
			{
				Thread.Sleep(200);
			}
			catch (UnauthorizedAccessException) when (attempt < 4)
			{
				Thread.Sleep(200);
			}
		}
	}

	private static string SharedTextureName(ReadOnlySpan<byte> content)
	{
		byte[] hash = SHA256.HashData(content);
		return Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant() + ".png";
	}

	private static string CellTextureUri(string fileName) => $"../../../{SharedFolder}/{TexturesFolder}/{fileName}";

	private static string AssetTextureUri(string fileName) => $"../{TexturesFolder}/{fileName}";

	private static string StripLodSuffix(string name)
	{
		int marker = name.LastIndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
		if (marker < 0 || marker + 4 >= name.Length || !char.IsAsciiDigit(name[marker + 4]))
		{
			return name;
		}

		return name[..marker];
	}

	// LOD variants live under the same transform, but comparing float bits is
	// too strict; rounding pairs them while still separating real duplicates.
	private static string MatrixKey(Matrix4x4 matrix) => FormattableString.Invariant(
		$"{matrix.M11:F4},{matrix.M12:F4},{matrix.M13:F4},{matrix.M21:F4},{matrix.M22:F4},{matrix.M23:F4},{matrix.M31:F4},{matrix.M32:F4},{matrix.M33:F4},{matrix.M41:F3},{matrix.M42:F3},{matrix.M43:F3}");

	private static void WriteManifest(
		string path,
		string packageName,
		float cellSize,
		bool allLods,
		int lodCount,
		Vector3 min,
		Vector3 max,
		Vector3 focusMin,
		Vector3 focusMax,
		IEnumerable<SharedAsset> assets,
		IEnumerable<PackageCell> cells)
	{
		var manifest = new
		{
			formatVersion = 2,
			level = Path.GetFileNameWithoutExtension(packageName),
			cellSize,
			allLods,
			lodCount,
			min = ToArray(min),
			max = ToArray(max),
			focusMin = ToArray(focusMin),
			focusMax = ToArray(focusMax),
			shared = $"../{SharedFolder}",
			assets = assets
				.OrderBy(asset => asset.Id, StringComparer.Ordinal)
				.Select(asset => new
				{
					id = asset.Id,
					file = $"{AssetsFolder}/{asset.Id}.gltf",
					material = asset.MaterialName,
					triangles = asset.Triangles,
					bytes = asset.Bytes,
				}),
			cells = cells
				.OrderBy(cell => cell.X)
				.ThenBy(cell => cell.Z)
				.Select(cell => new
				{
					id = cell.Id,
					min = ToArray(cell.Min),
					max = ToArray(cell.Max),
					center = ToArray((cell.Min + cell.Max) * 0.5f),
					instances = cell.Instances.Count > 0 ? $"{CellsFolder}/{cell.Id}/instances.json" : null,
					instanceCount = cell.Instances.Count,
					lods = cell.Lods
						.Keys
						.OrderBy(lod => lod)
						.Select(lod => new
						{
							level = lod,
							file = $"{CellsFolder}/{cell.Id}/lod{lod}.gltf",
							triangles = cell.WrittenTriangles[lod],
							bytes = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, CellsFolder, cell.Id, $"lod{lod}.gltf")).Length,
						}),
				}),
		};

		File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static void WriteInstances(string path, PackageCell cell)
	{
		var data = new
		{
			cell = cell.Id,
			instances = cell.Instances.Select(item => new
			{
				name = item.Name,
				t = ToArray(item.Transform.Translation),
				r = new[]
				{
					item.Transform.Rotation.X,
					item.Transform.Rotation.Y,
					item.Transform.Rotation.Z,
					item.Transform.Rotation.W,
				},
				s = ToArray(item.Transform.Scale),
				lods = item.Lods
					.OrderBy(pair => pair.Key)
					.Select(pair => new
					{
						level = pair.Key,
						assets = pair.Value.Select(asset => asset.Id).ToArray(),
						triangles = pair.Value.Sum(asset => asset.Triangles),
					}),
			}),
		};

		File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static void WriteMaterials(string path, IEnumerable<string> materialNames)
	{
		var materials = new { materials = materialNames.OrderBy(name => name, StringComparer.Ordinal) };
		File.WriteAllText(path, JsonSerializer.Serialize(materials, new JsonSerializerOptions { WriteIndented = true }));
	}

	private static float[] ToArray(Vector3 value) => [value.X, value.Y, value.Z];

	private sealed class PackageCell(int x, int z)
	{
		public int X { get; } = x;

		public int Z { get; } = z;

		public string Id { get; } = string.Format(CultureInfo.InvariantCulture, "x{0}_z{1}", x, z);

		public Vector3 Min { get; private set; } = new(float.PositiveInfinity);

		public Vector3 Max { get; private set; } = new(float.NegativeInfinity);

		public Dictionary<int, CellLod> Lods { get; } = [];

		public Dictionary<int, long> WrittenTriangles { get; } = [];

		public List<SharedObject> Instances { get; } = [];

		public void Include(Vector3 a, Vector3 b, Vector3 c)
		{
			Min = Vector3.Min(Min, Vector3.Min(a, Vector3.Min(b, c)));
			Max = Vector3.Max(Max, Vector3.Max(a, Vector3.Max(b, c)));
		}

		public void Include(Vector3 min, Vector3 max)
		{
			Min = Vector3.Min(Min, min);
			Max = Vector3.Max(Max, max);
		}

		/// <summary>
		/// Entries for one written LOD file. Authored LOD files only contain the
		/// objects that actually have that LOD, so cell-local terrain and
		/// LOD0-only geometry would vanish when the viewer switches a cell to a
		/// coarser level. Every object therefore falls back to its highest
		/// authored LOD at or below the requested level (or its lowest authored
		/// LOD when it has none below).
		/// </summary>
		public IEnumerable<MeshEntry> SelectEntries(int lod)
		{
			Dictionary<(string Name, string Matrix), SortedDictionary<int, List<MeshEntry>>> objects = [];
			foreach ((int authoredLod, CellLod cellLod) in Lods)
			{
				foreach (MeshEntry entry in cellLod.Entries)
				{
					(string, string) key = (StripLodSuffix(entry.Name), MatrixKey(entry.WorldMatrix));
					if (!objects.TryGetValue(key, out SortedDictionary<int, List<MeshEntry>>? variants))
					{
						variants = [];
						objects.Add(key, variants);
					}

					if (!variants.TryGetValue(authoredLod, out List<MeshEntry>? list))
					{
						list = [];
						variants.Add(authoredLod, list);
					}

					list.Add(entry);
				}
			}

			foreach (SortedDictionary<int, List<MeshEntry>> variants in objects.Values)
			{
				int selected = -1;
				foreach (int authoredLod in variants.Keys)
				{
					if (authoredLod <= lod)
					{
						selected = authoredLod;
					}
				}

				if (selected < 0)
				{
					selected = variants.Keys.Min();
				}

				foreach (MeshEntry entry in variants[selected])
				{
					yield return entry;
				}
			}
		}

		public CellLod GetOrCreateLod(int lod)
		{
			if (!Lods.TryGetValue(lod, out CellLod? cellLod))
			{
				cellLod = new CellLod();
				Lods.Add(lod, cellLod);
			}

			return cellLod;
		}
	}

	private sealed class CellLod
	{
		public List<MeshEntry> Entries { get; } = [];

		public Dictionary<(long Draw, Type VertexType), MeshEntry> EntriesByDraw { get; } = [];

		public long Triangles { get; set; }

		public MeshEntry GetOrCreateEntry(long drawIndex, Type vertexType, GlbDrawMesh drawMesh)
		{
			(long, Type) key = (drawIndex, vertexType);
			if (!EntriesByDraw.TryGetValue(key, out MeshEntry? entry))
			{
				entry = new MeshEntry(drawMesh.Name, drawMesh.WorldMatrix, vertexType);
				EntriesByDraw.Add(key, entry);
				Entries.Add(entry);
			}

			return entry;
		}
	}

	private sealed class MeshEntry
	{
		private const ulong HashSeedA = 14695981039346656037UL;
		private const ulong HashSeedB = 0x9E3779B97F4A7C15UL;
		private const ulong HashPrimeA = 1099511628211UL;
		private const ulong HashPrimeB = 0xC2B2AE3D27D4EB4FUL;

		public MeshEntry(string name, Matrix4x4 worldMatrix, Type vertexType)
		{
			Name = name;
			WorldMatrix = worldMatrix;
			VertexType = vertexType;
		}

		public string Name { get; }

		public Matrix4x4 WorldMatrix { get; }

		public Type VertexType { get; }

		public long Triangles { get; private set; }

		/// <summary>
		/// Content hash of this entry's local geometry and material layout. Two
		/// entries with equal hashes produce the same glTF mesh, so the cell can
		/// reference one mesh from both nodes instead of storing it twice.
		/// </summary>
		public ulong GeometryHashA { get; private set; } = HashSeedA;

		public ulong GeometryHashB { get; private set; } = HashSeedB;

		public IMeshBuilder<MaterialBuilder>? Mesh { get; private set; }

		public Dictionary<MaterialBuilder, IPrimitiveBuilder> Primitives { get; } = [];

		public void AddTriangle(IVertexBuilder a, IVertexBuilder b, IVertexBuilder c, MaterialBuilder material)
		{
			Feed(material.Name ?? string.Empty);
			Feed(a);
			Feed(b);
			Feed(c);
			GetPrimitive(material, a).AddTriangle(a, b, c);
			Triangles++;
		}

		public IPrimitiveBuilder GetPrimitive(MaterialBuilder material, IVertexBuilder vertex)
		{
			if (!Primitives.TryGetValue(material, out IPrimitiveBuilder? primitive))
			{
				IMeshBuilder<MaterialBuilder> mesh = Mesh ??= vertex.CreateCompatibleMesh<MaterialBuilder>(Name);
				primitive = mesh.UsePrimitive(material);
				Primitives.Add(material, primitive);
			}

			return primitive;
		}

		private void Feed(IVertexBuilder vertex)
		{
			IVertexGeometry geometry = vertex.GetGeometry();
			Vector3 position = geometry.GetPosition();
			Feed(position.X);
			Feed(position.Y);
			Feed(position.Z);

			bool hasNormal = geometry.TryGetNormal(out Vector3 normal);
			Feed(hasNormal ? 1UL : 0UL);
			if (hasNormal)
			{
				Feed(normal.X);
				Feed(normal.Y);
				Feed(normal.Z);
			}

			bool hasTangent = geometry.TryGetTangent(out Vector4 tangent);
			Feed(hasTangent ? 1UL : 0UL);
			if (hasTangent)
			{
				Feed(tangent.X);
				Feed(tangent.Y);
				Feed(tangent.Z);
				Feed(tangent.W);
			}

			IVertexMaterial material = vertex.GetMaterial();
			Feed((ulong)material.MaxColors);
			for (int i = 0; i < material.MaxColors; i++)
			{
				Vector4 color = material.GetColor(i);
				Feed(color.X);
				Feed(color.Y);
				Feed(color.Z);
				Feed(color.W);
			}

			Feed((ulong)material.MaxTextCoords);
			for (int i = 0; i < material.MaxTextCoords; i++)
			{
				Vector2 texCoord = material.GetTexCoord(i);
				Feed(texCoord.X);
				Feed(texCoord.Y);
			}
		}

		private void Feed(string value)
		{
			Feed((ulong)value.Length);
			foreach (char character in value)
			{
				Feed(character);
			}
		}

		private void Feed(float value) => Feed((ulong)BitConverter.SingleToUInt32Bits(value));

		private void Feed(ulong value)
		{
			// The projects enable CheckForOverflowUnderflow, so hashing must be
			// explicitly unchecked or every multiply beyond 2^64 throws.
			unchecked
			{
				GeometryHashA = (GeometryHashA ^ value) * HashPrimeA;
				GeometryHashB = (GeometryHashB ^ value) * HashPrimeB;
			}
		}
	}

	private sealed class SharedObject(
		string name,
		Matrix4x4 worldMatrix,
		AffineTransform transform,
		Vector3 center)
	{
		public string Name { get; } = name;

		public Matrix4x4 WorldMatrix { get; } = worldMatrix;

		public AffineTransform Transform { get; } = transform;

		public Vector3 Center { get; } = center;

		public Dictionary<int, List<SharedAsset>> Lods { get; } = [];
	}

	private sealed class SharedAsset
	{
		public required string Id { get; init; }

		public required string Name { get; init; }

		public required string MaterialName { get; init; }

		public long Triangles { get; init; }

		public long Bytes { get; init; }
	}

	/// <summary>
	/// Caches the expensive material fingerprint (including image bytes) once
	/// per MaterialBuilder. Materials are shared by every draw mesh in a level,
	/// so hashing them per primitive would re-read the same texture hundreds of
	/// times.
	/// </summary>
	private sealed class MaterialFingerprints
	{
		private readonly Dictionary<MaterialBuilder, (ulong A, ulong B)> cache =
			new(ReferenceEqualityComparer.Instance);

		public (ulong A, ulong B) Get(MaterialBuilder material)
		{
			if (cache.TryGetValue(material, out (ulong A, ulong B) value))
			{
				return value;
			}

			HashState hash = new();
			FeedMaterial(ref hash, material);
			value = (hash.A, hash.B);
			cache.Add(material, value);
			return value;
		}
	}

	private struct HashState
	{
		private const ulong SeedA = 14695981039346656037UL;
		private const ulong SeedB = 0x9E3779B97F4A7C15UL;
		private const ulong PrimeA = 1099511628211UL;
		private const ulong PrimeB = 0xC2B2AE3D27D4EB4FUL;

		private ulong _a = SeedA;
		private ulong _b = SeedB;

		public HashState()
		{
		}

		public ulong A => _a;

		public ulong B => _b;

		public void Feed(ulong value)
		{
			unchecked
			{
				_a = (_a ^ value) * PrimeA;
				_b = (_b ^ value) * PrimeB;
			}
		}

		public void Feed(long value) => Feed((ulong)value);

		public void Feed(int value) => Feed((ulong)(uint)value);

		public void Feed(float value) => Feed((ulong)BitConverter.SingleToUInt32Bits(value));

		public void Feed(string value)
		{
			Feed((ulong)value.Length);
			foreach (char character in value)
			{
				Feed(character);
			}
		}

		public string ToHex() =>
			_a.ToString("x16", CultureInfo.InvariantCulture)
			+ _b.ToString("x16", CultureInfo.InvariantCulture);
	}
}
