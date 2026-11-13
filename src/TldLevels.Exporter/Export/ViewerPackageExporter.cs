using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using SharpGLTF.Geometry;
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
	int SkippedNodes);

/// <summary>
/// Writes a streamed viewer package from the source scene in a single pass.
/// <para>
/// Geometry is assigned to a fixed world-space X/Z grid by triangle centroid, so
/// large meshes are actually cut instead of being copied into every overlapping
/// cell. Every level shares one <c>textures/</c> folder; cells only contain their
/// own <c>.gltf</c> and <c>.bin</c> files.
/// </para>
/// </summary>
public static class ViewerPackageExporter
{
	private const string TextureFolder = "textures";
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
		string textureDirectory = Path.Combine(tempDirectory, TextureFolder);
		Directory.CreateDirectory(textureDirectory);

		Dictionary<(int X, int Z), PackageCell> cells = [];
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
			triangleCount += PartitionMesh(
				drawMesh,
				drawIndex++,
				cellSize,
				cells,
				materialNames,
				ref focusMin,
				ref focusMax);
		}

		Dictionary<int, long> bytesByLod = [];
		int skippedNodes = 0;
		foreach (PackageCell cell in cells.Values)
		{
			string cellDirectory = Path.Combine(tempDirectory, CellsFolder, cell.Id);
			Directory.CreateDirectory(cellDirectory);

			foreach ((int lod, CellLod cellLod) in cell.Lods.OrderBy(pair => pair.Key))
			{
				string fileName = $"lod{lod}.gltf";
				string path = Path.Combine(cellDirectory, fileName);
				skippedNodes += WriteCellLod(path, cellLod, textureDirectory);
			}
		}

		if (skippedNodes > 0)
		{
			Console.WriteLine($"[package] skipped {skippedNodes} node(s) with an invalid world matrix");
		}

		int lodCount = cells.Values.SelectMany(cell => cell.Lods.Keys).DefaultIfEmpty(0).Max() + 1;
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
			skippedNodes);
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
				entry.GetPrimitive(material, va).AddTriangle(va, vb, vc);
				cellLod.Triangles++;
				triangles++;
			}
		}

		return triangles;
	}

	private static int WriteCellLod(string path, CellLod cellLod, string textureDirectory)
	{
		SceneBuilder scene = new();
		int skipped = 0;
		foreach (MeshEntry entry in cellLod.Entries)
		{
			if (!TryCreateTransform(entry.WorldMatrix, out AffineTransform transform, out string reason))
			{
				Console.WriteLine($"[package] skipping '{entry.Name}': {reason}");
				skipped++;
				continue;
			}

			NodeBuilder node = new(entry.Name)
			{
				LocalTransform = transform,
			};
			scene.AddRigidMesh(entry.Mesh, node);
		}

		ModelRoot model = scene.ToGltf2();
		foreach (Image image in model.LogicalImages)
		{
			image.AlternateWriteFileName = TextureUri(SharedTextureName(image.Content.Content.Span));
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

				return TextureUri(name);
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
			transform = new AffineTransform(translation, rotation, scale);
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

		Matrix4x4 rotationMatrix = new(
			x.X / scaleX, x.Y / scaleX, x.Z / scaleX, 0f,
			y.X / scaleY, y.Y / scaleY, y.Z / scaleY, 0f,
			z.X / scaleZ, z.Y / scaleZ, z.Z / scaleZ, 0f,
			0f, 0f, 0f, 1f);
		if (matrix.GetDeterminant() < 0f)
		{
			scaleX = -scaleX;
		}

		transform = new AffineTransform(
			new Vector3(matrix.M41, matrix.M42, matrix.M43),
			Quaternion.CreateFromRotationMatrix(rotationMatrix),
			new Vector3(scaleX, scaleY, scaleZ));
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

	private static string TextureUri(string fileName) => $"../../{TextureFolder}/{fileName}";

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
		IEnumerable<PackageCell> cells)
	{
		var manifest = new
		{
			formatVersion = 1,
			level = Path.GetFileNameWithoutExtension(packageName),
			cellSize,
			allLods,
			lodCount,
			min = ToArray(min),
			max = ToArray(max),
			focusMin = ToArray(focusMin),
			focusMax = ToArray(focusMax),
			cells = cells
				.OrderBy(cell => cell.X)
				.ThenBy(cell => cell.Z)
				.Select(cell => new
				{
					id = cell.Id,
					min = ToArray(cell.Min),
					max = ToArray(cell.Max),
					center = ToArray((cell.Min + cell.Max) * 0.5f),
					lods = cell.Lods
						.OrderBy(pair => pair.Key)
						.Select(pair => new
						{
							level = pair.Key,
							file = $"{CellsFolder}/{cell.Id}/lod{pair.Key}.gltf",
							triangles = pair.Value.Triangles,
							bytes = new FileInfo(Path.Combine(Path.GetDirectoryName(path)!, CellsFolder, cell.Id, $"lod{pair.Key}.gltf")).Length,
						}),
				}),
		};

		File.WriteAllText(path, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
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

		public void Include(Vector3 a, Vector3 b, Vector3 c)
		{
			Min = Vector3.Min(Min, Vector3.Min(a, Vector3.Min(b, c)));
			Max = Vector3.Max(Max, Vector3.Max(a, Vector3.Max(b, c)));
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
		public MeshEntry(string name, Matrix4x4 worldMatrix, Type vertexType)
		{
			Name = name;
			WorldMatrix = worldMatrix;
			VertexType = vertexType;
		}

		public string Name { get; }

		public Matrix4x4 WorldMatrix { get; }

		public Type VertexType { get; }

		public IMeshBuilder<MaterialBuilder>? Mesh { get; private set; }

		public Dictionary<MaterialBuilder, IPrimitiveBuilder> Primitives { get; } = [];

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
	}
}
