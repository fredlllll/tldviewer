using System;
using System.IO;
using UnityEngine;

namespace TldLevels.Viewer
{
    /// <summary>
    /// On-disk description of a streamed level package written by the exporter
    /// (<c>--viewer-package</c>). The manifest is the only source of truth for
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

        public static Vector3 ToUnity(float[] value)
        {
            if (value == null || value.Length < 3)
            {
                return Vector3.zero;
            }

            return new Vector3(-value[0], value[1], value[2]);
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
}
