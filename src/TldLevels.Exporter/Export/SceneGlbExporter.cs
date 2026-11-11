using AssetRipper.Assets;
using AssetRipper.Export.Modules.Models;
using AssetRipper.IO.Files;
using SharpGLTF.Scenes;

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
		out int terrainCount)
	{
		List<IUnityObjectBase> assetList = [.. assets];
		SceneBuilder sceneBuilder = GlbLevelBuilder.Build(assetList, isScene: true);
		terrainCount = TerrainBuilder.AddTerrains(sceneBuilder, assetList);

		using Stream stream = fileSystem.File.Create(path);
		return GlbWriter.TryWrite(sceneBuilder, stream, out _);
	}
}
