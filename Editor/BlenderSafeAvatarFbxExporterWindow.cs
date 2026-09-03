using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ccd775.AvatarFbxExporter
{
    [Serializable]
    public sealed class BlenderSafeFbxExportResult
    {
        public string OutputPath;
        public int TransformCount;
        public int RenamedTransformCount;
        public int SeparatedBoneRendererCount;
        public int SkinnedRendererCount;
        public int VertexCount;
        public int BlendShapeCount;
        public int BlendShapeFrameCount;
        public int CulledNaNAnimationVertexCount;
        public int CulledNaNAnimationPrimitiveCount;
        public int AdjustedFbxControlPointCount;
        public float MaxFbxControlPointAdjustment;
        public int PreservedNonZeroBlendShapeWeights;
        public bool EmbeddedTextures;
        public int EmbeddedTextureBindingCount;
        public int EmbeddedTextureFileCount;
        public long EmbeddedTextureSourceBytes;
        public float MaxPoseBakeError;

        /// <summary>Worst pose-bake deviation as a fraction of the renderer's own size.</summary>
        public float MaxPoseBakeErrorRatio;

        public int FlattenedBlendShapeChannelCount;
        public int StandardizedBoneCount;
        public float MaxSkeletonNormalizationError;
        public float MaxBindPoseError;
        public BlenderSafeFbxValidationLevel ValidationLevel;

        /// <summary>Non-fatal problems found during the export. Also written to the console.</summary>
        public List<string> Warnings = new List<string>();

        public List<BlenderSafeRendererReport> Renderers = new List<BlenderSafeRendererReport>();
    }

    [Serializable]
    public sealed class BlenderSafeRendererReport
    {
        public string Name;
        public int VertexCount;
        public int BlendShapeCount;
        public int BlendShapeFrameCount;
        public int FlattenedBlendShapeChannelCount;
        public int CulledNaNAnimationVertexCount;
        public int CulledNaNAnimationPrimitiveCount;
        public Vector3 BoundsCenter;
        public Vector3 BoundsSize;

        /// <summary>Worst deviation over control points that a triangle actually references.</summary>
        public float MaxPoseBakeError;

        /// <summary>Worst deviation over control points no triangle references.</summary>
        public float MaxUnrenderedPoseBakeError;

        public float PoseBakeErrorRatio;
        public int PoseBakeWorstVertexIndex = -1;
        public string PoseBakeDiagnostic;
    }

    public sealed class BlenderSafeAvatarFbxExporterWindow : EditorWindow
    {
        private const string LastDirectoryKey = "ccd775.BlenderSafeAvatarFbxExporter.LastDirectory";

        [SerializeField] private GameObject sourceAvatar;
        [SerializeField] private bool embedAllMaterialTextures = true;
        [SerializeField] private BlenderSafeFbxValidationLevel validationLevel =
            BlenderSafeFbxValidationLevel.Balanced;
        [SerializeField] private bool showAdvanced;

        [MenuItem("Tools/Avatar/Blender-Safe FBX Exporter")]
        private static void OpenWindow()
        {
            var window = GetWindow<BlenderSafeAvatarFbxExporterWindow>("Blender-Safe FBX");
            if (Selection.activeGameObject != null)
            {
                window.sourceAvatar = Selection.activeGameObject;
            }
            window.minSize = new Vector2(430f, 230f);
            window.Show();
        }

        [MenuItem("GameObject/Avatar/Export Blender-Safe FBX...", false, 49)]
        private static void ExportSelected()
        {
            ExportWithDialog(Selection.activeGameObject, true, BlenderSafeFbxValidationLevel.Balanced);
        }

        [MenuItem("GameObject/Avatar/Export Blender-Safe FBX...", true)]
        private static bool ValidateExportSelected()
        {
            return Selection.activeGameObject != null &&
                   Selection.activeGameObject.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        private void OnSelectionChange()
        {
            if (Selection.activeGameObject != null)
            {
                sourceAvatar = Selection.activeGameObject;
                Repaint();
            }
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Blender-Safe Avatar FBX Exporter", EditorStyles.boldLabel);
            EditorGUILayout.Space(4f);
            sourceAvatar = (GameObject)EditorGUILayout.ObjectField(
                "Manual Bake Root / Prefab", sourceAvatar, typeof(GameObject), true);
            embedAllMaterialTextures = EditorGUILayout.ToggleLeft(
                "Embed all material textures", embedAllMaterialTextures);

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "Advanced", true);
            if (showAdvanced)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    validationLevel = (BlenderSafeFbxValidationLevel)EditorGUILayout.EnumPopup(
                        new GUIContent(
                            "Validation",
                            "How large a geometric deviation aborts the export. Every level measures " +
                            "and reports the same numbers."),
                        validationLevel);
                    EditorGUILayout.LabelField(" ", DescribeValidationLevel(validationLevel), EditorStyles.wordWrappedMiniLabel);
                }
            }

            EditorGUILayout.Space(8f);
            EditorGUILayout.HelpBox(
                "Bakes the current bone transforms into one Blender-compatible rest pose while preserving skinning, " +
                "BlendShapes, and their current weights. Optional texture embedding collects every material texture. " +
                "Only a temporary clone is modified; the selected avatar is not changed.",
                MessageType.Info);
            EditorGUILayout.HelpBox(
                "Select a Modular Avatar Manual Bake result, not the original avatar with Merge Armature components. " +
                "Unity shaders are approximated with standard FBX material channels and are not reproduced exactly in Blender.",
                MessageType.Warning);

            EditorGUILayout.Space(8f);
            using (new EditorGUI.DisabledScope(sourceAvatar == null ||
                                               sourceAvatar.GetComponentInChildren<SkinnedMeshRenderer>(true) == null))
            {
                if (GUILayout.Button("Export Blender-Safe FBX...", GUILayout.Height(32f)))
                {
                    ExportWithDialog(sourceAvatar, embedAllMaterialTextures, validationLevel);
                }
            }
        }

        private static string DescribeValidationLevel(BlenderSafeFbxValidationLevel level)
        {
            switch (level)
            {
                case BlenderSafeFbxValidationLevel.Strict:
                    return "Aborts on almost any measurable deviation. Matches 0.1.x behaviour.";
                case BlenderSafeFbxValidationLevel.ReportOnly:
                    return "Never aborts on a geometric deviation; structural faults still do.";
                default:
                    return "Reports small deviations and aborts only on a visible one. Recommended.";
            }
        }

        private static void ExportWithDialog(
            GameObject source,
            bool embedAllTextures,
            BlenderSafeFbxValidationLevel validation)
        {
            if (source == null)
            {
                EditorUtility.DisplayDialog("Blender-Safe FBX", "Select a Manual Bake avatar first.", "OK");
                return;
            }

            var defaultDirectory = EditorPrefs.GetString(
                LastDirectoryKey,
                Directory.GetParent(Application.dataPath).FullName);
            if (!Directory.Exists(defaultDirectory))
            {
                defaultDirectory = Directory.GetParent(Application.dataPath).FullName;
            }

            var defaultName = BlenderSafeAvatarFbxExporter.MakeSafeFileName(source.name + "_BlenderSafe");
            var path = EditorUtility.SaveFilePanel(
                "Export Blender-Safe Avatar FBX",
                defaultDirectory,
                defaultName,
                "fbx");
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            if (File.Exists(path) && !EditorUtility.DisplayDialog(
                    "Overwrite FBX?",
                    "The target already exists. The FBX will be replaced only after the new file passes validation. " +
                    "An existing Unity .meta/GUID is preserved.",
                    "Overwrite",
                    "Cancel"))
            {
                return;
            }

            EditorPrefs.SetString(LastDirectoryKey, Path.GetDirectoryName(path));

            try
            {
                var result = BlenderSafeAvatarFbxExporter.Export(source, path, new BlenderSafeFbxExportOptions
                {
                    EmbedAllMaterialTextures = embedAllTextures,
                    OverwriteExisting = true,
                    ValidationLevel = validation
                });
                var assetPath = BlenderSafeAvatarFbxExporter.TryGetAssetPath(result.OutputPath);
                if (!string.IsNullOrEmpty(assetPath))
                {
                    Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(assetPath);
                }

                EditorUtility.DisplayDialog(
                    "Blender-Safe FBX Exported",
                    $"Exported: {result.OutputPath}\n\n" +
                    $"Skinned meshes: {result.SkinnedRendererCount}\n" +
                    $"Vertices: {result.VertexCount}\n" +
                    $"BlendShapes: {result.BlendShapeCount}\n" +
                    $"Separated mesh/bone nodes: {result.SeparatedBoneRendererCount}\n" +
                    $"Disambiguated FBX control points: {result.AdjustedFbxControlPointCount} " +
                    $"(max {result.MaxFbxControlPointAdjustment:E3})\n" +
                    $"Embedded textures: {(result.EmbeddedTextures ? result.EmbeddedTextureFileCount.ToString() : "Off")}" +
                    (result.EmbeddedTextures
                        ? $" files / {result.EmbeddedTextureBindingCount} bindings\n"
                        : "\n") +
                    $"Standardized bones: {result.StandardizedBoneCount}\n" +
                    (result.CulledNaNAnimationPrimitiveCount > 0
                        ? $"NaNimation deletion: {result.CulledNaNAnimationVertexCount} vertices / " +
                          $"{result.CulledNaNAnimationPrimitiveCount} primitives culled\n"
                        : string.Empty) +
                    (result.FlattenedBlendShapeChannelCount > 0
                        ? $"Flattened in-between channels: {result.FlattenedBlendShapeChannelCount}\n"
                        : string.Empty) +
                    $"Pose bake max error: {result.MaxPoseBakeError:E3} ({result.MaxPoseBakeErrorRatio:P3} of mesh size)\n" +
                    $"Skeleton normalization max error: {result.MaxSkeletonNormalizationError:E3}\n" +
                    $"Bind pose max error: {result.MaxBindPoseError:E3}" +
                    DescribeWarnings(result),
                    "OK");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Blender-Safe FBX Export Failed", exception.Message, "OK");
            }
        }

        private static string DescribeWarnings(BlenderSafeFbxExportResult result)
        {
            if (result.Warnings == null || result.Warnings.Count == 0)
            {
                return string.Empty;
            }

            var shown = Mathf.Min(result.Warnings.Count, 3);
            var text = $"\n\n{result.Warnings.Count} warning(s) — see the console:\n";
            for (var index = 0; index < shown; index++)
            {
                var line = result.Warnings[index].Replace("\n", " ");
                if (line.Length > 160)
                {
                    line = line.Substring(0, 157) + "...";
                }
                text += "• " + line + "\n";
            }
            return text;
        }
    }
}
