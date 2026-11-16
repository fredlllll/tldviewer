using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GLTFast;
using GLTFast.Loading;
using GLTFast.Materials;
using GLTFast.Schema;
using UnityEngine;
using Material = UnityEngine.Material;

namespace TldLevels.Viewer
{
    /// <summary>
    /// Shared texture cache used by streamed cells and asset imports.
    /// <para>
    /// Packages reference hash-named PNG files in the shared
    /// <c>textures/</c> folder. glTFast would decode one copy per cell or
    /// asset; instead the custom download provider hands out 1x1 placeholders
    /// and this cache supplies the real, deduplicated textures when materials
    /// are patched after instantiation.
    /// </para>
    /// <para>
    /// Reading PNGs from the level share is the slow part on a network drive,
    /// so background workers call <see cref="PrefetchFromJson"/> while the
    /// main thread is still importing geometry. Only the decode and GPU upload
    /// remain on the main thread, spread over frames by
    /// <see cref="PackageLoadBudget"/>.
    /// </para>
    /// </summary>
    public sealed class PackageTextures : IDisposable
    {
        private const long MaxPrefetchedBytes = 128L * 1024 * 1024;

        private static readonly Regex UriPattern = new Regex(
            "\"uri\"\\s*:\\s*\"(?<uri>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private readonly string textureDirectory;
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        private readonly ConcurrentDictionary<string, byte[]> prefetched =
            new ConcurrentDictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private long prefetchedBytes;
        private volatile bool disposed;

        public PackageTextures(string textureDirectory)
        {
            this.textureDirectory = textureDirectory;
        }

        public int Count => textures.Count;

        /// <summary>
        /// Reads and caches the PNG bytes referenced by a glTF JSON document.
        /// Safe to call from a background thread; this only does file I/O, no
        /// Unity API access.
        /// </summary>
        public void PrefetchFromJson(string json)
        {
            if (disposed || string.IsNullOrEmpty(json))
            {
                return;
            }

            try
            {
                foreach (Match match in UriPattern.Matches(json))
                {
                    string uri = match.Groups["uri"].Value;
                    if (!IsTextureUri(uri))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(uri.Replace('\\', '/'));
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        Prefetch(fileName);
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[PackageTextures] Prefetch failed: {exception.Message}");
            }
        }

        /// <summary>
        /// Prefetches one shared texture file. The bytes stay in a bounded
        /// staging cache until the main thread decodes them.
        /// </summary>
        public void Prefetch(string fileName)
        {
            if (disposed || string.IsNullOrEmpty(fileName) || prefetched.ContainsKey(fileName))
            {
                return;
            }

            if (Volatile.Read(ref prefetchedBytes) >= MaxPrefetchedBytes)
            {
                return;
            }

            try
            {
                string path = Path.Combine(textureDirectory, fileName);
                if (!File.Exists(path))
                {
                    return;
                }

                byte[] bytes = File.ReadAllBytes(path);
                if (prefetched.TryAdd(fileName, bytes))
                {
                    Interlocked.Add(ref prefetchedBytes, bytes.Length);
                }
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                Debug.LogWarning($"[PackageTextures] Could not prefetch {fileName}: {exception.Message}");
            }
        }

        private static bool IsTextureUri(string uri) =>
            uri.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
            || uri.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Replaces glTFast's placeholder textures on every material of a
        /// freshly loaded cell with the shared cache instances.
        /// </summary>
        public void Apply(GltfImport import) => ApplyAsync(import, null).GetAwaiter().GetResult();

        /// <summary>
        /// Budgeted variant of <see cref="Apply"/>. Decoding and uploading a
        /// PNG cannot leave the main thread, so the pauses happen between
        /// texture slots and keep the rest of the frame free.
        /// </summary>
        public async Task ApplyAsync(GltfImport import, PackageLoadBudget budget)
        {
            if (import == null)
            {
                return;
            }

            Root root = import.GetSourceRoot();
            if (root == null)
            {
                return;
            }

            bool[] gammaTextures = BuildGammaTextureMap(root);

            for (int i = 0; i < import.MaterialCount; i++)
            {
                Material material = import.GetMaterial(i);
                MaterialBase source = import.GetSourceMaterial(i);
                if (material == null || source == null)
                {
                    continue;
                }

                PbrMetallicRoughnessBase pbr = source.PbrMetallicRoughness;
                await ApplyTextureAsync(import, material, pbr?.BaseColorTexture, MaterialProperty.BaseColorTexture, gammaTextures, budget);
                await ApplyTextureAsync(import, material, pbr?.MetallicRoughnessTexture, MaterialProperty.MetallicRoughnessMap, gammaTextures, budget);
                await ApplyTextureAsync(import, material, source.NormalTexture, MaterialProperty.NormalTexture, gammaTextures, budget);
                await ApplyTextureAsync(import, material, source.OcclusionTexture, MaterialProperty.OcclusionTexture, gammaTextures, budget);
                await ApplyTextureAsync(import, material, source.EmissiveTexture, MaterialProperty.EmissiveTexture, gammaTextures, budget);

                PbrSpecularGlossiness specGloss = source.Extensions?.KHR_materials_pbrSpecularGlossiness;
                if (specGloss != null)
                {
                    await ApplyTextureAsync(import, material, specGloss.diffuseTexture, MaterialProperty.DiffuseTexture, gammaTextures, budget);
                    await ApplyTextureAsync(import, material, specGloss.specularGlossinessTexture, MaterialProperty.SpecularGlossinessTexture, gammaTextures, budget);
                }
            }
        }

        private async Task ApplyTextureAsync(
            GltfImport import,
            Material material,
            TextureInfoBase textureInfo,
            int propertyId,
            bool[] gammaTextures,
            PackageLoadBudget budget)
        {
            if (textureInfo == null || textureInfo.index < 0 || !material.HasProperty(propertyId))
            {
                return;
            }

            if (budget != null)
            {
                await budget.BreakPoint();
            }

            Texture2D texture = GetTexture(import, textureInfo.index, gammaTextures);
            if (texture != null)
            {
                material.SetTexture(propertyId, texture);
            }
        }

        private Texture2D GetTexture(GltfImport import, int textureIndex, bool[] gammaTextures)
        {
            TextureBase sourceTexture = import.GetSourceTexture(textureIndex);
            if (sourceTexture == null)
            {
                return null;
            }

            int imageIndex = sourceTexture.GetImageIndex();
            Image image = import.GetSourceImage(imageIndex);
            if (image == null || string.IsNullOrEmpty(image.uri))
            {
                return null;
            }

            string fileName = Path.GetFileName(image.uri);
            if (string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            bool linear = QualitySettings.activeColorSpace == ColorSpace.Linear
                && (gammaTextures == null || textureIndex >= gammaTextures.Length || !gammaTextures[textureIndex]);

            Sampler sampler = null;
            Root root = import.GetSourceRoot();
            if (root.Samplers != null && sourceTexture.sampler >= 0 && sourceTexture.sampler < root.Samplers.Count)
            {
                sampler = root.Samplers[sourceTexture.sampler];
            }

            FilterMode filter = sampler?.FilterMode ?? FilterMode.Bilinear;
            TextureWrapMode wrapU = sampler?.WrapU ?? TextureWrapMode.Repeat;
            TextureWrapMode wrapV = sampler?.WrapV ?? TextureWrapMode.Repeat;
            string key = $"{fileName}|{(linear ? "L" : "S")}|{(int)filter}|{(int)wrapU}|{(int)wrapV}";
            if (textures.TryGetValue(key, out Texture2D cached) && cached != null)
            {
                if (prefetched.TryRemove(fileName, out byte[] unused))
                {
                    Interlocked.Add(ref prefetchedBytes, -unused.Length);
                }

                return cached;
            }

            byte[] data;
            if (!prefetched.TryRemove(fileName, out data))
            {
                string path = Path.Combine(textureDirectory, fileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    data = File.ReadAllBytes(path);
                }
                catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                {
                    Debug.LogWarning($"[PackageTextures] Could not read {fileName}: {exception.Message}");
                    return null;
                }
            }
            else
            {
                Interlocked.Add(ref prefetchedBytes, -data.Length);
            }

            // Shared cell materials must outlive any single cell, so the cache
            // owns these textures instead of the glTF import. Unity's editor
            // asset GC does not know about plain managed references and would
            // otherwise unload them while the next cell (or LOD) loads, leaving
            // that cell with 1x1 placeholder textures.
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
            {
                name = fileName,
                hideFlags = HideFlags.DontUnloadUnusedAsset,
                filterMode = filter,
                wrapModeU = wrapU,
                wrapModeV = wrapV,
                anisoLevel = 1,
            };
            if (!texture.LoadImage(data, true))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            textures[key] = texture;
            return texture;
        }

        /// <summary>
        /// Marks textures used in color (gamma) slots, mirroring glTFast's own
        /// logic so shared textures match what a direct import would produce.
        /// </summary>
        private static bool[] BuildGammaTextureMap(Root root)
        {
            if (QualitySettings.activeColorSpace != ColorSpace.Linear || root.Textures == null)
            {
                return null;
            }

            bool[] gamma = new bool[root.Textures.Count];
            if (root.Materials == null)
            {
                return gamma;
            }

            foreach (MaterialBase material in root.Materials)
            {
                MarkGamma(material.PbrMetallicRoughness?.BaseColorTexture, gamma);
                MarkGamma(material.EmissiveTexture, gamma);
                PbrSpecularGlossiness specGloss = material.Extensions?.KHR_materials_pbrSpecularGlossiness;
                if (specGloss != null)
                {
                    MarkGamma(specGloss.diffuseTexture, gamma);
                    MarkGamma(specGloss.specularGlossinessTexture, gamma);
                }
            }

            return gamma;
        }

        private static void MarkGamma(TextureInfoBase textureInfo, bool[] gamma)
        {
            if (textureInfo != null && textureInfo.index >= 0 && textureInfo.index < gamma.Length)
            {
                gamma[textureInfo.index] = true;
            }
        }

        public void Dispose()
        {
            disposed = true;
            prefetched.Clear();
            Interlocked.Exchange(ref prefetchedBytes, 0);

            foreach (Texture2D texture in textures.Values)
            {
                if (texture != null)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(texture);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }
                }
            }

            textures.Clear();
        }
    }

    /// <summary>
    /// glTFast download provider for packages: buffers are read from disk,
    /// embedded-external images are replaced with a 1x1 placeholder, and the
    /// real shared textures are installed by <see cref="PackageTextures"/>.
    /// </summary>
    public sealed class ViewerDownloadProvider : IDownloadProvider
    {
        private static readonly byte[] TinyPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAANSURBVBhXY/j///9/AAn7A/0FQ0XKAAAAAElFTkSuQmCC");

        private readonly DefaultDownloadProvider inner = new DefaultDownloadProvider();
        private readonly IReadOnlyDictionary<string, byte[]> prefetched;

        public ViewerDownloadProvider()
        {
        }

        /// <summary>
        /// Serves the given file from memory before falling back to disk. The
        /// background prefetch phase reads a glTF and its buffer together, so
        /// the main-thread import does not touch the network share again.
        /// </summary>
        public ViewerDownloadProvider(string fileName, byte[] data)
        {
            if (!string.IsNullOrEmpty(fileName) && data != null)
            {
                prefetched = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
                {
                    [fileName] = data,
                };
            }
        }

        public Task<IDownload> Request(Uri url)
        {
            if (url != null && url.IsFile)
            {
                if (IsImage(url))
                {
                    return Task.FromResult<IDownload>(new MemoryDownload(TinyPng));
                }

                if (prefetched != null
                    && prefetched.TryGetValue(Path.GetFileName(url.LocalPath), out byte[] data))
                {
                    return Task.FromResult<IDownload>(new MemoryDownload(data));
                }

                return Task.Run<IDownload>(() => new MemoryDownload(File.ReadAllBytes(url.LocalPath)));
            }

            return inner.Request(url);
        }

        public Task<ITextureDownload> RequestTexture(Uri url, bool nonReadable)
        {
            if (url != null && url.IsFile && IsImage(url))
            {
                return Task.FromResult<ITextureDownload>(new PlaceholderTextureDownload());
            }

            return inner.RequestTexture(url, nonReadable);
        }

        private static bool IsImage(Uri url)
        {
            string path = url.AbsolutePath;
            return path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ktx2", StringComparison.OrdinalIgnoreCase);
        }

        private sealed class MemoryDownload : IDownload
        {
            public MemoryDownload(byte[] data)
            {
                Data = data;
            }

            public bool Success => true;
            public string Error => null;
            public byte[] Data { get; }
            public string Text => null;
            public bool? IsBinary => null;
            public void Dispose() { }
        }

        private sealed class PlaceholderTextureDownload : ITextureDownload
        {
            public PlaceholderTextureDownload()
            {
                Texture = new Texture2D(1, 1, TextureFormat.RGBA32, false, false);
                Texture.LoadImage(TinyPng);
            }

            public bool Success => true;
            public string Error => null;
            public byte[] Data => TinyPng;
            public string Text => null;
            public bool? IsBinary => null;
            public Texture2D Texture { get; }
            public void Dispose() { }
        }
    }
}
