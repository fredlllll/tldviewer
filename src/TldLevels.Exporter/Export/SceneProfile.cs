using AssetRipper.Numerics;
using AssetRipper.Processing.Prefabs;
using AssetRipper.SourceGenerated.Classes.ClassID_25;
using AssetRipper.SourceGenerated.Classes.ClassID_33;
using AssetRipper.SourceGenerated.Classes.ClassID_4;
using AssetRipper.SourceGenerated.Extensions;
using System.Numerics;

namespace TldLevels.Exporter.Export;

/// <summary>
/// Cheap summary for one scene, used to tell a complete art scene from a design
/// overlay.
/// <para>
/// Wintermute splits many interiors into two serialized files: an art scene
/// with the room shell, and a design scene with only the loose props. The
/// authoring convention marks that in the hierarchy: a design scene groups its
/// props under a top-level <c>Design</c> and has no <c>Art</c> group, while an
/// art scene carries <c>Art</c> (usually next to its own <c>Design</c> items).
/// Exported alone, a design scene is items floating in a void.
/// </para>
/// <para>
/// Older levels predate those group names and wrap everything in a neutral root
/// such as <c>Root</c>; for those the maximum mesh extent still separates loose
/// props from room shells.
/// </para>
/// </summary>
public sealed record SceneProfile(
	string SceneName,
	int MeshCount,
	float MaxHorizontalExtent,
	bool IsOverlay)
{
	/// <summary>
	/// Fallback for scenes without <c>Art</c>/<c>Design</c> groups: loose props
	/// and story items are smaller than this, room shells and vehicles larger.
	/// Measured maximum horizontal extents: overlays 0.1-12.6 m (3, 5, 7, 11,
	/// 13, 17, 21, 25, 31); real scenes 20.2 m and up (29 basement, 23 cinematic
	/// dam, 28 basement variant). The threshold sits in that gap so a long cable
	/// or beam does not stop a design scene from merging.
	/// </summary>
	public const float OverlayMaxExtent = 15.0f;

	public static SceneProfile Analyze(SceneHierarchyObject scene)
	{
		float maxExtent = 0.0f;
		int meshCount = 0;

		bool hasDesignRoot = false;
		bool hasArtRoot = false;
		foreach (var root in scene.GetRoots())
		{
			string name = root.Name.String;
			if (string.Equals(name, "Design", StringComparison.OrdinalIgnoreCase))
			{
				hasDesignRoot = true;
			}
			else if (string.Equals(name, "Art", StringComparison.OrdinalIgnoreCase))
			{
				hasArtRoot = true;
			}
		}

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
				// Unknown bounds only matter to the extent fallback; ignore the
				// mesh instead of inventing a size for it.
				continue;
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

		// Group names win when the scene uses them; a scene with Art is never an
		// overlay even if its Design items are tiny.
		bool overlay = meshCount > 0
			&& (hasDesignRoot || hasArtRoot
				? hasDesignRoot && !hasArtRoot
				: maxExtent <= OverlayMaxExtent);
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
