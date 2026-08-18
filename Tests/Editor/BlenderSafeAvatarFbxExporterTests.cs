using System;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Fbx;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ccd775.AvatarFbxExporter.Tests
{
    public sealed class BlenderSafeAvatarFbxExporterTests
    {
        [Test]
        public void ExportPreservesManualPoseBlendShapeWeightAndGeneratedTexture()
        {
            using (var fixture = new SyntheticAvatarFixture())
            {
                var initialRotation = fixture.Bone.localRotation;
                var result = BlenderSafeAvatarFbxExporter.Export(
                    fixture.Root,
                    fixture.OutputPath,
                    true,
                    false);

                Assert.That(File.Exists(fixture.OutputPath), Is.True);
                Assert.That(IsBinaryFbx(fixture.OutputPath), Is.True);
                Assert.That(result.SkinnedRendererCount, Is.EqualTo(1));
                Assert.That(result.BlendShapeCount, Is.EqualTo(1));
                Assert.That(result.PreservedNonZeroBlendShapeWeights, Is.EqualTo(1));
                Assert.That(result.EmbeddedTextureFileCount, Is.GreaterThanOrEqualTo(1));
                Assert.That(result.EmbeddedTextureBindingCount, Is.EqualTo(1));
                Assert.That(fixture.Renderer.GetBlendShapeWeight(0), Is.EqualTo(35f));
                Assert.That(Quaternion.Angle(fixture.Bone.localRotation, initialRotation), Is.LessThan(0.0001f));

                ReadFbxSummary(
                    fixture.OutputPath,
                    out var meshCount,
                    out var channelCount,
                    out var manualShapeWeight);
                Assert.That(meshCount, Is.EqualTo(1));
                Assert.That(channelCount, Is.EqualTo(1));
                Assert.That(manualShapeWeight, Is.EqualTo(35f).Within(0.0001f));
            }
        }

        [Test]
        public void ExportRefusesUnconfirmedOverwrite()
        {
            using (var fixture = new SyntheticAvatarFixture())
            {
                var sentinel = Encoding.UTF8.GetBytes("existing-file");
                File.WriteAllBytes(fixture.OutputPath, sentinel);

                Assert.Throws<IOException>(() => BlenderSafeAvatarFbxExporter.Export(
                    fixture.Root,
                    fixture.OutputPath,
                    false,
                    false));
                Assert.That(File.ReadAllBytes(fixture.OutputPath), Is.EqualTo(sentinel));
            }
        }

        [Test]
        public void ExportReplacesExistingFileAfterExplicitConfirmation()
        {
            using (var fixture = new SyntheticAvatarFixture())
            {
                File.WriteAllText(fixture.OutputPath, "existing-file");
                BlenderSafeAvatarFbxExporter.Export(
                    fixture.Root,
                    fixture.OutputPath,
                    false,
                    true);

                Assert.That(IsBinaryFbx(fixture.OutputPath), Is.True);
                Assert.That(Directory.GetFiles(
                    fixture.OutputDirectory,
                    ".SyntheticAvatar.fbx.*.backup"), Is.Empty);
            }
        }

        [Test]
        public void ExportAcceptsPrefabAssetRoot()
        {
            using (var fixture = new SyntheticAvatarFixture())
            {
                var prefabPath = fixture.CreatePrefabAsset();
                var prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var result = BlenderSafeAvatarFbxExporter.Export(
                    prefabRoot,
                    fixture.OutputPath,
                    false,
                    false);

                Assert.That(result.SkinnedRendererCount, Is.EqualTo(1));
                Assert.That(result.BlendShapeCount, Is.EqualTo(1));
                Assert.That(IsBinaryFbx(fixture.OutputPath), Is.True);
            }
        }

        private static bool IsBinaryFbx(string path)
        {
            var header = File.ReadAllBytes(path).Take(23).ToArray();
            return Encoding.ASCII.GetString(header).StartsWith("Kaydara FBX Binary", StringComparison.Ordinal);
        }

        private static void ReadFbxSummary(
            string path,
            out int meshCount,
            out int channelCount,
            out float manualShapeWeight)
        {
            meshCount = 0;
            channelCount = 0;
            manualShapeWeight = float.NaN;

            var manager = FbxManager.Create();
            var ioSettings = FbxIOSettings.Create(manager, Globals.IOSROOT);
            manager.SetIOSettings(ioSettings);
            try
            {
                var scene = FbxScene.Create(manager, "TestValidation");
                try
                {
                    using (var importer = FbxImporter.Create(manager, "TestImporter"))
                    {
                        Assert.That(importer.Initialize(path, -1, manager.GetIOSettings()), Is.True);
                        Assert.That(importer.Import(scene), Is.True);
                    }

                    var localMeshCount = 0;
                    var localChannelCount = 0;
                    var localManualShapeWeight = float.NaN;
                    Visit(scene.GetRootNode(), node =>
                    {
                        var mesh = node.GetMesh();
                        if (mesh == null)
                        {
                            return;
                        }

                        localMeshCount++;
                        for (var sourceIndex = 0; sourceIndex < mesh.GetSrcObjectCount(); sourceIndex++)
                        {
                            var source = mesh.GetSrcObject(sourceIndex);
                            for (var childIndex = 0; childIndex < source.GetSrcObjectCount(); childIndex++)
                            {
                                var child = source.GetSrcObject(childIndex);
                                var property = child.FindProperty("DeformPercent");
                                if (property == null || !property.IsValid())
                                {
                                    continue;
                                }
                                localChannelCount++;
                                if (string.Equals(child.GetName(), "ManualShape", StringComparison.Ordinal))
                                {
                                    localManualShapeWeight = property.GetFloat();
                                }
                            }
                        }
                    });
                    meshCount = localMeshCount;
                    channelCount = localChannelCount;
                    manualShapeWeight = localManualShapeWeight;
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
        }

        private static void Visit(FbxNode node, Action<FbxNode> visitor)
        {
            if (node == null)
            {
                return;
            }
            visitor(node);
            for (var childIndex = 0; childIndex < node.GetChildCount(); childIndex++)
            {
                Visit(node.GetChild(childIndex), visitor);
            }
        }

        private sealed class SyntheticAvatarFixture : IDisposable
        {
            public readonly GameObject Root;
            public readonly Transform Bone;
            public readonly SkinnedMeshRenderer Renderer;
            public readonly string OutputDirectory;
            public readonly string OutputPath;

            private readonly Mesh mesh;
            private readonly Material material;
            private readonly Texture2D texture;
            private string generatedAssetFolder;

            public SyntheticAvatarFixture()
            {
                OutputDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ccd775",
                    "BlenderSafeAvatarFbxExporterTests",
                    Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(OutputDirectory);
                OutputPath = Path.Combine(OutputDirectory, "SyntheticAvatar.fbx");

                Root = new GameObject("SyntheticAvatar") { hideFlags = HideFlags.HideAndDontSave };
                var boneObject = new GameObject("Bone") { hideFlags = HideFlags.HideAndDontSave };
                boneObject.transform.SetParent(Root.transform, false);
                Bone = boneObject.transform;

                var meshObject = new GameObject("Body") { hideFlags = HideFlags.HideAndDontSave };
                meshObject.transform.SetParent(Root.transform, false);
                Renderer = meshObject.AddComponent<SkinnedMeshRenderer>();

                mesh = new Mesh { name = "SyntheticBody" };
                mesh.vertices = new[]
                {
                    new Vector3(-0.25f, 0f, 0f),
                    new Vector3(0.25f, 0f, 0f),
                    new Vector3(-0.25f, 0.5f, 0f),
                    new Vector3(0.25f, 0.5f, 0f)
                };
                mesh.normals = Enumerable.Repeat(Vector3.back, 4).ToArray();
                mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.up, Vector2.one };
                mesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
                mesh.boneWeights = Enumerable.Repeat(
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    4).ToArray();
                mesh.bindposes = new[] { Bone.worldToLocalMatrix * meshObject.transform.localToWorldMatrix };
                mesh.AddBlendShapeFrame(
                    "ManualShape",
                    100f,
                    new[]
                    {
                        Vector3.zero,
                        Vector3.zero,
                        new Vector3(0f, 0.1f, 0f),
                        new Vector3(0f, 0.1f, 0f)
                    },
                    new Vector3[4],
                    new Vector3[4]);
                mesh.RecalculateBounds();

                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
                {
                    name = "GeneratedTexture"
                };
                texture.SetPixels(new[] { Color.red, Color.green, Color.blue, Color.white });
                texture.Apply(false, false);

                var shader = Shader.Find("Standard");
                if (shader == null)
                {
                    throw new InvalidOperationException("The Standard shader is required by this test.");
                }
                material = new Material(shader) { name = "SyntheticMaterial" };
                material.SetTexture("_MainTex", texture);

                Renderer.sharedMesh = mesh;
                Renderer.bones = new[] { Bone };
                Renderer.rootBone = Bone;
                Renderer.sharedMaterial = material;
                Renderer.SetBlendShapeWeight(0, 35f);
                Bone.localRotation = Quaternion.Euler(0f, 20f, 0f);
            }

            public string CreatePrefabAsset()
            {
                generatedAssetFolder =
                    "Assets/__BlenderSafeAvatarFbxExporterTests_" + Guid.NewGuid().ToString("N");
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(generatedAssetFolder));

                var textureAsset = Object.Instantiate(texture);
                textureAsset.name = texture.name;
                AssetDatabase.CreateAsset(textureAsset, generatedAssetFolder + "/Texture.asset");
                var materialAsset = new Material(material) { name = material.name };
                materialAsset.SetTexture("_MainTex", textureAsset);
                AssetDatabase.CreateAsset(materialAsset, generatedAssetFolder + "/Material.mat");
                var meshAsset = Object.Instantiate(mesh);
                meshAsset.name = mesh.name;
                AssetDatabase.CreateAsset(meshAsset, generatedAssetFolder + "/Mesh.asset");

                var prefabSource = Object.Instantiate(Root);
                try
                {
                    foreach (var transform in prefabSource.GetComponentsInChildren<Transform>(true))
                    {
                        transform.gameObject.hideFlags = HideFlags.None;
                    }
                    var prefabRenderer = prefabSource.GetComponentInChildren<SkinnedMeshRenderer>(true);
                    prefabRenderer.sharedMesh = meshAsset;
                    prefabRenderer.sharedMaterial = materialAsset;
                    var prefabPath = generatedAssetFolder + "/SyntheticAvatar.prefab";
                    PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
                    AssetDatabase.SaveAssets();
                    return prefabPath;
                }
                finally
                {
                    Object.DestroyImmediate(prefabSource);
                }
            }

            public void Dispose()
            {
                if (!string.IsNullOrEmpty(generatedAssetFolder))
                {
                    AssetDatabase.DeleteAsset(generatedAssetFolder);
                }
                if (Root != null)
                {
                    Object.DestroyImmediate(Root);
                }
                if (mesh != null)
                {
                    Object.DestroyImmediate(mesh);
                }
                if (material != null)
                {
                    Object.DestroyImmediate(material);
                }
                if (texture != null)
                {
                    Object.DestroyImmediate(texture);
                }
                if (Directory.Exists(OutputDirectory))
                {
                    Directory.Delete(OutputDirectory, true);
                }
            }
        }
    }
}
