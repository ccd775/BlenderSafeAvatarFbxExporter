using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        private sealed class MaterialSemantics
        {
            public string RenderType;
            public Color BaseColor;
            public bool UsesTransparency;
            public int AlphaMaskMode;
            public bool EmissionEnabled;
            public Color EmissionColor;
            public float EmissionStrength;
            public bool NormalMapEnabled;
            public float NormalMapStrength;
            public bool ReflectionEnabled;
            public float Metallic;
            public float Smoothness;
        }

        private enum StandardTextureVariant
        {
            OpaqueRgb,
            AlphaFromRed
        }

        private static MaterialSemantics CaptureMaterialSemantics(Material material)
        {
            var renderType = material.GetTag("RenderType", false, string.Empty);
            var shaderName = material.shader != null ? material.shader.name : string.Empty;
            var baseColor = GetMaterialColor(material, Color.white, "_Color", "_BaseColor");
            var emissionColor = GetMaterialColor(material, Color.black, "_EmissionColor");
            var emissionMap = GetMaterialTexture(material, "_EmissionMap");
            var normalMap = GetMaterialTexture(material, "_BumpMap", "_NormalMap");

            var usesTransparency =
                renderType.IndexOf("Transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                renderType.IndexOf("Cutout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                renderType.IndexOf("Fade", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!usesTransparency && material.HasProperty("_TransparentMode"))
            {
                usesTransparency = material.GetFloat("_TransparentMode") > 0.5f;
            }
            if (!usesTransparency && string.IsNullOrEmpty(renderType))
            {
                usesTransparency =
                    shaderName.IndexOf("transparent", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    shaderName.IndexOf("cutout", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            bool emissionEnabled;
            if (material.HasProperty("_UseEmission"))
            {
                emissionEnabled = material.GetFloat("_UseEmission") > 0.5f;
            }
            else if (material.HasProperty("_EmissionEnabled"))
            {
                emissionEnabled = material.GetFloat("_EmissionEnabled") > 0.5f;
            }
            else
            {
                emissionEnabled = material.IsKeywordEnabled("_EMISSION") ||
                                  emissionMap != null ||
                                  emissionColor.maxColorComponent > 0.0001f;
            }

            var emissionBlend = GetMaterialFloat(material, 1f, "_EmissionBlend");
            var normalEnabled = normalMap != null &&
                                (!material.HasProperty("_UseBumpMap") || material.GetFloat("_UseBumpMap") > 0.5f);
            var reflectionEnabled = material.HasProperty("_UseReflection")
                ? material.GetFloat("_UseReflection") > 0.5f
                : GetMaterialTexture(material, "_MetallicGlossMap") != null ||
                  GetMaterialFloat(material, 0f, "_Metallic") > 0.0001f;

            return new MaterialSemantics
            {
                RenderType = renderType,
                BaseColor = baseColor,
                UsesTransparency = usesTransparency,
                AlphaMaskMode = Mathf.RoundToInt(GetMaterialFloat(material, 0f, "_AlphaMaskMode")),
                EmissionEnabled = emissionEnabled,
                EmissionColor = emissionEnabled ? emissionColor : Color.black,
                EmissionStrength = emissionEnabled
                    ? Mathf.Max(0f, emissionBlend * emissionColor.a)
                    : 0f,
                NormalMapEnabled = normalEnabled,
                NormalMapStrength = normalEnabled
                    ? Mathf.Max(0f, GetMaterialFloat(material, 1f, "_BumpScale"))
                    : 0f,
                ReflectionEnabled = reflectionEnabled,
                Metallic = reflectionEnabled
                    ? Mathf.Clamp01(GetMaterialFloat(material, 0f, "_Metallic"))
                    : 0f,
                Smoothness = reflectionEnabled
                    ? Mathf.Clamp01(GetMaterialFloat(material, 0.5f, "_Smoothness", "_Glossiness"))
                    : 0f
            };
        }

        private static void ConfigureStandardTextureBindings(
            Material material,
            MaterialTextureSet materialSet,
            string mediaDirectory,
            IDictionary<string, string> standardTexturePaths)
        {
            AssignStandardChannel(materialSet, FbxSurfaceMaterial.sDiffuse, "_MainTex", "_BaseMap", "_BaseColorMap");

            if (materialSet.Semantics.NormalMapEnabled)
            {
                AssignStandardChannel(materialSet, FbxSurfaceMaterial.sNormalMap, "_BumpMap", "_NormalMap");
            }
            if (materialSet.Semantics.EmissionEnabled)
            {
                AssignStandardChannel(materialSet, FbxSurfaceMaterial.sEmissive, "_EmissionMap");
            }
            if (materialSet.Semantics.UsesTransparency && materialSet.Semantics.AlphaMaskMode == 1)
            {
                AssignStandardChannel(materialSet, FbxSurfaceMaterial.sTransparentColor, "_AlphaMask");
            }

            var diffuseBinding = materialSet.Bindings.FirstOrDefault(
                binding => string.Equals(binding.StandardFbxPropertyName, FbxSurfaceMaterial.sDiffuse, StringComparison.Ordinal));
            if (diffuseBinding != null &&
                (!materialSet.Semantics.UsesTransparency || materialSet.Semantics.AlphaMaskMode == 1) &&
                SourceCanContainAlpha(diffuseBinding.SourceFullPath))
            {
                SetStandardTextureVariant(
                    material,
                    diffuseBinding,
                    mediaDirectory,
                    standardTexturePaths,
                    StandardTextureVariant.OpaqueRgb);
            }

            var alphaBinding = materialSet.Bindings.FirstOrDefault(
                binding => string.Equals(binding.StandardFbxPropertyName, FbxSurfaceMaterial.sTransparentColor, StringComparison.Ordinal));
            if (alphaBinding != null)
            {
                SetStandardTextureVariant(
                    material,
                    alphaBinding,
                    mediaDirectory,
                    standardTexturePaths,
                    StandardTextureVariant.AlphaFromRed);
            }
        }

        private static void ApplyMaterialProperties(
            FbxScene scene,
            IReadOnlyList<RendererMaterialSet> rendererSets)
        {
            var rootNode = scene.GetRootNode();
            var processedMaterials = new HashSet<MaterialTextureSet>();

            foreach (var rendererSet in rendererSets)
            {
                var node = rootNode.FindChild(rendererSet.NodeName, true);
                if (node == null)
                {
                    Warn(
                        $"FBX mesh node '{rendererSet.NodeName}' was not found while applying material " +
                        "properties; its materials keep the values Unity's FBX exporter wrote.");
                    continue;
                }
                if (node.GetMaterialCount() < rendererSet.Materials.Count)
                {
                    Warn(
                        $"FBX node '{rendererSet.NodeName}' has {node.GetMaterialCount()} materials but Unity " +
                        $"has {rendererSet.Materials.Count} slots; the extra slots were skipped.");
                }

                for (var materialIndex = 0; materialIndex < rendererSet.Materials.Count; materialIndex++)
                {
                    var materialSet = rendererSet.Materials[materialIndex];
                    if (materialSet == null || !processedMaterials.Add(materialSet))
                    {
                        continue;
                    }

                    var fbxMaterial = materialIndex < node.GetMaterialCount()
                        ? node.GetMaterial(materialIndex)
                        : null;
                    if (fbxMaterial == null)
                    {
                        Warn($"FBX material slot {materialIndex} on '{rendererSet.NodeName}' is empty; skipped.");
                        continue;
                    }

                    ApplyMaterialProperties(fbxMaterial, materialSet);
                }
            }
        }

        private static void ApplyMaterialProperties(
            FbxSurfaceMaterial fbxMaterial,
            MaterialTextureSet materialSet)
        {
            var semantics = materialSet.Semantics;
            SetMaterialMetadata(fbxMaterial, "UnityMaterialName", materialSet.UnityMaterialName);
            SetMaterialMetadata(fbxMaterial, "UnityShaderName", materialSet.UnityShaderName);
            SetMaterialMetadata(fbxMaterial, "UnityRenderType", semantics.RenderType);

            SetColorProperty(fbxMaterial, FbxSurfaceMaterial.sDiffuse, NormalizeFbxColor(semantics.BaseColor));
            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sDiffuseFactor, 1.0);

            SetColorProperty(fbxMaterial, FbxSurfaceMaterial.sEmissive, NormalizeFbxColor(semantics.EmissionColor));
            SetDoubleProperty(
                fbxMaterial,
                FbxSurfaceMaterial.sEmissiveFactor,
                Mathf.Clamp01(semantics.EmissionStrength));
            if (!semantics.EmissionEnabled)
            {
                DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sEmissive);
            }

            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sBumpFactor, semantics.NormalMapStrength);
            if (!semantics.NormalMapEnabled)
            {
                DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sNormalMap);
                DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sBump);
            }

            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sReflectionFactor, semantics.Metallic);
            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sSpecularFactor, semantics.ReflectionEnabled ? 0.25 : 0.0);
            var shininess = 100.0 * semantics.Smoothness * semantics.Smoothness;
            SetDoubleProperty(fbxMaterial, "Shininess", shininess);
            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sShininess, shininess);
            DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sReflection);
            DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sShininess);
            if (!semantics.ReflectionEnabled)
            {
                DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sSpecular);
            }

            var alpha = semantics.UsesTransparency ? Mathf.Clamp01(semantics.BaseColor.a) : 1f;
            SetColorProperty(fbxMaterial, FbxSurfaceMaterial.sTransparentColor, Color.black);
            SetDoubleProperty(fbxMaterial, FbxSurfaceMaterial.sTransparencyFactor, 1.0 - alpha);
            SetDoubleProperty(fbxMaterial, "Opacity", alpha);
            if (!semantics.UsesTransparency || semantics.AlphaMaskMode != 1)
            {
                DisconnectTexture(fbxMaterial, FbxSurfaceMaterial.sTransparentColor);
            }
        }

        private static Color NormalizeFbxColor(Color value)
        {
            var intensity = Mathf.Max(1f, value.r, value.g, value.b);
            return new Color(
                Mathf.Clamp01(value.r / intensity),
                Mathf.Clamp01(value.g / intensity),
                Mathf.Clamp01(value.b / intensity),
                Mathf.Clamp01(value.a));
        }

        private static void SetColorProperty(FbxSurfaceMaterial material, string propertyName, Color value)
        {
            var property = material.FindProperty(propertyName);
            if (property == null || !property.IsValid())
            {
                property = FbxProperty.Create(material, Globals.FbxColor3DT, propertyName, propertyName);
            }

            property.Set(new FbxColor(value.r, value.g, value.b));
            var actual = property.GetFbxColor();
            if (Math.Abs(actual.mRed - value.r) > 0.000001 ||
                Math.Abs(actual.mGreen - value.g) > 0.000001 ||
                Math.Abs(actual.mBlue - value.b) > 0.000001)
            {
                // A material colour is already an approximation of a Unity shader; a rejected write
                // is not a reason to discard the geometry.
                Warn(
                    $"Could not set FBX material property '{material.GetName()}.{propertyName}'; " +
                    "it was left at its template default.");
            }
        }

        private static void DisconnectTexture(FbxSurfaceMaterial material, string propertyName)
        {
            var property = material.FindProperty(propertyName);
            if (property != null && property.IsValid())
            {
                property.DisconnectAllSrcObject();
            }
        }

        private static Color GetMaterialColor(Material material, Color fallback, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (material.HasProperty(propertyName))
                {
                    return material.GetColor(propertyName);
                }
            }
            return fallback;
        }

        private static float GetMaterialFloat(Material material, float fallback, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (material.HasProperty(propertyName))
                {
                    return material.GetFloat(propertyName);
                }
            }
            return fallback;
        }

        private static Texture GetMaterialTexture(Material material, params string[] propertyNames)
        {
            foreach (var propertyName in propertyNames)
            {
                if (material.HasProperty(propertyName))
                {
                    var texture = material.GetTexture(propertyName);
                    if (texture != null)
                    {
                        return texture;
                    }
                }
            }
            return null;
        }

        private static string StageMaterialTexture(
            Texture texture,
            string assetPath,
            string sourceFullPath,
            string mediaDirectory,
            string materialName,
            string propertyName)
        {
            var extension = File.Exists(sourceFullPath)
                ? Path.GetExtension(sourceFullPath).ToLowerInvariant()
                : string.Empty;
            var canEmbedSource = extension == ".png" ||
                                 extension == ".jpg" ||
                                 extension == ".jpeg" ||
                                 extension == ".tga" ||
                                 extension == ".bmp" ||
                                 extension == ".tif" ||
                                 extension == ".tiff" ||
                                 extension == ".exr" ||
                                 extension == ".hdr" ||
                                 extension == ".psd";
            var guid = string.IsNullOrEmpty(assetPath)
                ? Math.Abs(texture.GetInstanceID()).ToString("x8")
                : AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                guid = Math.Abs((assetPath ?? texture.name).GetHashCode()).ToString("x8");
            }
            var sourceName = File.Exists(sourceFullPath)
                ? Path.GetFileNameWithoutExtension(sourceFullPath)
                : texture.name;
            var stem = MakeSafeFileName(sourceName);

            if (canEmbedSource)
            {
                var stagedSourcePath = Path.Combine(
                    mediaDirectory,
                    $"{stem}__{guid.Substring(0, Math.Min(8, guid.Length))}{extension}");
                File.Copy(sourceFullPath, stagedSourcePath, true);
                return stagedSourcePath;
            }

            if (!(texture is Texture2D) && !(texture is RenderTexture))
            {
                // Cubemaps, texture arrays and 3D textures have no FBX equivalent. Skipping the one
                // binding is far cheaper than refusing to export the avatar.
                Warn(
                    $"Texture '{texture.name}' used by '{materialName}.{propertyName}' is a " +
                    $"{texture.GetType().Name}; only Texture2D and RenderTexture can be embedded. " +
                    "The binding was skipped.");
                return null;
            }

            var stagedPngPath = Path.Combine(
                mediaDirectory,
                $"{stem}__{guid.Substring(0, Math.Min(8, guid.Length))}.png");
            WriteTexturePng(texture, stagedPngPath);
            return stagedPngPath;
        }

        private static void WriteTexturePng(Texture sourceTexture, string outputPath)
        {
            var readableTexture = ReadTextureWithGpu(sourceTexture);
            try
            {
                File.WriteAllBytes(outputPath, readableTexture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(readableTexture);
            }
        }

        private static void SetStandardTextureVariant(
            Material material,
            TextureBinding binding,
            string mediaDirectory,
            IDictionary<string, string> standardTexturePaths,
            StandardTextureVariant variant)
        {
            var cacheKey = variant + "|" + binding.UnityAssetPath;
            if (!standardTexturePaths.TryGetValue(cacheKey, out var outputPath))
            {
                var sourceTexture = material.GetTexture(binding.UnityPropertyName);
                if (sourceTexture == null)
                {
                    return;
                }

                var guid = AssetDatabase.AssetPathToGUID(binding.UnityAssetPath);
                if (string.IsNullOrEmpty(guid))
                {
                    guid = Math.Abs(binding.UnityAssetPath.GetHashCode()).ToString("x8");
                }
                var stem = MakeSafeFileName(Path.GetFileNameWithoutExtension(binding.SourceFullPath));
                var suffix = variant == StandardTextureVariant.OpaqueRgb ? "fbx_rgb" : "fbx_alpha_r";
                outputPath = Path.Combine(
                    mediaDirectory,
                    $"{stem}__{guid.Substring(0, Math.Min(8, guid.Length))}__{suffix}.png");
                WriteStandardTextureVariant(sourceTexture, binding.SourceFullPath, outputPath, variant);
                standardTexturePaths.Add(cacheKey, outputPath);
            }

            binding.StandardSourceFullPath = outputPath;
            binding.StandardEmbeddedFileName = Path.GetFileName(outputPath);
        }

        private static void WriteStandardTextureVariant(
            Texture sourceTexture,
            string sourcePath,
            string outputPath,
            StandardTextureVariant variant)
        {
            Texture2D readableTexture = null;
            Texture2D outputTexture = null;
            try
            {
                readableTexture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                if (!ImageConversion.LoadImage(readableTexture, File.ReadAllBytes(sourcePath), false))
                {
                    UnityEngine.Object.DestroyImmediate(readableTexture);
                    readableTexture = ReadTextureWithGpu(sourceTexture);
                }

                var pixels = readableTexture.GetPixels32();
                byte[] encoded;
                if (variant == StandardTextureVariant.OpaqueRgb)
                {
                    var rgb = new byte[pixels.Length * 3];
                    for (var index = 0; index < pixels.Length; index++)
                    {
                        var byteIndex = index * 3;
                        rgb[byteIndex] = pixels[index].r;
                        rgb[byteIndex + 1] = pixels[index].g;
                        rgb[byteIndex + 2] = pixels[index].b;
                    }
                    outputTexture = new Texture2D(
                        readableTexture.width,
                        readableTexture.height,
                        TextureFormat.RGB24,
                        false,
                        false);
                    outputTexture.LoadRawTextureData(rgb);
                    outputTexture.Apply(false, false);
                    encoded = outputTexture.EncodeToPNG();
                }
                else
                {
                    for (var index = 0; index < pixels.Length; index++)
                    {
                        var value = pixels[index].r;
                        pixels[index] = new Color32(value, value, value, value);
                    }
                    outputTexture = new Texture2D(
                        readableTexture.width,
                        readableTexture.height,
                        TextureFormat.RGBA32,
                        false,
                        true);
                    outputTexture.SetPixels32(pixels);
                    outputTexture.Apply(false, false);
                    encoded = outputTexture.EncodeToPNG();
                }

                File.WriteAllBytes(outputPath, encoded);
            }
            finally
            {
                if (outputTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(outputTexture);
                }
                if (readableTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(readableTexture);
                }
            }
        }

        private static Texture2D ReadTextureWithGpu(Texture sourceTexture)
        {
            var renderTexture = RenderTexture.GetTemporary(
                sourceTexture.width,
                sourceTexture.height,
                0,
                RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.Default);
            var previous = RenderTexture.active;
            try
            {
                Graphics.Blit(sourceTexture, renderTexture);
                RenderTexture.active = renderTexture;
                var readable = new Texture2D(
                    sourceTexture.width,
                    sourceTexture.height,
                    TextureFormat.RGBA32,
                    false,
                    false);
                readable.ReadPixels(new Rect(0, 0, sourceTexture.width, sourceTexture.height), 0, 0, false);
                readable.Apply(false, false);
                return readable;
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        private static bool SourceCanContainAlpha(string sourcePath)
        {
            var extension = Path.GetExtension(sourcePath);
            if (string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (!string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            using (var stream = File.OpenRead(sourcePath))
            {
                if (stream.Length < 26)
                {
                    return true;
                }
                stream.Position = 25;
                var colorType = stream.ReadByte();
                return colorType == 3 || colorType == 4 || colorType == 6;
            }
        }
    }
}
