using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        private static int UniquifyBlendShapeControlPoints(GameObject root, out float maxAdjustment)
        {
            var adjustedCount = 0;
            maxAdjustment = 0f;

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0)
                {
                    continue;
                }

                var vertices = mesh.vertices;
                var usedVertices = new HashSet<Vector3>();
                for (var vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
                {
                    var original = vertices[vertexIndex];
                    var candidate = original;
                    while (!usedVertices.Add(candidate))
                    {
                        var bits = BitConverter.SingleToInt32Bits(candidate.x);
                        bits += candidate.x >= 0f ? 1 : -1;
                        candidate.x = BitConverter.Int32BitsToSingle(bits);
                    }

                    if (candidate.Equals(original))
                    {
                        continue;
                    }

                    vertices[vertexIndex] = candidate;
                    adjustedCount++;
                    maxAdjustment = Mathf.Max(maxAdjustment, Vector3.Distance(original, candidate));
                }

                mesh.vertices = vertices;
                mesh.RecalculateBounds();
            }

            return adjustedCount;
        }

        private static int SeparateBoneHostedSkinnedMeshRenderers(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null)
                .ToArray();
            var referencedBones = new HashSet<Transform>(
                renderers.SelectMany(renderer => renderer.bones).Where(bone => bone != null));
            var separatedCount = 0;

            foreach (var renderer in renderers)
            {
                if (!referencedBones.Contains(renderer.transform))
                {
                    continue;
                }

                // FBX nodes cannot simultaneously carry Mesh and Skeleton node attributes.
                var meshObject = new GameObject(renderer.name + "__Mesh")
                {
                    layer = renderer.gameObject.layer,
                    tag = renderer.gameObject.tag,
                    hideFlags = renderer.gameObject.hideFlags
                };
                meshObject.transform.SetParent(renderer.transform, false);
                meshObject.transform.localPosition = Vector3.zero;
                meshObject.transform.localRotation = Quaternion.identity;
                meshObject.transform.localScale = Vector3.one;
                meshObject.SetActive(renderer.gameObject.activeSelf);

                var separatedRenderer = meshObject.AddComponent<SkinnedMeshRenderer>();
                EditorUtility.CopySerialized(renderer, separatedRenderer);
                Object.DestroyImmediate(renderer);
                separatedCount++;
            }

            return separatedCount;
        }
    }
}
