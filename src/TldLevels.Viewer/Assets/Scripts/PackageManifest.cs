using System;
using System.IO;
using UnityEngine;

namespace TldLevels.Viewer
{
    /// <summary>
    /// On-disk description of a streamed level package written by the exporter
    /// The exporter writes this file inside every <c>levelN.viewer</c> package.
    /// The manifest is the only source of truth for
    /// which LOD files exist; the viewer never probes the file system for
    /// optional LODs.
    /// </summary>
    [Serializable]
    public sealed class PackageManifest
    {
        public int formatVersion;
        public string level;
        public float cellSize;
        public bool allLods;
        public int lodCount;
        public float[] min;
        public float[] max;
        public float[] focusMin;
        public float[] focusMax;

        /// <summary>
        /// Path to the shared asset root, relative to the package directory.
        /// Version 1 packages leave this null and keep their data self-contained.
        /// </summary>
        public string shared;

        /// <summary>
        /// Content-addressed meshes referenced by this level's instances.
        /// </summary>
        public PackageAssetManifest[] assets;

        public PackageCellManifest[] cells;

        public static bool TryLoad(string packageDirectory, out PackageManifest manifest, out string error)
        {
            manifest = null;
            string path = Path.Combine(packageDirectory, "manifest.json");
            if (!File.Exists(path))
            {
                error = $"No manifest.json in {packageDirectory}";
                return false;
            }

            try
            {
                manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(path));
            }
            catch (Exception exception)
            {
                error = $"Invalid manifest: {exception.Message}";
                return false;
            }

            if (manifest?.cells == null || manifest.cells.Length == 0)
            {
                manifest = null;
                error = "Manifest lists no cells.";
                return false;
            }

            error = null;
            return true;
        }

        /// <summary>
        /// Playable-area bounds in Unity space. The exporter stores glTF world
        /// coordinates, and glTFast mirrors X when instantiating nodes.
        /// </summary>
        public Bounds GetFocusBounds()
        {
            if (TryMakeBounds(focusMin, focusMax, out Bounds focus))
            {
                return focus;
            }

            if (TryMakeBounds(min, max, out Bounds total))
            {
                return total;
            }

            return new Bounds(Vector3.zero, Vector3.one * 100f);
        }

        /// <summary>
        /// Full level bounds in Unity space, including the sky shell and distant
        /// backdrop. Used to sanity-check streamed cell placement.
        /// </summary>
        public Bounds GetWorldBounds()
        {
            if (TryMakeBounds(min, max, out Bounds total))
            {
                return total;
            }

            return GetFocusBounds();
        }

        public static Vector3 ToUnity(float[] value)
        {
            if (value == null || value.Length < 3)
            {
                return Vector3.zero;
            }

            return new Vector3(-value[0], value[1], value[2]);
        }

        /// <summary>
        /// Shared asset root in absolute form. Version 1 packages have no
        /// shared root, so the package directory itself is the fallback.
        /// </summary>
        public string GetSharedDirectory(string packageDirectory)
        {
            if (string.IsNullOrEmpty(shared))
            {
                return packageDirectory;
            }

            return Path.GetFullPath(Path.Combine(
                packageDirectory,
                shared.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool TryMakeBounds(float[] minValue, float[] maxValue, out Bounds bounds)
        {
            bounds = default;
            if (minValue == null || maxValue == null || minValue.Length < 3 || maxValue.Length < 3)
            {
                return false;
            }

            Vector3 a = ToUnity(minValue);
            Vector3 b = ToUnity(maxValue);
            Vector3 center = (a + b) * 0.5f;
            Vector3 size = new Vector3(Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y), Mathf.Abs(b.z - a.z));
            if (!IsFinite(center) || !IsFinite(size))
            {
                return false;
            }

            bounds = new Bounds(center, size);
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    [Serializable]
    public sealed class PackageCellManifest
    {
        public string id;
        public float[] min;
        public float[] max;
        public float[] center;
        public string instances;
        public int instanceCount;
        public PackageLodManifest[] lods;

        public Vector3 UnityCenter => PackageManifest.ToUnity(center);

        /// <summary>
        /// Picks the closest available authored LOD from this cell's manifest
        /// entry. Prefers at least the requested detail, then the next coarser
        /// level. Never touches the file system.
        /// </summary>
        public PackageLodManifest SelectLod(int desiredLevel)
        {
            if (lods == null || lods.Length == 0)
            {
                return null;
            }

            PackageLodManifest best = null;
            foreach (PackageLodManifest lod in lods)
            {
                if (best == null)
                {
                    best = lod;
                    continue;
                }

                bool candidateFits = lod.level >= desiredLevel;
                bool bestFits = best.level >= desiredLevel;
                if (candidateFits != bestFits)
                {
                    if (candidateFits)
                    {
                        best = lod;
                    }
                }
                else if (candidateFits)
                {
                    if (lod.level < best.level)
                    {
                        best = lod;
                    }
                }
                else if (lod.level > best.level)
                {
                    best = lod;
                }
            }

            return best;
        }
    }

    [Serializable]
    public sealed class PackageLodManifest
    {
        public int level;
        public string file;
        public long triangles;
        public long bytes;
    }

    [Serializable]
    public sealed class PackageAssetManifest
    {
        public string id;
        public string file;
        public string material;
        public long triangles;
        public long bytes;
    }

    [Serializable]
    public sealed class PackageInstancesFile
    {
        public string cell;
        public PackageInstance[] instances;
    }

    [Serializable]
    public sealed class PackageInstance
    {
        public string name;
        public float[] t;
        public float[] r;
        public float[] s;
        public PackageInstanceLod[] lods;

        public Vector3 UnityPosition => PackageManifest.ToUnity(t);

        public Quaternion UnityRotation
        {
            get
            {
                if (r == null || r.Length < 4)
                {
                    return Quaternion.identity;
                }

                // Matches glTFast's node conversion.
                return new Quaternion(r[0], -r[1], -r[2], r[3]);
            }
        }

        public Vector3 UnityScale
        {
            get
            {
                if (s == null || s.Length < 3)
                {
                    return Vector3.one;
                }

                return new Vector3(s[0], s[1], s[2]);
            }
        }

        public PackageInstanceLod SelectLod(int desiredLevel) =>
            PackageLodSelection.Select(lods, desiredLevel);
    }

    [Serializable]
    public sealed class PackageInstanceLod
    {
        public int level;
        public string[] assets;
        public long triangles;
    }

    internal static class PackageLodSelection
    {
        public static PackageInstanceLod Select(PackageInstanceLod[] lods, int desiredLevel)
        {
            if (lods == null || lods.Length == 0)
            {
                return null;
            }

            PackageInstanceLod best = null;
            foreach (PackageInstanceLod lod in lods)
            {
                if (best == null)
                {
                    best = lod;
                    continue;
                }

                bool candidateFits = lod.level >= desiredLevel;
                bool bestFits = best.level >= desiredLevel;
                if (candidateFits != bestFits)
                {
                    if (candidateFits)
                    {
                        best = lod;
                    }
                }
                else if (candidateFits)
                {
                    if (lod.level < best.level)
                    {
                        best = lod;
                    }
                }
                else if (lod.level > best.level)
                {
                    best = lod;
                }
            }

            return best;
        }
    }
}
