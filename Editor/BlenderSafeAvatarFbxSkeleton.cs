using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        private struct BoneWorldPose
        {
            public Vector3 Position;
            public Quaternion Rotation;
        }

        private static int StandardizeSkeletonScales(
            GameObject root,
            out float maxMeshError,
            out float referenceLength)
        {
            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(renderer => renderer.sharedMesh != null)
                .ToArray();
            var bones = renderers
                .SelectMany(renderer => renderer.bones)
                .Where(bone => bone != null && bone != root.transform)
                .Distinct()
                .ToArray();

            var referenceVertices = new Dictionary<SkinnedMeshRenderer, Vector3[]>(renderers.Length);
            referenceLength = 1f;
            foreach (var renderer in renderers)
            {
                var referenceMesh = new Mesh();
                try
                {
                    renderer.BakeMesh(referenceMesh, false);
                    ValidateFiniteMesh(referenceMesh, renderer, "pre-normalization skeleton pose");
                    referenceLength = Mathf.Max(referenceLength, GetBoundsDiagonal(referenceMesh.bounds.size));
                    referenceVertices.Add(renderer, referenceMesh.vertices);
                }
                finally
                {
                    Object.DestroyImmediate(referenceMesh);
                }
            }

            var poses = new Dictionary<Transform, BoneWorldPose>(bones.Length);
            foreach (var bone in bones)
            {
                if (!IsFinite(bone.localToWorldMatrix))
                {
                    throw new InvalidOperationException(
                        $"Bone '{GetPath(bone, root.transform)}' has a non-finite Transform before skeleton normalization.");
                }
                if (bone.localToWorldMatrix.determinant <= 0f)
                {
                    throw new InvalidOperationException(
                        $"Bone '{GetPath(bone, root.transform)}' has a reflected or singular Transform " +
                        "that cannot be converted to a unit-scale Blender rest pose safely.");
                }
                poses.Add(bone, new BoneWorldPose
                {
                    Position = bone.position,
                    Rotation = bone.rotation
                });
            }

            foreach (var bone in bones.OrderBy(bone => GetTransformDepth(bone, root.transform)))
            {
                var pose = poses[bone];
                bone.localScale = Vector3.one;
                bone.SetPositionAndRotation(pose.Position, pose.Rotation);
                if (!IsFinite(bone.localToWorldMatrix))
                {
                    throw new InvalidOperationException(
                        $"Bone '{GetPath(bone, root.transform)}' became non-finite during skeleton normalization.");
                }
            }

            foreach (var renderer in renderers)
            {
                renderer.sharedMesh.bindposes = renderer.bones
                    .Select(bone => bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix)
                    .ToArray();
            }

            maxMeshError = 0f;
            foreach (var renderer in renderers)
            {
                var normalizedMesh = new Mesh();
                try
                {
                    renderer.BakeMesh(normalizedMesh, false);
                    ValidateFiniteMesh(normalizedMesh, renderer, "normalized skeleton pose");
                    var expected = referenceVertices[renderer];
                    var actual = normalizedMesh.vertices;
                    if (actual.Length != expected.Length)
                    {
                        throw new InvalidOperationException(
                            $"Skeleton normalization changed the vertex count of '{renderer.name}'.");
                    }
                    for (var vertexIndex = 0; vertexIndex < actual.Length; vertexIndex++)
                    {
                        maxMeshError = Mathf.Max(
                            maxMeshError,
                            Vector3.Distance(expected[vertexIndex], actual[vertexIndex]));
                    }
                }
                finally
                {
                    Object.DestroyImmediate(normalizedMesh);
                }
            }

            return bones.Length;
        }

        private static int GetTransformDepth(Transform transform, Transform root)
        {
            var depth = 0;
            for (var current = transform; current != null && current != root; current = current.parent)
            {
                depth++;
            }
            return depth;
        }
    }
}
