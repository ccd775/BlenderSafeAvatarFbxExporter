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
        public const string Version = "0.2.0";

        private const string TestedFbxExporterVersion = "4.2.1";
        private const string TestedFbxSdkVersion = "4.2.1";
        private const string MinimumFbxExporterVersion = "4.1.0";
        private const string MinimumFbxSdkVersion = "4.1.0";

        // Bind-pose checks run in double precision on the FBX side, so they use their own
        // dimensionless budgets. The warn value is what a clean scene actually reaches; the fail
        // value is where the deviation starts to matter geometrically.
        private const double FbxMatrixWarnTolerance = 0.0000001;
        private const double FbxMatrixFailTolerance = 0.0001;
        private const double FbxShearWarnTolerance = 0.00000001;
        private const double FbxShearFailTolerance = 0.0001;

        private sealed class BlendShapeWeightSet
        {
            public string NodeName;
            public readonly List<BlendShapeWeight> Weights = new List<BlendShapeWeight>();
        }

        private struct BlendShapeWeight
        {
            public string Name;

            /// <summary>Value written to FBX DeformPercent, against a 100-weight target shape.</summary>
            public float Weight;

            /// <summary>Weight the Unity renderer carried before clamping and rescaling.</summary>
            public float SourceWeight;

            /// <summary>Frame weight the source mesh used for this channel.</summary>
            public float SourceFrameWeight;
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
            return Export(sourceAvatar, outputPath, new BlenderSafeFbxExportOptions
            {
                EmbedAllMaterialTextures = embedAllMaterialTextures,
                OverwriteExisting = overwriteExisting
            });
        }

        public static BlenderSafeFbxExportResult Export(
            GameObject sourceAvatar,
            string outputPath,
            BlenderSafeFbxExportOptions options)
        {
            options = options ?? new BlenderSafeFbxExportOptions();
            if (activeExport != null)
            {
                throw new InvalidOperationException("A Blender-Safe FBX export is already running.");
            }

            var context = new ExportContext
            {
                Options = options,
                Result = new BlenderSafeFbxExportResult()
            };
            activeExport = context;
            try
            {
                return ExportInternal(sourceAvatar, outputPath, options, context.Result);
            }
            finally
            {
                FlushSuppressedWarnings();
                activeExport = null;
            }
        }

        private static BlenderSafeFbxExportResult ExportInternal(
            GameObject sourceAvatar,
            string outputPath,
            BlenderSafeFbxExportOptions options,
            BlenderSafeFbxExportResult result)
        {
            var embedAllMaterialTextures = options.EmbedAllMaterialTextures;
            var overwriteExisting = options.OverwriteExisting;

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
                var separatedBoneRendererCount = SeparateNonNormalizableSkinnedMeshRenderers(clone);
                clone.name = MakeUniqueCompatibleNames(clone, out var renamedCount);
                clone.hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSaveInEditor;
                clone.transform.position = Vector3.zero;

                RemoveAnimationDrivers(clone);

                result.OutputPath = fullOutputPath;
                result.ValidationLevel = options.ValidationLevel;
                result.TransformCount = clone.GetComponentsInChildren<Transform>(true).Length;
                result.RenamedTransformCount = renamedCount;
                result.SeparatedBoneRendererCount = separatedBoneRendererCount;
                result.EmbeddedTextures = embedAllMaterialTextures;

                var materialSets = CollectMaterialTextures(
                    clone,
                    stagingDirectory,
                    result,
                    embedAllMaterialTextures);
                var weightSets = PrepareClone(clone, result, createdMeshes);
                RemoveNaNAnimationDeletionTransforms(clone);
                result.StandardizedBoneCount = StandardizeSkeletonScales(
                    clone,
                    out var skeletonNormalizationError,
                    out var skeletonReferenceLength);
                result.MaxSkeletonNormalizationError = skeletonNormalizationError;
                ReportGeometryDeviation(
                    "Skeleton scale normalization",
                    result.MaxSkeletonNormalizationError,
                    skeletonReferenceLength,
                    "Setting every bone to unit scale moved skinned vertices. Bone rotations and " +
                    "positions were restored exactly; only scale-compensated hierarchies can drift.");

                result.MaxBindPoseError = ValidateUnifiedBindPoses(clone, out var bindPoseReferenceLength);
                ReportGeometryDeviation(
                    "Bind-pose unification",
                    result.MaxBindPoseError,
                    bindPoseReferenceLength,
                    "Two renderers disagree about where a shared bone rests. Blender keeps one rest " +
                    "matrix per bone, so the mesh with the losing bind pose can import shifted.");

                result.AdjustedFbxControlPointCount = UniquifyBlendShapeControlPoints(
                    clone,
                    out var maxControlPointAdjustment,
                    out var controlPointReferenceLength);
                result.MaxFbxControlPointAdjustment = maxControlPointAdjustment;
                ReportGeometryDeviation(
                    "FBX control-point disambiguation",
                    result.MaxFbxControlPointAdjustment,
                    controlPointReferenceLength,
                    "Coincident vertices with different BlendShape deltas were separated so the FBX " +
                    "exporter cannot merge them. The nudge is applied to the base geometry.");

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
                // A renderer transform that also carries children (or acts as a bone) cannot be
                // normalized in place. SeparateNonNormalizableSkinnedMeshRenderers moves it onto a
                // dedicated child on the clone, so this no longer has to be the user's problem.
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
                    if (!string.IsNullOrEmpty(stagedPath))
                    {
                        result.EmbeddedTextureSourceBytes += File.Exists(sourceFullPath)
                            ? new FileInfo(sourceFullPath).Length
                            : new FileInfo(stagedPath).Length;
                    }
                }

                // A texture the FBX format cannot carry is skipped rather than fatal.
                if (string.IsNullOrEmpty(stagedPath))
                {
                    continue;
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
                result.MaxPoseBakeErrorRatio = Mathf.Max(result.MaxPoseBakeErrorRatio, report.PoseBakeErrorRatio);
                result.FlattenedBlendShapeChannelCount += report.FlattenedBlendShapeChannelCount;
            }

            // The gate is evaluated on the renderer with the worst *relative* deviation, so an
            // avatar authored at a different unit scale is judged the same way.
            var worst = result.Renderers
                .OrderByDescending(report => report.PoseBakeErrorRatio)
                .FirstOrDefault();

            // A structural mismatch is not a deviation to be budgeted; the rebuilt mesh simply is
            // not comparable to the source, so no validation level may wave it through.
            var structural = result.Renderers.FirstOrDefault(report => float.IsInfinity(report.MaxPoseBakeError));
            if (structural != null)
            {
                throw new InvalidOperationException(
                    structural.PoseBakeDiagnostic ??
                    $"Renderer '{structural.Name}' could not be rebuilt into a comparable mesh.");
            }

            if (worst != null && worst.MaxPoseBakeError > 0f)
            {
                var affected = string.Join(
                    ", ",
                    result.Renderers
                        .Where(report => report.PoseBakeErrorRatio >= worst.PoseBakeErrorRatio * 0.1f)
                        .OrderByDescending(report => report.PoseBakeErrorRatio)
                        .Take(8)
                        .Select(report => $"{report.Name}={report.MaxPoseBakeError:E3}"));

                var detail = new List<string> { $"Affected renderers: {affected}." };
                if (!string.IsNullOrEmpty(worst.PoseBakeDiagnostic))
                {
                    detail.Add(worst.PoseBakeDiagnostic);
                }
                var environment = DescribeBlendShapeEnvironment(weightSets);
                if (!string.IsNullOrEmpty(environment))
                {
                    detail.Add(environment);
                }
                if (worst.MaxUnrenderedPoseBakeError > worst.MaxPoseBakeError)
                {
                    detail.Add(
                        $"Control points that no triangle references drift up to " +
                        $"{worst.MaxUnrenderedPoseBakeError:E3}; those are not measured by this gate.");
                }

                ReportGeometryDeviation(
                    "Pose baking",
                    worst.MaxPoseBakeError,
                    GetBoundsDiagonal(worst.BoundsSize),
                    "The exported base geometry and BlendShape targets are exact. Only the shape at " +
                    "the recorded default BlendShape weights differs from the Unity preview, so the " +
                    "avatar re-imports correctly once those weights are re-applied.",
                    string.Join("\n", detail));
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
                var flattenedChannels = 0;
                var exportedShapeNames = new HashSet<string>(StringComparer.Ordinal);
                for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
                {
                    var shapeName = sourceMesh.GetBlendShapeName(shapeIndex);
                    var sourceFrameCount = sourceMesh.GetBlendShapeFrameCount(shapeIndex);
                    if (sourceFrameCount == 0)
                    {
                        // Skipped entirely so the exported channel list stays aligned with the
                        // recorded weights.
                        Warn($"BlendShape '{shapeName}' on '{renderer.name}' has no frames and was skipped.");
                        continue;
                    }

                    // Mesh.AddBlendShapeFrame appends to an existing channel when the name repeats,
                    // which would silently merge two shapes into one.
                    if (!exportedShapeNames.Add(shapeName))
                    {
                        var uniqueName = shapeName;
                        var suffix = 2;
                        while (!exportedShapeNames.Add(uniqueName))
                        {
                            uniqueName = shapeName + "__" + suffix++;
                        }
                        Warn(
                            $"BlendShape '{shapeName}' on '{renderer.name}' is defined more than once; " +
                            $"the duplicate was exported as '{uniqueName}'.");
                        shapeName = uniqueName;
                    }

                    var sourceFrameWeight = sourceMesh.GetBlendShapeFrameWeight(
                        shapeIndex,
                        sourceFrameCount - 1);
                    if (sourceFrameCount > 1)
                    {
                        // Blender's FBX importer keeps one target shape per channel, so in-between
                        // frames are flattened onto the full-weight frame instead of refusing the
                        // export. Any resulting drift is measured by the pose-bake deviation.
                        flattenedChannels++;
                        Warn(
                            $"BlendShape '{shapeName}' on '{renderer.name}' has {sourceFrameCount} " +
                            "in-between frames. Blender keeps one target shape per channel, so only " +
                            "the full-weight frame was exported.");
                    }

                    // A single-frame channel is linear in its weight, so the target shape is
                    // measured at the normalized full weight: that stays well conditioned even when
                    // the source frame weight is tiny. A flattened in-between channel is measured at
                    // its own last frame instead, so the exported target is the authored full shape
                    // rather than an extrapolation past it.
                    var measureWeight = sourceFrameCount > 1
                        ? sourceFrameWeight
                        : NormalizedBlendShapeFrameWeight;
                    if (PlayerSettings.legacyClampBlendShapeWeights)
                    {
                        measureWeight = Mathf.Clamp(measureWeight, 0f, NormalizedBlendShapeFrameWeight);
                    }
                    if (Mathf.Abs(measureWeight) <= 0.000001f)
                    {
                        Warn(
                            $"BlendShape '{shapeName}' on '{renderer.name}' has a zero frame weight " +
                            "and was exported as an empty channel.");
                        measureWeight = NormalizedBlendShapeFrameWeight;
                    }

                    renderer.SetBlendShapeWeight(shapeIndex, measureWeight);
                    renderer.BakeMesh(frameMesh, false);
                    ValidateFiniteMesh(frameMesh, renderer, $"BlendShape '{shapeName}'");
                    TransformMesh(frameMesh, rendererToRoot);
                    ValidateFiniteMesh(frameMesh, renderer, $"transformed BlendShape '{shapeName}'");
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
                        shapeName,
                        NormalizedBlendShapeFrameWeight,
                        deltaVertices,
                        deltaNormals,
                        deltaTangents);
                    frameTotal++;

                    // DeformPercent is expressed against the normalized 100-weight target, so the
                    // Unity weight is rescaled by whatever weight the target was measured at.
                    var effectiveWeight = ToRecordedBlendShapeWeight(
                        originalWeights[shapeIndex],
                        shapeName,
                        renderer.name);
                    weightSet.Weights.Add(new BlendShapeWeight
                    {
                        Name = shapeName,
                        Weight = effectiveWeight * (NormalizedBlendShapeFrameWeight / measureWeight),
                        SourceWeight = originalWeights[shapeIndex],
                        SourceFrameWeight = sourceFrameWeight
                    });
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
                report.FlattenedBlendShapeChannelCount = flattenedChannels;

                var deviation = MeasureBlendShapeReconstruction(
                    referenceMesh,
                    renderer.sharedMesh,
                    weightSet.Weights,
                    renderer.name);
                report.MaxPoseBakeError = deviation.MaxError;
                report.MaxUnrenderedPoseBakeError = deviation.MaxUnrenderedError;
                report.PoseBakeWorstVertexIndex = deviation.WorstVertexIndex;
                report.PoseBakeErrorRatio = ToDeviationRatio(
                    deviation.MaxError,
                    GetBoundsDiagonal(report.BoundsSize));
                report.PoseBakeDiagnostic = deviation.Diagnostic;
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
                var degenerateVertices = 0;
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
                        if (!IsFinite(weight.weight))
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has a non-finite bone weight at vertex {vertexIndex}.");
                        }
                        if (weight.boneIndex < 0 || weight.boneIndex >= boneCount)
                        {
                            throw new InvalidOperationException(
                                $"Skinned mesh '{mesh.name}' has bone index {weight.boneIndex} outside 0..{boneCount - 1}.");
                        }
                        if (weight.weight < 0f)
                        {
                            degenerateVertices++;
                        }
                        totalWeight += weight.weight;
                    }

                    // A vertex with no positive influence collapses in Unity too. The bake
                    // reproduces whatever Unity shows, so this is reported rather than fatal.
                    if (totalWeight <= 0f)
                    {
                        degenerateVertices++;
                    }
                }

                if (cursor != weights.Length)
                {
                    throw new InvalidOperationException(
                        $"Skinned mesh '{mesh.name}' has extra entries in its bone-weight array.");
                }
                if (degenerateVertices > 0)
                {
                    Warn(
                        $"Skinned mesh '{mesh.name}' has {degenerateVertices} vertices with negative or " +
                        "zero total bone weight. They are exported exactly as Unity displays them.");
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
            var unweightedVertices = 0;
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

                    // The rebuilt bind pose equals the current pose, so pinning an unweighted
                    // vertex to any bone leaves it exactly where the bake put it. Without this the
                    // mesh would carry a zero-influence vertex that FBX cannot express.
                    if (destinationCount == 0)
                    {
                        destinationWeights.Add(new BoneWeight1 { boneIndex = 0, weight = 1f });
                        destinationCount = 1;
                        unweightedVertices++;
                    }
                    if (destinationCount > byte.MaxValue)
                    {
                        throw new InvalidOperationException(
                            $"Skinned mesh '{source.name}' has more than {byte.MaxValue} bone influences on one vertex.");
                    }
                    destinationCounts[vertexIndex] = (byte)destinationCount;
                }
            }

            if (unweightedVertices > 0)
            {
                Warn(
                    $"Skinned mesh '{source.name}' has {unweightedVertices} vertices with no usable bone " +
                    "influence. They were pinned to their baked rest position.");
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

        /// <summary>
        /// Returns the largest disagreement between renderers about where a shared bone rests.
        /// The rotation block is unitless while the translation column scales with the avatar, so
        /// <paramref name="referenceLength"/> reports the scale the caller should judge it against.
        /// </summary>
        private static float ValidateUnifiedBindPoses(GameObject root, out float referenceLength)
        {
            var inferredMatrices = new Dictionary<Transform, Matrix4x4>();
            var maxLinearError = 0f;
            var maxTranslationError = 0f;
            var maxTranslation = 1f;

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var bindPoses = renderer.sharedMesh.bindposes;
                for (var index = 0; index < renderer.bones.Length; index++)
                {
                    var bone = renderer.bones[index];
                    var inferred = root.transform.worldToLocalMatrix *
                                   renderer.transform.localToWorldMatrix *
                                   bindPoses[index].inverse;
                    var translation = inferred.GetColumn(3);
                    maxTranslation = Mathf.Max(
                        maxTranslation,
                        Mathf.Max(Mathf.Abs(translation.x), Mathf.Max(Mathf.Abs(translation.y), Mathf.Abs(translation.z))));

                    if (inferredMatrices.TryGetValue(bone, out var previous))
                    {
                        MatrixDifference(previous, inferred, out var linear, out var translationError);
                        maxLinearError = Mathf.Max(maxLinearError, linear);
                        maxTranslationError = Mathf.Max(maxTranslationError, translationError);
                    }
                    else
                    {
                        inferredMatrices.Add(bone, inferred);
                    }
                }
            }

            referenceLength = maxTranslation;
            // Both halves are expressed in the same units before they are combined: the rotation
            // block is scaled up to the avatar size so one budget covers the whole matrix.
            return Mathf.Max(maxLinearError * maxTranslation, maxTranslationError);
        }

        private static void MatrixDifference(
            Matrix4x4 left,
            Matrix4x4 right,
            out float maxLinearDifference,
            out float maxTranslationDifference)
        {
            maxLinearDifference = 0f;
            maxTranslationDifference = 0f;
            for (var column = 0; column < 4; column++)
            {
                for (var row = 0; row < 4; row++)
                {
                    var difference = Mathf.Abs(left[row, column] - right[row, column]);
                    if (column == 3 && row < 3)
                    {
                        maxTranslationDifference = Mathf.Max(maxTranslationDifference, difference);
                    }
                    else
                    {
                        maxLinearDifference = Mathf.Max(maxLinearDifference, difference);
                    }
                }
            }
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
                        if (matchedChannels < expectedChannels)
                        {
                            // Unmatched channels keep whatever weight Unity's exporter wrote, which
                            // is a wrong default value rather than broken geometry.
                            if (matchedChannels == 0 && expectedChannels > 0)
                            {
                                throw new InvalidOperationException(
                                    $"None of the {expectedChannels} BlendShape channels could be located in the " +
                                    "FBX, so no current weight could be restored.");
                            }
                            Warn(
                                $"{expectedChannels - matchedChannels} of {expectedChannels} BlendShape channels " +
                                "could not be located in the FBX; their current weights were left at the " +
                                "value Unity's FBX exporter wrote.");
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
                    Warn(
                        $"FBX mesh node '{rendererSet.NodeName}' was not found while embedding textures; " +
                        "its textures were skipped.");
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

                    SetMaterialMetadata(fbxMaterial, "UnityMaterialName", materialSet.UnityMaterialName);
                    SetMaterialMetadata(fbxMaterial, "UnityShaderName", materialSet.UnityShaderName);
                    foreach (var binding in materialSet.Bindings)
                    {
                        var texture = GetOrCreateFbxTexture(
                            scene,
                            binding,
                            textureCache,
                            ref textureIndex);
                        if (texture == null)
                        {
                            continue;
                        }

                        var customPropertyName = "UnityTexture_" + MakeCompatibleName(binding.UnityPropertyName);
                        var customProperty = GetOrCreateStringProperty(fbxMaterial, customPropertyName);
                        customProperty.Set(binding.UnityAssetPath);
                        customProperty.DisconnectAllSrcObject();
                        if (!texture.ConnectDstProperty(customProperty))
                        {
                            Warn(
                                $"Could not connect texture '{binding.UnityAssetPath}' to " +
                                $"'{materialSet.UnityMaterialName}.{binding.UnityPropertyName}'; skipped.");
                            continue;
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
                            if (standardTexture == null)
                            {
                                continue;
                            }
                            standardProperty.DisconnectAllSrcObject();
                            if (!standardTexture.ConnectDstProperty(standardProperty))
                            {
                                Warn(
                                    $"Could not connect '{binding.UnityPropertyName}' to FBX property " +
                                    $"'{binding.StandardFbxPropertyName}'; skipped.");
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
                Warn($"FBX rejected texture path '{sourceFullPath}'; the binding was skipped.");
                return null;
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
