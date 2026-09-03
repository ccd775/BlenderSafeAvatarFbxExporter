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
        private static int UniquifyBlendShapeControlPoints(
            GameObject root,
            out float maxAdjustment,
            out float referenceLength)
        {
            var adjustedCount = 0;
            maxAdjustment = 0f;
            referenceLength = 1f;

            foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var mesh = renderer.sharedMesh;
                if (mesh == null || mesh.blendShapeCount == 0)
                {
                    continue;
                }

                referenceLength = Mathf.Max(referenceLength, GetBoundsDiagonal(mesh.bounds.size));
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

        /// <summary>
        /// Moves every skinned mesh whose Transform cannot be normalized in place onto a dedicated
        /// zero-transform child, so the exporter never has to ask the user to restructure a rig.
        /// </summary>
        private static int SeparateNonNormalizableSkinnedMeshRenderers(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null)
                .ToArray();
            var referencedBones = new HashSet<Transform>(
                renderers.SelectMany(renderer => renderer.bones).Where(bone => bone != null));
            var separatedCount = 0;

            foreach (var renderer in renderers)
            {
                // An FBX node cannot carry Mesh and Skeleton attributes at once, and a Transform
                // with children of its own cannot have its own transform zeroed without dragging
                // those children along.
                var hostsBone = referencedBones.Contains(renderer.transform);
                var hostsChildren = renderer.transform != root.transform && renderer.transform.childCount > 0;
                if (!hostsBone && !hostsChildren)
                {
                    continue;
                }

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
