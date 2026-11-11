using AssetRipper.Numerics;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using System.Numerics;

namespace TldLevels.Exporter.Export;

/// <summary>
/// Cheap geometry summary for one scene, used to tell a complete art scene from
/// a design overlay.
/// <para>
/// Wintermute splits many interiors into two serialized files: an art scene
/// with the room shell, and a design scene with only the loose props. Design
/// scenes have no mesh larger than a few metres, so their maximum horizontal
/// extent is a reliable discriminator. Exported alone they look like items
/// floating in a void.
/// </para>
/// </summary>
public sealed record SceneProfile(
	string SceneName,
	int MeshCount,
	float MaxHorizontalExtent,
	bool IsOverlay)
{
	/// <summary>
	/// Props are smaller than this; walls, floors, terrain and vehicles are
	/// larger. Level 23 (a cinematic dam set with 9.8 m beams) and level 29
	/// (a complete basement variant) must stay above it.
	/// </summary>
	public const float OverlayMaxExtent = 6.0f;

	public static SceneProfile Analyze(SceneHierarchyObject scene)
	{
		float maxExtent = 0.0f;
		int meshCount = 0;

		foreach (var gameObject in scene.GameObjects)
		{
			if (!gameObject.TryGetComponent<IMeshFilter>(out var meshFilter)
				|| !meshFilter.TryGetMesh(out var mesh)
				|| !mesh.IsSet()
				|| !gameObject.TryGetComponent<IRenderer>(out _))
			{
				continue;
			}

			meshCount++;

			Vector3 center = mesh.LocalAABB.Center.CastToStruct();
			Vector3 extent = mesh.LocalAABB.Extent.CastToStruct();
			if (!IsFinite(center) || !IsFinite(extent))
			{
				// Unknown bounds: err on the side of treating this as a real
				// art scene so we never hide geometry behind a bad merge.
				return new SceneProfile(scene.Name.String, meshCount, float.PositiveInfinity, false);
			}

			Matrix4x4 global = GlobalMatrix(gameObject.GetTransform());
			float minX = float.PositiveInfinity;
			float maxX = float.NegativeInfinity;
			float minZ = float.PositiveInfinity;
			float maxZ = float.NegativeInfinity;

			for (int corner = 0; corner < 8; corner++)
			{
				Vector3 local = new(
					center.X + (((corner & 1) == 0) ? -extent.X : extent.X),
					center.Y + (((corner & 2) == 0) ? -extent.Y : extent.Y),
					center.Z + (((corner & 4) == 0) ? -extent.Z : extent.Z));
				Vector3 world = Vector3.Transform(local, global);
				minX = MathF.Min(minX, world.X);
				maxX = MathF.Max(maxX, world.X);
				minZ = MathF.Min(minZ, world.Z);
				maxZ = MathF.Max(maxZ, world.Z);
			}

			// Unity is Y-up, so X/Z are the ground plane.
			maxExtent = MathF.Max(maxExtent, MathF.Max(maxX - minX, maxZ - minZ));
		}

		bool overlay = meshCount > 0 && maxExtent <= OverlayMaxExtent;
		return new SceneProfile(scene.Name.String, meshCount, maxExtent, overlay);
	}

	private static Matrix4x4 GlobalMatrix(ITransform transform)
	{
		Matrix4x4 global = Matrix4x4.Identity;
		for (ITransform? current = transform; current is not null; current = current.Father_C4P)
		{
			// Matches GlbLevelBuilder: global = local * parentGlobal.
			global *= current.ToTransformation().Matrix;
		}

		return global;
	}

	private static bool IsFinite(Vector3 value) =>
		float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
