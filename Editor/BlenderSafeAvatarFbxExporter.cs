using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Fbx;
using Unity.Collections;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;
using UnityEngine.Animations;
using Object = UnityEngine.Object;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        public const string Version = "0.1.0";

        private const string RequiredFbxExporterVersion = "4.2.1";
        private const string RequiredFbxSdkVersion = "4.2.1";
        private const float PoseErrorTolerance = 0.0001f;
        private const float MatrixErrorTolerance = 0.0001f;
        private const double FbxMatrixErrorTolerance = 0.0000001;
        private const double FbxShearTolerance = 0.00000001;

        private sealed class BlendShapeWeightSet
        {
            public string NodeName;
            public readonly List<BlendShapeWeight> Weights = new List<BlendShapeWeight>();
        }

        private struct BlendShapeWeight
        {
            public string Name;
            public float Weight;
        }

        private sealed class RendererMaterialSet
        {
            public string NodeName;
            public readonly List<MaterialTextureSet> Materials = new List<MaterialTextureSet>();
        }

        private sealed class MaterialTextureSet
        {
            public string UnityMaterialName;
            public string UnityShaderName;
            public MaterialSemantics Semantics;
            public readonly List<TextureBinding> Bindings = new List<TextureBinding>();
        }

        private sealed class TextureBinding
        {
            public string UnityPropertyName;
            public string UnityAssetPath;
            public string SourceFullPath;
            public string EmbeddedFileName;
            public string StandardSourceFullPath;
            public string StandardEmbeddedFileName;
            public string StandardFbxPropertyName;
            public Vector2 Scale;
            public Vector2 Offset;
            public TextureWrapMode WrapModeU;
            public TextureWrapMode WrapModeV;
        }

        public static BlenderSafeFbxExportResult Export(
            GameObject sourceAvatar,
            string outputPath,
            bool embedAllMaterialTextures = true)
        {
            return Export(sourceAvatar, outputPath, embedAllMaterialTextures, false);
        }

        public static BlenderSafeFbxExportResult Export(
            GameObject sourceAvatar,
            string outputPath,
            bool embedAllMaterialTextures,
            bool overwriteExisting)
        {
            ValidateEnvironment();
            ValidateSource(sourceAvatar);

            var fullOutputPath = Path.GetFullPath(outputPath);
            if (!string.Equals(Path.GetExtension(fullOutputPath), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                fullOutputPath += ".fbx";
            }

            var outputDirectory = Path.GetDirectoryName(fullOutputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                throw new ArgumentException("Invalid FBX output path.", nameof(outputPath));
            }
            if (File.Exists(fullOutputPath) && !overwriteExisting)
            {
                throw new IOException(
                    $"The target FBX already exists: '{fullOutputPath}'. " +
                    "Pass overwriteExisting: true only after confirming replacement.");
            }
            Directory.CreateDirectory(outputDirectory);

            var stagingDirectory = CreateStagingDirectory();
            var stagingPath = Path.Combine(stagingDirectory, "avatar.fbx");
            var createdMeshes = new List<Mesh>();
            GameObject cloneContainer = null;
            GameObject clone = null;

            try
            {
                cloneContainer = new GameObject("BlenderSafeExportContainer")
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                cloneContainer.SetActive(false);
                clone = Object.Instantiate(sourceAvatar, cloneContainer.transform, true);
                var separatedBoneRendererCount = SeparateBoneHostedSkinnedMeshRenderers(clone);
                clone.name = MakeUniqueCompatibleNames(clone, out var renamedCount);
                clone.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;
                clone.transform.position = Vector3.zero;

                RemoveAnimationDrivers(clone);

                var result = new BlenderSafeFbxExportResult
                {
                    OutputPath = fullOutputPath,
                    TransformCount = clone.GetComponentsInChildren<Transform>(true).Length,
                    RenamedTransformCount = renamedCount,
                    SeparatedBoneRendererCount = separatedBoneRendererCount,
                    EmbeddedTextures = embedAllMaterialTextures
                };

                var materialSets = CollectMaterialTextures(
                    clone,
                    stagingDirectory,
                    result,
                    embedAllMaterialTextures);
                var weightSets = PrepareClone(clone, result, createdMeshes);
                RemoveNaNAnimationDeletionTransforms(clone);
                result.StandardizedBoneCount = StandardizeSkeletonScales(
                    clone,
                    out var skeletonNormalizationError);
                result.MaxSkeletonNormalizationError = skeletonNormalizationError;
                if (result.MaxSkeletonNormalizationError > PoseErrorTolerance)
                {
                    throw new InvalidOperationException(
                        $"Skeleton scale normalization changed the visible mesh. " +
                        $"Max vertex error: {result.MaxSkeletonNormalizationError:E6}");
                }
                result.MaxBindPoseError = ValidateUnifiedBindPoses(clone);
                if (result.MaxBindPoseError > MatrixErrorTolerance)
                {
                    throw new InvalidOperationException(
                        $"The generated bind poses are not unified. Max matrix error: {result.MaxBindPoseError:E6}");
                }

                result.AdjustedFbxControlPointCount = UniquifyBlendShapeControlPoints(
                    clone,
                    out var maxControlPointAdjustment);
                result.MaxFbxControlPointAdjustment = maxControlPointAdjustment;
                if (result.MaxFbxControlPointAdjustment > PoseErrorTolerance)
                {
                    throw new InvalidOperationException(
                        $"FBX control-point disambiguation changed the mesh too much. " +
                        $"Max vertex adjustment: {result.MaxFbxControlPointAdjustment:E6}");
                }

                var exportedPath = ModelExporter.ExportObject(stagingPath, clone);
                if (string.IsNullOrEmpty(exportedPath) || !File.Exists(stagingPath))
                {
                    throw new InvalidOperationException("Unity FBX Exporter did not produce an FBX file.");
                }

                ApplyFbxPostProcess(
                    stagingPath,
                    weightSets,
                    materialSets,
                    embedAllMaterialTextures);
                CommitOutputFile(stagingPath, fullOutputPath, overwriteExisting);

                result.PreservedNonZeroBlendShapeWeights = weightSets.Sum(
                    set => set.Weights.Count(weight => Mathf.Abs(weight.Weight) > 0.0001f));
                return result;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                TryDeleteStagingDirectory(stagingDirectory);
                if (cloneContainer != null)
                {
                    Object.DestroyImmediate(cloneContainer);
                }
                else if (clone != null)
                {
                    Object.DestroyImmediate(clone);
                }
                foreach (var mesh in createdMeshes)
                {
                    if (mesh != null)
                    {
                        Object.DestroyImmediate(mesh);
                    }
                }
            }
        }

        public static string MakeSafeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                builder.Append(invalid.Contains(character) ? '_' : character);
            }
            return builder.ToString();
        }

        public static string TryGetAssetPath(string path)
        {
            var fullPath = Path.GetFullPath(path).Replace('\\', '/');
            var assetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
            if (!fullPath.StartsWith(assetsPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            return "Assets/" + fullPath.Substring(assetsPath.Length + 1);
        }

        private static void ValidateSource(GameObject sourceAvatar)
        {
            if (sourceAvatar == null)
            {
                throw new ArgumentNullException(nameof(sourceAvatar));
            }

            var renderers = sourceAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0)
            {
                throw new InvalidOperationException("The selected object has no SkinnedMeshRenderer.");
            }
            var rendererTransformsUsedAsBones = new HashSet<Transform>(
                renderers.SelectMany(renderer => renderer.bones).Where(bone => bone != null));

            foreach (var renderer in renderers)
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null)
                {
                    throw new InvalidOperationException($"SkinnedMeshRenderer '{GetPath(renderer.transform, sourceAvatar.transform)}' has no mesh.");
                }
                if (!mesh.isReadable)
                {
                    throw new InvalidOperationException(
                        $"Mesh '{mesh.name}' is not readable. Enable Read/Write on its Model Import Settings before exporting.");
                }
                if (renderer.bones == null || renderer.bones.Length == 0)
                {
                    throw new InvalidOperationException($"Skinned mesh '{mesh.name}' has no bones.");
                }
                if (renderer.transform != sourceAvatar.transform &&
                    renderer.transform.childCount > 0 &&
                    !rendererTransformsUsedAsBones.Contains(renderer.transform))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh object '{GetPath(renderer.transform, sourceAvatar.transform)}' has child objects. " +
                        "Move those children out before exporting so the mesh transform can be normalized safely.");
                }
                if (mesh.bindposes.Length != renderer.bones.Length)
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{mesh.name}' has {renderer.bones.Length} bones but {mesh.bindposes.Length} bind poses.");
                }
                if (renderer.rootBone != null &&
                    renderer.rootBone != sourceAvatar.transform &&
                    !renderer.rootBone.IsChildOf(sourceAvatar.transform))
                {
                    throw new InvalidOperationException(
                        $"Root bone '{renderer.rootBone.name}' used by mesh '{mesh.name}' is outside the selected avatar hierarchy.");
                }
                ValidateBoneWeights(mesh, renderer.bones.Length);
                for (var shapeIndex = 0; shapeIndex < mesh.blendShapeCount; shapeIndex++)
                {
                    if (mesh.GetBlendShapeFrameCount(shapeIndex) > 1)
                    {
                        throw new InvalidOperationException(
                            $"BlendShape '{mesh.GetBlendShapeName(shapeIndex)}' on mesh '{mesh.name}' uses in-between frames. " +
                            "Blender's FBX importer does not support multiple frames in one BlendShape channel.");
                    }
                }
                var weightedBoneIndices = GetWeightedBoneIndices(mesh);
                for (var boneIndex = 0; boneIndex < renderer.bones.Length; boneIndex++)
                {
                    var bone = renderer.bones[boneIndex];
                    if (bone == null)
                    {
                        if (weightedBoneIndices.Contains(boneIndex))
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has vertex weights assigned to a missing bone at index {boneIndex}.");
                        }
                        continue;
                    }
                    if (bone != sourceAvatar.transform && !bone.IsChildOf(sourceAvatar.transform))
                    {
                        throw new InvalidOperationException(
                            $"Bone '{bone.name}' used by mesh '{mesh.name}' is outside the selected avatar hierarchy.");
                    }
                }
            }
        }

        private static List<RendererMaterialSet> CollectMaterialTextures(
            GameObject root,
            string stagingDirectory,
            BlenderSafeFbxExportResult result,
            bool collectTextures)
        {
            var mediaDirectory = Path.Combine(stagingDirectory, "media");
            if (collectTextures)
            {
                Directory.CreateDirectory(mediaDirectory);
            }

            var stagedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var standardTexturePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var materialCache = new Dictionary<Material, MaterialTextureSet>();
            var rendererSets = new List<RendererMaterialSet>();

            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                if (!(renderer is SkinnedMeshRenderer))
                {
                    if (!(renderer is MeshRenderer))
                    {
                        continue;
                    }
                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                    {
                        continue;
                    }
                }

                var rendererSet = new RendererMaterialSet { NodeName = renderer.name };
                foreach (var material in renderer.sharedMaterials)
                {
                    if (material == null)
                    {
                        rendererSet.Materials.Add(null);
                        continue;
                    }

                    if (!materialCache.TryGetValue(material, out var materialSet))
                    {
                        materialSet = CreateMaterialTextureSet(
                            material,
                            mediaDirectory,
                            stagedPaths,
                            standardTexturePaths,
                            result,
                            collectTextures);
                        materialCache.Add(material, materialSet);
                    }
                    rendererSet.Materials.Add(materialSet);
                }
                rendererSets.Add(rendererSet);
            }

            result.EmbeddedTextureFileCount = stagedPaths.Count + standardTexturePaths.Count;
            return rendererSets;
        }

        private static MaterialTextureSet CreateMaterialTextureSet(
            Material material,
            string mediaDirectory,
            IDictionary<string, string> stagedPaths,
            IDictionary<string, string> standardTexturePaths,
            BlenderSafeFbxExportResult result,
            bool collectTextures)
        {
            var materialSet = new MaterialTextureSet
            {
                UnityMaterialName = material.name,
                UnityShaderName = material.shader != null ? material.shader.name : string.Empty,
                Semantics = CaptureMaterialSemantics(material)
            };

            if (!collectTextures)
            {
                return materialSet;
            }

            foreach (var propertyName in material.GetTexturePropertyNames())
            {
                var texture = material.GetTexture(propertyName);
                if (texture == null)
                {
                    continue;
                }

                var assetPath = AssetDatabase.GetAssetPath(texture);
                var sourceFullPath = ResolveAssetSourcePath(assetPath);
                var cacheKey = string.IsNullOrEmpty(assetPath)
                    ? $"generated:{texture.GetInstanceID()}"
                    : assetPath;
                if (!stagedPaths.TryGetValue(cacheKey, out var stagedPath))
                {
                    stagedPath = StageMaterialTexture(
                        texture,
                        assetPath,
                        sourceFullPath,
                        mediaDirectory,
                        material.name,
                        propertyName);
                    stagedPaths.Add(cacheKey, stagedPath);
                    result.EmbeddedTextureSourceBytes += File.Exists(sourceFullPath)
                        ? new FileInfo(sourceFullPath).Length
                        : new FileInfo(stagedPath).Length;
                }

                materialSet.Bindings.Add(new TextureBinding
                {
                    UnityPropertyName = propertyName,
                    UnityAssetPath = string.IsNullOrEmpty(assetPath)
                        ? $"generated://{texture.name}"
                        : assetPath,
                    SourceFullPath = stagedPath,
                    EmbeddedFileName = Path.GetFileName(stagedPath),
                    Scale = material.GetTextureScale(propertyName),
                    Offset = material.GetTextureOffset(propertyName),
                    WrapModeU = texture.wrapModeU,
                    WrapModeV = texture.wrapModeV
                });
                result.EmbeddedTextureBindingCount++;
            }

            ConfigureStandardTextureBindings(
                material,
                materialSet,
                mediaDirectory,
                standardTexturePaths);
            return materialSet;
        }

        private static void AssignStandardChannel(
            MaterialTextureSet materialSet,
            string fbxPropertyName,
            params string[] unityPropertyNames)
        {
            foreach (var unityPropertyName in unityPropertyNames)
            {
                var binding = materialSet.Bindings.FirstOrDefault(
                    candidate => string.Equals(candidate.UnityPropertyName, unityPropertyName, StringComparison.Ordinal));
                if (binding == null)
                {
                    continue;
                }

                binding.StandardFbxPropertyName = fbxPropertyName;
                return;
            }
        }

        private static List<BlendShapeWeightSet> PrepareClone(
            GameObject clone,
            BlenderSafeFbxExportResult result,
            List<Mesh> createdMeshes)
        {
            var renderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null)
                .ToArray();
            var weightSets = new List<BlendShapeWeightSet>(renderers.Length);

            for (var rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
            {
                var renderer = renderers[rendererIndex];
                EditorUtility.DisplayProgressBar(
                    "Preparing Blender-Safe Avatar",
                    GetPath(renderer.transform, clone.transform),
                    rendererIndex / (float)Mathf.Max(1, renderers.Length));

                var report = BakeRenderer(renderer, clone.transform, createdMeshes, out var weightSet);
                result.Renderers.Add(report);
                weightSets.Add(weightSet);
                result.SkinnedRendererCount++;
                result.VertexCount += report.VertexCount;
                result.BlendShapeCount += report.BlendShapeCount;
                result.BlendShapeFrameCount += report.BlendShapeFrameCount;
                result.CulledNaNAnimationVertexCount += report.CulledNaNAnimationVertexCount;
                result.CulledNaNAnimationPrimitiveCount += report.CulledNaNAnimationPrimitiveCount;
                result.MaxPoseBakeError = Mathf.Max(result.MaxPoseBakeError, report.MaxPoseBakeError);
            }

            if (result.MaxPoseBakeError > PoseErrorTolerance)
            {
                var failedRenderers = string.Join(
                    ", ",
                    result.Renderers
                        .Where(report => report.MaxPoseBakeError > PoseErrorTolerance)
                        .OrderByDescending(report => report.MaxPoseBakeError)
                        .Take(8)
                        .Select(report => $"{report.Name}={report.MaxPoseBakeError:E3}"));
                throw new InvalidOperationException(
                    $"Pose baking changed the visible mesh. Max vertex error: {result.MaxPoseBakeError:E6}. " +
                    $"Affected renderers: {failedRenderers}");
            }

            return weightSets;
        }

        private static BlenderSafeRendererReport BakeRenderer(
            SkinnedMeshRenderer renderer,
            Transform avatarRoot,
            List<Mesh> createdMeshes,
            out BlendShapeWeightSet weightSet)
        {
            var originalSourceMesh = renderer.sharedMesh;
            var originalBones = renderer.bones;
            var sourceMesh = originalSourceMesh;
            IReadOnlyList<Transform> sourceBones = originalBones;
            var shapeCount = sourceMesh.blendShapeCount;
            var originalWeights = new float[shapeCount];
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                originalWeights[shapeIndex] = renderer.GetBlendShapeWeight(shapeIndex);
            }

            weightSet = new BlendShapeWeightSet { NodeName = renderer.name };
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                weightSet.Weights.Add(new BlendShapeWeight
                {
                    Name = sourceMesh.GetBlendShapeName(shapeIndex),
                    Weight = originalWeights[shapeIndex]
                });
            }

            var referenceMesh = new Mesh { name = sourceMesh.name + "__Reference" };
            var frameMesh = new Mesh { name = sourceMesh.name + "__Frame" };
            Mesh sanitizedSourceMesh = null;
            Mesh bakedMesh = null;

            try
            {
                sanitizedSourceMesh = CreateNaNAnimationSanitizedMesh(
                    renderer,
                    sourceMesh,
                    sourceBones,
                    out var sanitizedBones,
                    out var culledVertexCount,
                    out var culledPrimitiveCount);
                if (sanitizedSourceMesh != null)
                {
                    sourceMesh = sanitizedSourceMesh;
                    sourceBones = sanitizedBones;
                    renderer.sharedMesh = sanitizedSourceMesh;
                    renderer.bones = sanitizedBones;
                }

                var rendererToRoot = GetBakeMeshToRootMatrix(renderer.transform, avatarRoot);
                renderer.BakeMesh(referenceMesh, false);
                ValidateFiniteMesh(referenceMesh, renderer, "current pose");
                TransformMesh(referenceMesh, rendererToRoot);
                ValidateFiniteMesh(referenceMesh, renderer, "transformed current pose");
                var report = CreateRendererReport(renderer, referenceMesh);
                report.CulledNaNAnimationVertexCount = culledVertexCount;
                report.CulledNaNAnimationPrimitiveCount = culledPrimitiveCount;

                SetAllBlendShapeWeights(renderer, 0f);
                bakedMesh = new Mesh { name = renderer.name + "__BlenderSafeMesh" };
                renderer.BakeMesh(bakedMesh, false);
                ValidateFiniteMesh(bakedMesh, renderer, "zero BlendShape pose");
                TransformMesh(bakedMesh, rendererToRoot);
                ValidateFiniteMesh(bakedMesh, renderer, "transformed zero BlendShape pose");
                bakedMesh.name = renderer.name + "__BlenderSafeMesh";
                CompactBoneData(sourceMesh, sourceBones, avatarRoot, bakedMesh, out var compactBones);

                var baseVertices = bakedMesh.vertices;
                var baseNormals = bakedMesh.normals;
                var baseTangents = bakedMesh.tangents;
                var deltaVertices = new Vector3[bakedMesh.vertexCount];
                var deltaNormals = baseNormals.Length == bakedMesh.vertexCount
                    ? new Vector3[bakedMesh.vertexCount]
                    : null;
                var deltaTangents = baseTangents.Length == bakedMesh.vertexCount
                    ? new Vector3[bakedMesh.vertexCount]
                    : null;

                var frameTotal = 0;
                for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
                {
                    var frameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
                    frameTotal += frameCount;
                    for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        var frameWeight = sourceMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                        renderer.SetBlendShapeWeight(shapeIndex, frameWeight);
                        renderer.BakeMesh(frameMesh, false);
                        ValidateFiniteMesh(
                            frameMesh,
                            renderer,
                            $"BlendShape '{sourceMesh.GetBlendShapeName(shapeIndex)}' frame {frameIndex}");
                        TransformMesh(frameMesh, rendererToRoot);
                        ValidateFiniteMesh(
                            frameMesh,
                            renderer,
                            $"transformed BlendShape '{sourceMesh.GetBlendShapeName(shapeIndex)}' frame {frameIndex}");
                        renderer.SetBlendShapeWeight(shapeIndex, 0f);

                        var frameVertices = frameMesh.vertices;
                        for (var vertexIndex = 0; vertexIndex < deltaVertices.Length; vertexIndex++)
                        {
                            deltaVertices[vertexIndex] = frameVertices[vertexIndex] - baseVertices[vertexIndex];
                        }

                        if (deltaNormals != null)
                        {
                            var frameNormals = frameMesh.normals;
                            for (var vertexIndex = 0; vertexIndex < deltaNormals.Length; vertexIndex++)
                            {
                                deltaNormals[vertexIndex] = frameNormals[vertexIndex] - baseNormals[vertexIndex];
                            }
                        }

                        if (deltaTangents != null)
                        {
                            var frameTangents = frameMesh.tangents;
                            for (var vertexIndex = 0; vertexIndex < deltaTangents.Length; vertexIndex++)
                            {
                                deltaTangents[vertexIndex] = (Vector3)frameTangents[vertexIndex] -
                                                             (Vector3)baseTangents[vertexIndex];
                            }
                        }

                        bakedMesh.AddBlendShapeFrame(
                            sourceMesh.GetBlendShapeName(shapeIndex),
                            frameWeight,
                            deltaVertices,
                            deltaNormals,
                            deltaTangents);
                    }
                }

                if (renderer.transform != avatarRoot)
                {
                    renderer.transform.SetParent(avatarRoot, false);
                    renderer.transform.localPosition = Vector3.zero;
                    renderer.transform.localRotation = Quaternion.identity;
                    renderer.transform.localScale = Vector3.one;
                }

                bakedMesh.bindposes = compactBones
                    .Select(bone => bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix)
                    .ToArray();
                renderer.bones = compactBones;
                renderer.sharedMesh = bakedMesh;
                renderer.localBounds = bakedMesh.bounds;
                createdMeshes.Add(bakedMesh);
                bakedMesh = null;

                RestoreBlendShapeWeights(renderer, originalWeights);
                report.BlendShapeFrameCount = frameTotal;
                report.MaxPoseBakeError = CalculateBlendShapeReconstructionError(
                    referenceMesh,
                    renderer.sharedMesh,
                    originalWeights);
                return report;
            }
            finally
            {
                if (renderer.sharedMesh == sourceMesh)
                {
                    RestoreBlendShapeWeights(renderer, originalWeights);
                }
                if (sanitizedSourceMesh != null)
                {
                    if (renderer.sharedMesh == sanitizedSourceMesh)
                    {
                        renderer.sharedMesh = originalSourceMesh;
                        renderer.bones = originalBones;
                    }
                    Object.DestroyImmediate(sanitizedSourceMesh);
                }
                if (bakedMesh != null)
                {
                    Object.DestroyImmediate(bakedMesh);
                }
                Object.DestroyImmediate(referenceMesh);
                Object.DestroyImmediate(frameMesh);
            }
        }

        private static BlenderSafeRendererReport CreateRendererReport(
            SkinnedMeshRenderer renderer,
            Mesh bakedCurrentMesh)
        {
            var vertices = bakedCurrentMesh.vertices;
            var bounds = new Bounds();
            if (vertices.Length > 0)
            {
                bounds = new Bounds(vertices[0], Vector3.zero);
                for (var index = 1; index < vertices.Length; index++)
                {
                    bounds.Encapsulate(vertices[index]);
                }
            }

            return new BlenderSafeRendererReport
            {
                Name = renderer.name,
                VertexCount = bakedCurrentMesh.vertexCount,
                BlendShapeCount = renderer.sharedMesh.blendShapeCount,
                BoundsCenter = bounds.center,
                BoundsSize = bounds.size
            };
        }

        private static Mesh CreateNaNAnimationSanitizedMesh(
            SkinnedMeshRenderer renderer,
            Mesh source,
            IReadOnlyList<Transform> sourceBones,
            out Transform[] sanitizedBones,
            out int culledVertexCount,
            out int culledPrimitiveCount)
        {
            var invalidBoneIndices = new HashSet<int>();
            for (var boneIndex = 0; boneIndex < sourceBones.Count; boneIndex++)
            {
                var bone = sourceBones[boneIndex];
                if (bone == null || IsFinite(bone.localToWorldMatrix))
                {
                    continue;
                }
                if (!IsNaNAnimationDeletionBone(bone))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{source.name}' uses bone '{bone.name}' with a non-finite Transform. " +
                        "Only NDMF NaNimation deletion bones can be converted safely.");
                }
                invalidBoneIndices.Add(boneIndex);
            }

            culledVertexCount = 0;
            culledPrimitiveCount = 0;
            if (invalidBoneIndices.Count == 0)
            {
                sanitizedBones = sourceBones.ToArray();
                return null;
            }

            var oldToNew = Enumerable.Repeat(-1, sourceBones.Count).ToArray();
            var finiteBones = new List<Transform>(sourceBones.Count - invalidBoneIndices.Count);
            var finiteBindPoses = new List<Matrix4x4>(finiteBones.Capacity);
            var sourceBindPoses = source.bindposes;
            for (var oldIndex = 0; oldIndex < sourceBones.Count; oldIndex++)
            {
                var bone = sourceBones[oldIndex];
                if (bone == null || invalidBoneIndices.Contains(oldIndex))
                {
                    continue;
                }
                if (!IsFinite(sourceBindPoses[oldIndex]))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{source.name}' has a non-finite bind pose at bone index {oldIndex}.");
                }
                oldToNew[oldIndex] = finiteBones.Count;
                finiteBones.Add(bone);
                finiteBindPoses.Add(sourceBindPoses[oldIndex]);
            }
            if (finiteBones.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Skinned mesh '{source.name}' has no finite bones after converting NaNimation deletion bones.");
            }

            var fallbackOldIndex = -1;
            for (var boneIndex = 0; boneIndex < sourceBones.Count; boneIndex++)
            {
                if (sourceBones[boneIndex] == renderer.rootBone && oldToNew[boneIndex] >= 0)
                {
                    fallbackOldIndex = boneIndex;
                    break;
                }
            }
            if (fallbackOldIndex < 0)
            {
                fallbackOldIndex = Array.FindIndex(oldToNew, mappedIndex => mappedIndex >= 0);
            }
            var fallbackBoneIndex = oldToNew[fallbackOldIndex];

            var culledVertices = new bool[source.vertexCount];
            var destinationCounts = new byte[source.vertexCount];
            var destinationWeights = new List<BoneWeight1>();
            using (var sourceCounts = source.GetBonesPerVertex())
            using (var sourceWeights = source.GetAllBoneWeights())
            {
                var cursor = 0;
                for (var vertexIndex = 0; vertexIndex < sourceCounts.Length; vertexIndex++)
                {
                    var firstWeight = cursor;
                    for (var influenceIndex = 0; influenceIndex < sourceCounts[vertexIndex]; influenceIndex++)
                    {
                        var weight = sourceWeights[cursor++];
                        if (!IsFinite(weight.weight) || weight.boneIndex < 0 || weight.boneIndex >= sourceBones.Count)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{source.name}' has an invalid bone weight at vertex {vertexIndex}.");
                        }
                        if (weight.weight != 0f && invalidBoneIndices.Contains(weight.boneIndex))
                        {
                            culledVertices[vertexIndex] = true;
                        }
                    }

                    if (culledVertices[vertexIndex])
                    {
                        culledVertexCount++;
                        destinationCounts[vertexIndex] = 1;
                        destinationWeights.Add(new BoneWeight1
                        {
                            boneIndex = fallbackBoneIndex,
                            weight = 1f
                        });
                        continue;
                    }

                    var destinationCount = 0;
                    for (var weightIndex = firstWeight; weightIndex < cursor; weightIndex++)
                    {
                        var weight = sourceWeights[weightIndex];
                        if (weight.weight == 0f)
                        {
                            continue;
                        }
                        var mappedIndex = oldToNew[weight.boneIndex];
                        if (mappedIndex < 0)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{source.name}' has a weighted influence assigned to a missing bone.");
                        }
                        weight.boneIndex = mappedIndex;
                        destinationWeights.Add(weight);
                        destinationCount++;
                    }
                    if (destinationCount == 0)
                    {
                        destinationWeights.Add(new BoneWeight1
                        {
                            boneIndex = fallbackBoneIndex,
                            weight = 1f
                        });
                        destinationCount = 1;
                    }
                    if (destinationCount > byte.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Skinned mesh '{source.name}' has more than {byte.MaxValue} bone influences on one vertex.");
                    }
                    destinationCounts[vertexIndex] = (byte)destinationCount;
                }
            }

            var sanitized = Object.Instantiate(source);
            sanitized.name = source.name + "__NaNAnimationSanitized";
            try
            {
                using (var counts = new NativeArray<byte>(destinationCounts, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(destinationWeights.ToArray(), Allocator.Temp))
                {
                    sanitized.SetBoneWeights(counts, weights);
                }
                sanitized.bindposes = finiteBindPoses.ToArray();

                for (var subMeshIndex = 0; subMeshIndex < sanitized.subMeshCount; subMeshIndex++)
                {
                    var topology = sanitized.GetTopology(subMeshIndex);
                    var primitiveSize = GetPrimitiveSize(topology, source.name);
                    var indices = sanitized.GetIndices(subMeshIndex);
                    if (indices.Length % primitiveSize != 0)
                    {
                        throw new InvalidOperationException(
                            $"Sub-mesh {subMeshIndex} on '{source.name}' has an invalid {topology} index count.");
                    }

                    var keptIndices = new List<int>(indices.Length);
                    for (var index = 0; index < indices.Length; index += primitiveSize)
                    {
                        var shouldCull = false;
                        for (var corner = 0; corner < primitiveSize; corner++)
                        {
                            var vertexIndex = indices[index + corner];
                            if (vertexIndex < 0 || vertexIndex >= culledVertices.Length)
                            {
                                throw new InvalidOperationException(
                                    $"Sub-mesh {subMeshIndex} on '{source.name}' has an invalid vertex index {vertexIndex}.");
                            }
                            shouldCull |= culledVertices[vertexIndex];
                        }
                        if (shouldCull)
                        {
                            culledPrimitiveCount++;
                            continue;
                        }
                        for (var corner = 0; corner < primitiveSize; corner++)
                        {
                            keptIndices.Add(indices[index + corner]);
                        }
                    }
                    sanitized.SetIndices(keptIndices, topology, subMeshIndex, false);
                }
                sanitized.RecalculateBounds();
                sanitizedBones = finiteBones.ToArray();
                return sanitized;
            }
            catch
            {
                Object.DestroyImmediate(sanitized);
                throw;
            }
        }

        private static int GetPrimitiveSize(MeshTopology topology, string meshName)
        {
            switch (topology)
            {
                case MeshTopology.Triangles:
                    return 3;
                case MeshTopology.Quads:
                    return 4;
                case MeshTopology.Lines:
                    return 2;
                case MeshTopology.Points:
                    return 1;
                default:
                    throw new InvalidOperationException(
                        $"Mesh '{meshName}' uses unsupported topology {topology} with NaNimation deletion bones.");
            }
        }

        private static bool IsNaNAnimationDeletionBone(Transform bone)
        {
            return bone != null &&
                   bone.parent != null &&
                   bone.name.StartsWith("NaNimatedBone", StringComparison.Ordinal) &&
                   bone.parent.name.StartsWith("NaNimation", StringComparison.Ordinal);
        }

        private static void RemoveNaNAnimationDeletionTransforms(GameObject root)
        {
            var referencedBones = new HashSet<Transform>(
                root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                    .SelectMany(renderer => renderer.bones)
                    .Where(bone => bone != null));
            var invalidTransforms = root.GetComponentsInChildren<Transform>(true)
                .Where(transform => !IsFinite(transform.localToWorldMatrix))
                .ToArray();

            foreach (var transform in invalidTransforms)
            {
                if (!IsNaNAnimationDeletionBone(transform))
                {
                    throw new InvalidOperationException(
                        $"Transform '{GetPath(transform, root.transform)}' contains a non-finite value.");
                }
                if (referencedBones.Contains(transform))
                {
                    throw new InvalidOperationException(
                        $"NaNimation deletion bone '{transform.name}' is still referenced after mesh conversion.");
                }
                if (transform.childCount != 0)
                {
                    throw new InvalidOperationException(
                        $"NaNimation deletion bone '{transform.name}' unexpectedly has child objects.");
                }
                Object.DestroyImmediate(transform.gameObject);
            }

            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                if (!IsFinite(transform.localToWorldMatrix))
                {
                    throw new InvalidOperationException(
                        $"Transform '{GetPath(transform, root.transform)}' still contains a non-finite value after NaNimation conversion.");
                }
            }
        }

        private static void ValidateFiniteMesh(Mesh mesh, SkinnedMeshRenderer renderer, string phase)
        {
            var vertices = mesh.vertices;
            for (var index = 0; index < vertices.Length; index++)
            {
                if (!IsFinite(vertices[index]))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{renderer.name}' produced a non-finite vertex at index {index} while baking {phase}.");
                }
            }

            var normals = mesh.normals;
            for (var index = 0; index < normals.Length; index++)
            {
                if (!IsFinite(normals[index]))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{renderer.name}' produced a non-finite normal at index {index} while baking {phase}.");
                }
            }

            var tangents = mesh.tangents;
            for (var index = 0; index < tangents.Length; index++)
            {
                if (!IsFinite(tangents[index]))
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{renderer.name}' produced a non-finite tangent at index {index} while baking {phase}.");
                }
            }
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(Vector4 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
        }

        private static bool IsFinite(Matrix4x4 value)
        {
            for (var index = 0; index < 16; index++)
            {
                if (!IsFinite(value[index]))
                {
                    return false;
                }
            }
            return true;
        }

        private static Matrix4x4 GetBakeMeshToRootMatrix(Transform rendererTransform, Transform avatarRoot)
        {
            var rendererWithoutScale = Matrix4x4.TRS(
                rendererTransform.position,
                rendererTransform.rotation,
                Vector3.one);
            var matrix = avatarRoot.worldToLocalMatrix * rendererWithoutScale;
            if (!IsFinite(matrix))
            {
                throw new InvalidOperationException(
                    $"Skinned mesh '{rendererTransform.name}' has a non-finite transform relative to the avatar root.");
            }
            return matrix;
        }

        private static void TransformMesh(Mesh mesh, Matrix4x4 matrix)
        {
            var vertices = mesh.vertices;
            for (var index = 0; index < vertices.Length; index++)
            {
                vertices[index] = matrix.MultiplyPoint3x4(vertices[index]);
            }
            mesh.vertices = vertices;

            var normals = mesh.normals;
            if (normals.Length == vertices.Length)
            {
                var normalMatrix = matrix.inverse.transpose;
                for (var index = 0; index < normals.Length; index++)
                {
                    normals[index] = normalMatrix.MultiplyVector(normals[index]).normalized;
                }
                mesh.normals = normals;
            }

            var tangents = mesh.tangents;
            if (tangents.Length == vertices.Length)
            {
                var handedness = matrix.determinant < 0f ? -1f : 1f;
                for (var index = 0; index < tangents.Length; index++)
                {
                    var tangent = matrix.MultiplyVector((Vector3)tangents[index]).normalized;
                    tangents[index] = new Vector4(tangent.x, tangent.y, tangent.z, tangents[index].w * handedness);
                }
                mesh.tangents = tangents;
            }

            mesh.RecalculateBounds();
        }

        private static void ValidateBoneWeights(Mesh mesh, int boneCount)
        {
            using (var counts = mesh.GetBonesPerVertex())
            using (var weights = mesh.GetAllBoneWeights())
            {
                if (counts.Length != mesh.vertexCount)
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{mesh.name}' has an invalid bones-per-vertex array.");
                }

                var cursor = 0;
                for (var vertexIndex = 0; vertexIndex < counts.Length; vertexIndex++)
                {
                    var totalWeight = 0f;
                    for (var influenceIndex = 0; influenceIndex < counts[vertexIndex]; influenceIndex++)
                    {
                        if (cursor >= weights.Length)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has a truncated bone-weight array.");
                        }
                        var weight = weights[cursor++];
                        if (!IsFinite(weight.weight) || weight.weight < 0f)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has an invalid bone weight at vertex {vertexIndex}.");
                        }
                        if (weight.boneIndex < 0 || weight.boneIndex >= boneCount)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has bone index {weight.boneIndex} outside 0..{boneCount - 1}.");
                        }
                        totalWeight += weight.weight;
                    }
                    if (!IsFinite(totalWeight) || totalWeight <= 0f)
                    {
                        throw new InvalidOperationException(
                            $"Skinned mesh '{mesh.name}' has no positive bone weight at vertex {vertexIndex}.");
                    }
                }

                if (cursor != weights.Length)
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{mesh.name}' has extra entries in its bone-weight array.");
                }
            }
        }

        private static HashSet<int> GetWeightedBoneIndices(Mesh mesh)
        {
            var indices = new HashSet<int>();
            using (var weights = mesh.GetAllBoneWeights())
            {
                for (var index = 0; index < weights.Length; index++)
                {
                    if (weights[index].weight > 0.000001f)
                    {
                        indices.Add(weights[index].boneIndex);
                    }
                }
            }
            return indices;
        }

        private static void CompactBoneData(
            Mesh source,
            IReadOnlyList<Transform> sourceBones,
            Transform avatarRoot,
            Mesh destination,
            out Transform[] compactBones)
        {
            var oldToNew = new int[sourceBones.Count];
            var bones = new List<Transform>(sourceBones.Count);
            var includedBones = new HashSet<Transform>();
            for (var oldIndex = 0; oldIndex < sourceBones.Count; oldIndex++)
            {
                var bone = sourceBones[oldIndex];
                if (bone == null)
                {
                    oldToNew[oldIndex] = -1;
                    continue;
                }

                oldToNew[oldIndex] = bones.Count;
                bones.Add(bone);
                includedBones.Add(bone);
            }

            if (bones.Count == 0)
            {
                throw new InvalidOperationException($"Skinned mesh '{source.name}' has no valid bones.");
            }

            var originalBoneCount = bones.Count;
            for (var boneIndex = 0; boneIndex < originalBoneCount; boneIndex++)
            {
                var ancestor = bones[boneIndex].parent;
                while (ancestor != null && ancestor != avatarRoot)
                {
                    if (includedBones.Add(ancestor))
                    {
                        bones.Add(ancestor);
                    }
                    ancestor = ancestor.parent;
                }
                if (ancestor == null && bones[boneIndex] != avatarRoot)
                {
                    throw new InvalidOperationException(
                        $"Bone '{bones[boneIndex].name}' used by mesh '{source.name}' is outside the avatar hierarchy.");
                }
            }

            var destinationCounts = new byte[source.vertexCount];
            var destinationWeights = new List<BoneWeight1>();
            using (var sourceCounts = source.GetBonesPerVertex())
            using (var sourceWeights = source.GetAllBoneWeights())
            {
                var cursor = 0;
                for (var vertexIndex = 0; vertexIndex < sourceCounts.Length; vertexIndex++)
                {
                    var destinationCount = 0;
                    for (var influenceIndex = 0; influenceIndex < sourceCounts[vertexIndex]; influenceIndex++)
                    {
                        var weight = sourceWeights[cursor++];
                        var mappedIndex = weight.boneIndex < oldToNew.Length
                            ? oldToNew[weight.boneIndex]
                            : -1;
                        if (mappedIndex < 0)
                        {
                            if (weight.weight > 0.000001f)
                            {
                                throw new InvalidOperationException(
                                    $"Skinned mesh '{source.name}' has a weighted influence assigned to a missing bone.");
                            }
                            continue;
                        }

                        weight.boneIndex = mappedIndex;
                        destinationWeights.Add(weight);
                        destinationCount++;
                    }

                    if (destinationCount > byte.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Skinned mesh '{source.name}' has more than {byte.MaxValue} bone influences on one vertex.");
                    }
                    destinationCounts[vertexIndex] = (byte)destinationCount;
                }
            }

            using (var counts = new NativeArray<byte>(destinationCounts, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(destinationWeights.ToArray(), Allocator.Temp))
            {
                destination.SetBoneWeights(counts, weights);
            }
            compactBones = bones.ToArray();
        }

        private static void SetAllBlendShapeWeights(SkinnedMeshRenderer renderer, float value)
        {
            for (var index = 0; index < renderer.sharedMesh.blendShapeCount; index++)
            {
                renderer.SetBlendShapeWeight(index, value);
            }
        }

        private static void RestoreBlendShapeWeights(SkinnedMeshRenderer renderer, IReadOnlyList<float> weights)
        {
            for (var index = 0; index < weights.Count; index++)
            {
                renderer.SetBlendShapeWeight(index, weights[index]);
            }
        }

        private static float CalculateBlendShapeReconstructionError(
            Mesh expected,
            Mesh bakedMesh,
            IReadOnlyList<float> weights)
        {
            if (expected.vertexCount != bakedMesh.vertexCount || bakedMesh.blendShapeCount != weights.Count)
            {
                return float.PositiveInfinity;
            }

            var expectedVertices = expected.vertices;
            var reconstructed = bakedMesh.vertices;
            var deltas = new Vector3[bakedMesh.vertexCount];
            var deltaNormals = new Vector3[bakedMesh.vertexCount];
            var deltaTangents = new Vector3[bakedMesh.vertexCount];

            for (var shapeIndex = 0; shapeIndex < bakedMesh.blendShapeCount; shapeIndex++)
            {
                var frameCount = bakedMesh.GetBlendShapeFrameCount(shapeIndex);
                if (frameCount == 0 || Mathf.Abs(weights[shapeIndex]) <= 0.000001f)
                {
                    continue;
                }

                var frameIndex = frameCount - 1;
                var frameWeight = bakedMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                if (Mathf.Abs(frameWeight) <= 0.000001f)
                {
                    continue;
                }

                bakedMesh.GetBlendShapeFrameVertices(
                    shapeIndex,
                    frameIndex,
                    deltas,
                    deltaNormals,
                    deltaTangents);
                var multiplier = weights[shapeIndex] / frameWeight;
                for (var vertexIndex = 0; vertexIndex < reconstructed.Length; vertexIndex++)
                {
                    reconstructed[vertexIndex] += deltas[vertexIndex] * multiplier;
                }
            }

            var maxError = 0f;
            for (var index = 0; index < expectedVertices.Length; index++)
            {
                maxError = Mathf.Max(maxError, Vector3.Distance(expectedVertices[index], reconstructed[index]));
            }
            return maxError;
        }

        private static float ValidateUnifiedBindPoses(GameObject root)
        {
            var inferredMatrices = new Dictionary<Transform, Matrix4x4>();
            var maxError = 0f;

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bindPoses = renderer.sharedMesh.bindposes;
                for (var index = 0; index < renderer.bones.Length; index++)
                {
                    var bone = renderer.bones[index];
                    var inferred = root.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix *
                                   bindPoses[index].inverse;
                    if (inferredMatrices.TryGetValue(bone, out var previous))
                    {
                        maxError = Mathf.Max(maxError, MatrixDifference(previous, inferred));
                    }
                    else
                    {
                        inferredMatrices.Add(bone, inferred);
                    }
                }
            }

            return maxError;
        }

        private static float MatrixDifference(Matrix4x4 left, Matrix4x4 right)
        {
            var max = 0f;
            for (var index = 0; index < 16; index++)
            {
                max = Mathf.Max(max, Mathf.Abs(left[index] - right[index]));
            }
            return max;
        }

        private static void RemoveAnimationDrivers(GameObject root)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                Object.DestroyImmediate(animator);
            }
            foreach (var animation in root.GetComponentsInChildren<Animation>(true))
            {
                Object.DestroyImmediate(animation);
            }
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (component is IConstraint)
                {
                    Object.DestroyImmediate(component);
                }
            }
        }

        private static string MakeUniqueCompatibleNames(GameObject root, out int renamedCount)
        {
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            renamedCount = 0;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                var original = transform.name;
                var baseName = MakeCompatibleName(original);
                var candidate = baseName;
                var suffix = 2;
                while (!usedNames.Add(candidate))
                {
                    candidate = baseName + "__" + suffix++;
                }
                if (!string.Equals(original, candidate, StringComparison.Ordinal))
                {
                    transform.name = candidate;
                    renamedCount++;
                }
            }
            return root.name;
        }

        private static string MakeCompatibleName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "_";
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }
                builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
            }

            var result = builder.ToString().Normalize(NormalizationForm.FormC);
            if (string.IsNullOrEmpty(result))
            {
                result = "_";
            }
            if (char.IsDigit(result[0]))
            {
                result = "_" + result;
            }
            return result;
        }

        private static void ApplyFbxPostProcess(
            string fbxPath,
            IReadOnlyList<BlendShapeWeightSet> weightSets,
            IReadOnlyList<RendererMaterialSet> materialSets,
            bool embedTextures)
        {
            var setsByNode = weightSets.ToDictionary(set => set.NodeName, StringComparer.Ordinal);
            var temporaryPath = fbxPath + ".postprocessed.tmp.fbx";
            var alignedBoneCount = 0;
            try
            {
                var manager = FbxManager.Create();
                var ioSettings = FbxIOSettings.Create(manager, Globals.IOSROOT);
                ioSettings.SetBoolProp(Globals.IMP_FBX_ANIMATION, false);
                ioSettings.SetBoolProp(Globals.EXP_FBX_MATERIAL, true);
                if (embedTextures)
                {
                    ioSettings.SetBoolProp(Globals.EXP_FBX_TEXTURE, true);
                    ioSettings.SetBoolProp(Globals.EXP_FBX_EMBEDDED, true);
                }
                manager.SetIOSettings(ioSettings);
                try
                {
                    var scene = FbxScene.Create(manager, "BlenderSafePostProcess");
                    try
                    {
                        using (var importer = FbxImporter.Create(manager, "BlenderSafeImporter"))
                        {
                            if (!importer.Initialize(fbxPath, -1, manager.GetIOSettings()) || !importer.Import(scene))
                            {
                                throw new InvalidOperationException("Failed to reopen the FBX for post-processing.");
                            }
                        }

                        alignedBoneCount = AlignBoneDefaultsToBindPoses(scene);

                        var matchedChannels = 0;
                        ApplyBlendShapeWeights(scene.GetRootNode(), setsByNode, ref matchedChannels);
                        var expectedChannels = weightSets.Sum(set => set.Weights.Count);
                        if (matchedChannels != expectedChannels)
                        {
                            throw new InvalidOperationException(
                                $"FBX BlendShape channel mismatch. Expected {expectedChannels}, matched {matchedChannels}.");
                        }

                        ApplyMaterialProperties(scene, materialSets);
                        if (embedTextures)
                        {
                            ApplyMaterialTextures(scene, materialSets);
                            ApplyMaterialProperties(scene, materialSets);
                        }

                        using (var exporter = Autodesk.Fbx.FbxExporter.Create(manager, "BlenderSafeExporter"))
                        {
                            if (!exporter.Initialize(temporaryPath, -1, manager.GetIOSettings()) || !exporter.Export(scene))
                            {
                                throw new InvalidOperationException("Failed to save the post-processed FBX.");
                            }
                        }
                    }
                    finally
                    {
                        scene.Destroy();
                    }
                }
                finally
                {
                    manager.Destroy();
                }

                ValidateSavedFbxBoneDefaults(temporaryPath, alignedBoneCount);
                File.Copy(temporaryPath, fbxPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static void RemoveExistingMaterialTextures(FbxScene scene)
        {
            var textures = new HashSet<FbxObject>();
            for (var materialIndex = 0; materialIndex < scene.GetMaterialCount(); materialIndex++)
            {
                var material = scene.GetMaterial(materialIndex);
                for (var property = material.GetFirstProperty(); property != null && property.IsValid(); property = material.GetNextProperty(property))
                {
                    for (var sourceIndex = property.GetSrcObjectCount() - 1; sourceIndex >= 0; sourceIndex--)
                    {
                        var source = property.GetSrcObject(sourceIndex);
                        var uvSet = source?.FindProperty("UVSet");
                        if (uvSet == null || !uvSet.IsValid())
                        {
                            continue;
                        }

                        property.DisconnectSrcObject(source);
                        textures.Add(source);
                    }
                }
            }

            var media = new HashSet<FbxObject>();
            foreach (var texture in textures)
            {
                for (var sourceIndex = texture.GetSrcObjectCount() - 1; sourceIndex >= 0; sourceIndex--)
                {
                    var source = texture.GetSrcObject(sourceIndex);
                    texture.DisconnectSrcObject(source);
                    media.Add(source);
                }
                texture.Destroy();
            }

            foreach (var source in media)
            {
                source.Destroy();
            }
        }

        private static void ApplyMaterialTextures(
            FbxScene scene,
            IReadOnlyList<RendererMaterialSet> rendererSets)
        {
            RemoveExistingMaterialTextures(scene);

            var rootNode = scene.GetRootNode();
            var processedMaterials = new HashSet<MaterialTextureSet>();
            var textureCache = new Dictionary<string, FbxFileTexture>(StringComparer.Ordinal);
            var textureIndex = 0;

            foreach (var rendererSet in rendererSets)
            {
                var node = rootNode.FindChild(rendererSet.NodeName, true);
                if (node == null)
                {
                    throw new InvalidOperationException(
                        $"FBX mesh node '{rendererSet.NodeName}' was not found while embedding textures.");
                }
                if (node.GetMaterialCount() < rendererSet.Materials.Count)
                {
                    throw new InvalidOperationException(
                        $"FBX node '{rendererSet.NodeName}' has {node.GetMaterialCount()} materials, " +
                        $"but Unity has {rendererSet.Materials.Count} material slots.");
                }

                for (var materialIndex = 0; materialIndex < rendererSet.Materials.Count; materialIndex++)
                {
                    var materialSet = rendererSet.Materials[materialIndex];
                    if (materialSet == null || !processedMaterials.Add(materialSet))
                    {
                        continue;
                    }

                    var fbxMaterial = node.GetMaterial(materialIndex);
                    if (fbxMaterial == null)
                    {
                        throw new InvalidOperationException(
                            $"FBX material slot {materialIndex} on '{rendererSet.NodeName}' is empty.");
                    }

                    SetMaterialMetadata(fbxMaterial, "UnityMaterialName", materialSet.UnityMaterialName);
                    SetMaterialMetadata(fbxMaterial, "UnityShaderName", materialSet.UnityShaderName);
                    foreach (var binding in materialSet.Bindings)
                    {
                        var texture = GetOrCreateFbxTexture(
                            scene,
                            binding,
                            textureCache,
                            ref textureIndex);

                        var customPropertyName = "UnityTexture_" + MakeCompatibleName(binding.UnityPropertyName);
                        var customProperty = GetOrCreateStringProperty(fbxMaterial, customPropertyName);
                        customProperty.Set(binding.UnityAssetPath);
                        customProperty.DisconnectAllSrcObject();
                        if (!texture.ConnectDstProperty(customProperty))
                        {
                            throw new InvalidOperationException(
                                $"Could not connect texture '{binding.UnityAssetPath}' to " +
                                $"'{materialSet.UnityMaterialName}.{binding.UnityPropertyName}'.");
                        }

                        if (!string.IsNullOrEmpty(binding.StandardFbxPropertyName))
                        {
                            var standardProperty = fbxMaterial.FindProperty(binding.StandardFbxPropertyName);
                            if (standardProperty == null || !standardProperty.IsValid())
                            {
                                continue;
                            }

                            var standardTexture = string.IsNullOrEmpty(binding.StandardSourceFullPath)
                                ? texture
                                : GetOrCreateFbxTexture(
                                    scene,
                                    binding,
                                    textureCache,
                                    ref textureIndex,
                                    binding.StandardSourceFullPath,
                                    binding.StandardEmbeddedFileName,
                                    "Standard");
                            standardProperty.DisconnectAllSrcObject();
                            if (!standardTexture.ConnectDstProperty(standardProperty))
                            {
                                throw new InvalidOperationException(
                                    $"Could not connect '{binding.UnityPropertyName}' to FBX property " +
                                    $"'{binding.StandardFbxPropertyName}'.");
                            }
                        }
                    }
                }
            }
        }

        private static FbxFileTexture GetOrCreateFbxTexture(
            FbxScene scene,
            TextureBinding binding,
            IDictionary<string, FbxFileTexture> textureCache,
            ref int textureIndex,
            string sourceFullPath = null,
            string embeddedFileName = null,
            string namePrefix = "UnityTexture")
        {
            sourceFullPath = sourceFullPath ?? binding.SourceFullPath;
            embeddedFileName = embeddedFileName ?? binding.EmbeddedFileName;
            var cacheKey = string.Join(
                "|",
                sourceFullPath,
                binding.Scale.x.ToString("R", CultureInfo.InvariantCulture),
                binding.Scale.y.ToString("R", CultureInfo.InvariantCulture),
                binding.Offset.x.ToString("R", CultureInfo.InvariantCulture),
                binding.Offset.y.ToString("R", CultureInfo.InvariantCulture),
                (int)binding.WrapModeU,
                (int)binding.WrapModeV);
            if (textureCache.TryGetValue(cacheKey, out var existingTexture))
            {
                return existingTexture;
            }

            var textureName = $"{namePrefix}_{textureIndex++}_{MakeCompatibleName(Path.GetFileNameWithoutExtension(embeddedFileName))}";
            var texture = FbxFileTexture.Create(scene, textureName);
            if (!texture.SetFileName(sourceFullPath))
            {
                throw new InvalidOperationException($"FBX rejected texture path '{sourceFullPath}'.");
            }
            texture.SetRelativeFileName(embeddedFileName);
            texture.SetTextureUse(FbxTexture.ETextureUse.eStandard);
            texture.SetMappingType(FbxTexture.EMappingType.eUV);
            texture.UVSet.Set("UVSet0");
            texture.SetScale(binding.Scale.x, binding.Scale.y);
            texture.SetTranslation(binding.Offset.x, binding.Offset.y);
            texture.SetWrapMode(ToFbxWrapMode(binding.WrapModeU), ToFbxWrapMode(binding.WrapModeV));
            textureCache.Add(cacheKey, texture);
            return texture;
        }

        private static FbxTexture.EWrapMode ToFbxWrapMode(TextureWrapMode wrapMode)
        {
            return wrapMode == TextureWrapMode.Clamp
                ? FbxTexture.EWrapMode.eClamp
                : FbxTexture.EWrapMode.eRepeat;
        }

        private static void SetMaterialMetadata(FbxSurfaceMaterial material, string propertyName, string value)
        {
            var property = GetOrCreateStringProperty(material, propertyName);
            property.Set(value ?? string.Empty);
        }

        private static FbxProperty GetOrCreateStringProperty(FbxSurfaceMaterial material, string propertyName)
        {
            var property = material.FindProperty(propertyName);
            if (property == null || !property.IsValid())
            {
                property = FbxProperty.Create(material, Globals.FbxStringDT, propertyName, propertyName);
            }
            property.ModifyFlag(FbxPropertyFlags.EFlags.eUserDefined, true);
            return property;
        }

        private static void ApplyBlendShapeWeights(
            FbxNode node,
            IReadOnlyDictionary<string, BlendShapeWeightSet> setsByNode,
            ref int matchedChannels)
        {
            if (node == null)
            {
                return;
            }

            var mesh = node.GetMesh();
            if (mesh != null && setsByNode.TryGetValue(node.GetName(), out var weightSet))
            {
                var channels = GetBlendShapeChannels(mesh);
                var used = new bool[channels.Count];
                for (var desiredIndex = 0; desiredIndex < weightSet.Weights.Count; desiredIndex++)
                {
                    var desired = weightSet.Weights[desiredIndex];
                    var channelIndex = -1;
                    for (var index = 0; index < channels.Count; index++)
                    {
                        if (!used[index] && string.Equals(channels[index].GetName(), desired.Name, StringComparison.Ordinal))
                        {
                            channelIndex = index;
                            break;
                        }
                    }
                    if (channelIndex < 0 && desiredIndex < channels.Count && !used[desiredIndex])
                    {
                        channelIndex = desiredIndex;
                    }
                    if (channelIndex < 0)
                    {
                        continue;
                    }

                    var property = channels[channelIndex].FindProperty("DeformPercent");
                    if (property != null && property.IsValid())
                    {
                        property.Set(desired.Weight);
                        used[channelIndex] = true;
                        matchedChannels++;
                    }
                }
            }

            for (var index = 0; index < node.GetChildCount(); index++)
            {
                ApplyBlendShapeWeights(node.GetChild(index), setsByNode, ref matchedChannels);
            }
        }

        private static List<FbxObject> GetBlendShapeChannels(FbxMesh mesh)
        {
            var channels = new List<FbxObject>();
            for (var sourceIndex = 0; sourceIndex < mesh.GetSrcObjectCount(); sourceIndex++)
            {
                var source = mesh.GetSrcObject(sourceIndex);
                var directProperty = source.FindProperty("DeformPercent");
                if (directProperty != null && directProperty.IsValid())
                {
                    channels.Add(source);
                }

                for (var childIndex = 0; childIndex < source.GetSrcObjectCount(); childIndex++)
                {
                    var child = source.GetSrcObject(childIndex);
                    var property = child.FindProperty("DeformPercent");
                    if (property != null && property.IsValid())
                    {
                        channels.Add(child);
                    }
                }
            }
            return channels;
        }

        private static void ImportIfProjectAsset(string fullPath)
        {
            var assetPath = TryGetAssetPath(fullPath);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);
        }

        private static string GetPath(Transform transform, Transform root)
        {
            if (transform == root)
            {
                return root.name;
            }

            var names = new List<string>();
            var current = transform;
            while (current != null && current != root)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
