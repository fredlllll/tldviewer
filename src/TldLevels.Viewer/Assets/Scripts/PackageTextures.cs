using System;
using System.Collections.Generic;
using System.IO;
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
    /// Per-level texture cache shared by every streamed cell.
    /// <para>
    /// Package cells reference the same hash-named PNG files in
    /// <c>textures/</c>. glTFast would decode one copy per cell; instead the
    /// custom download provider hands out 1x1 placeholders and this cache
    /// supplies the real, deduplicated textures when cell materials are
    /// patched after instantiation.
    /// </para>
    /// </summary>
    public sealed class PackageTextures : IDisposable
    {
        private readonly string textureDirectory;
        private readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();

        public PackageTextures(string packageDirectory)
        {
            textureDirectory = Path.Combine(packageDirectory, "textures");
        }

        public int Count => textures.Count;

        /// <summary>
        /// Replaces glTFast's placeholder textures on every material of a
        /// freshly loaded cell with the shared cache instances.
        /// </summary>
        public void Apply(GltfImport import)
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
                ApplyTexture(import, material, pbr?.BaseColorTexture, MaterialProperty.BaseColorTexture, gammaTextures);
                ApplyTexture(import, material, pbr?.MetallicRoughnessTexture, MaterialProperty.MetallicRoughnessMap, gammaTextures);
                ApplyTexture(import, material, source.NormalTexture, MaterialProperty.NormalTexture, gammaTextures);
                ApplyTexture(import, material, source.OcclusionTexture, MaterialProperty.OcclusionTexture, gammaTextures);
                ApplyTexture(import, material, source.EmissiveTexture, MaterialProperty.EmissiveTexture, gammaTextures);

                PbrSpecularGlossiness specGloss = source.Extensions?.KHR_materials_pbrSpecularGlossiness;
                if (specGloss != null)
                {
                    ApplyTexture(import, material, specGloss.diffuseTexture, MaterialProperty.DiffuseTexture, gammaTextures);
                    ApplyTexture(import, material, specGloss.specularGlossinessTexture, MaterialProperty.SpecularGlossinessTexture, gammaTextures);
                }
            }
        }

        private void ApplyTexture(
            GltfImport import,
            Material material,
            TextureInfoBase textureInfo,
            int propertyId,
            bool[] gammaTextures)
        {
            if (textureInfo == null || textureInfo.index < 0 || !material.HasProperty(propertyId))
            {
                return;
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
            if (textures.TryGetValue(key, out Texture2D cached))
            {
                return cached;
            }

            string path = Path.Combine(textureDirectory, fileName);
            if (!File.Exists(path))
            {
                return null;
            }

            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear)
            {
                name = fileName,
                filterMode = filter,
                wrapModeU = wrapU,
                wrapModeV = wrapV,
                anisoLevel = 1,
            };
            if (!texture.LoadImage(File.ReadAllBytes(path), true))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }

            textures.Add(key, texture);
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
            foreach (Texture2D texture in textures.Values)
            {
                if (texture != null)
                {
                    UnityEngine.Object.Destroy(texture);
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

        public Task<IDownload> Request(Uri url)
        {
            if (url != null && url.IsFile)
            {
                if (IsImage(url))
                {
                    return Task.FromResult<IDownload>(new MemoryDownload(TinyPng));
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
