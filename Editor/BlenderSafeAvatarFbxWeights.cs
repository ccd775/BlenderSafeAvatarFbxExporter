using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        // Every exported BlendShape frame is normalized to this full weight, so an FBX
        // DeformPercent maps one-to-one onto the recorded Unity weight regardless of the frame
        // weight the source mesh happened to use.
        private const float NormalizedBlendShapeFrameWeight = 100f;

        private struct BlendShapeDeviation
        {
            public float MaxError;
            public int WorstVertexIndex;
            public Vector3 WorstVertexPosition;
            public float MaxUnrenderedError;
            public string Diagnostic;
        }

        /// <summary>
        /// Converts a Unity renderer weight into the value written to FBX DeformPercent.
        /// The exported frame always carries <see cref="NormalizedBlendShapeFrameWeight"/>, and
        /// Unity's legacy clamping is reproduced so the FBX matches what the editor displays.
        /// </summary>
        private static float ToRecordedBlendShapeWeight(float rendererWeight, string shapeName, string rendererName)
        {
            if (!IsFinite(rendererWeight))
            {
                Warn($"BlendShape '{shapeName}' on '{rendererName}' has a non-finite weight; exporting it as 0.");
                return 0f;
            }

            if (!PlayerSettings.legacyClampBlendShapeWeights)
            {
                return rendererWeight;
            }

            var clamped = Mathf.Clamp(rendererWeight, 0f, NormalizedBlendShapeFrameWeight);
            if (Mathf.Abs(clamped - rendererWeight) > 0.000001f)
            {
                Warn(
                    $"BlendShape '{shapeName}' on '{rendererName}' has weight {rendererWeight:0.###}, " +
                    "but the project enables \"Clamp BlendShapes (Deprecated)\" so Unity renders it as " +
                    $"{clamped:0.###}. The clamped value was exported to match the editor.");
            }
            return clamped;
        }

        /// <summary>
        /// Measures how far the exported base mesh plus BlendShape targets, evaluated at the
        /// recorded weights, drifts from the mesh Unity actually displays.
        /// </summary>
        private static BlendShapeDeviation MeasureBlendShapeReconstruction(
            Mesh expected,
            Mesh bakedMesh,
            IReadOnlyList<BlendShapeWeight> recordedWeights,
            string rendererName)
        {
            var deviation = new BlendShapeDeviation { WorstVertexIndex = -1 };
            if (expected.vertexCount != bakedMesh.vertexCount ||
                bakedMesh.blendShapeCount != recordedWeights.Count)
            {
                deviation.MaxError = float.PositiveInfinity;
                deviation.Diagnostic =
                    $"Rebuilt mesh for '{rendererName}' does not match the source " +
                    $"({expected.vertexCount} vs {bakedMesh.vertexCount} vertices, " +
                    $"{recordedWeights.Count} vs {bakedMesh.blendShapeCount} BlendShapes).";
                return deviation;
            }

            var expectedVertices = expected.vertices;
            var reconstructed = bakedMesh.vertices;
            var deltas = new Vector3[bakedMesh.vertexCount];
            var deltaNormals = new Vector3[bakedMesh.vertexCount];
            var deltaTangents = new Vector3[bakedMesh.vertexCount];
            var activeShapes = new List<int>();

            for (var shapeIndex = 0; shapeIndex < bakedMesh.blendShapeCount; shapeIndex++)
            {
                var frameCount = bakedMesh.GetBlendShapeFrameCount(shapeIndex);
                if (frameCount == 0 || Mathf.Abs(recordedWeights[shapeIndex].Weight) <= 0.000001f)
                {
                    continue;
                }

                var frameIndex = frameCount - 1;
                var frameWeight = bakedMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                if (Mathf.Abs(frameWeight) <= 0.000001f)
                {
                    continue;
                }

                activeShapes.Add(shapeIndex);
                bakedMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltas, deltaNormals, deltaTangents);
                var multiplier = recordedWeights[shapeIndex].Weight / frameWeight;
                for (var vertexIndex = 0; vertexIndex < reconstructed.Length; vertexIndex++)
                {
                    reconstructed[vertexIndex] += deltas[vertexIndex] * multiplier;
                }
            }

            // "Visible" means referenced by a triangle. Orphan control points and geometry culled by
            // NaNimation stay in the vertex array but can never be seen, so they are reported
            // separately instead of vetoing the export.
            var rendered = GetRenderedVertexMask(bakedMesh);
            for (var index = 0; index < expectedVertices.Length; index++)
            {
                var error = Vector3.Distance(expectedVertices[index], reconstructed[index]);
                if (rendered != null && !rendered[index])
                {
                    deviation.MaxUnrenderedError = Mathf.Max(deviation.MaxUnrenderedError, error);
                    continue;
                }
                if (error > deviation.MaxError)
                {
                    deviation.MaxError = error;
                    deviation.WorstVertexIndex = index;
                    deviation.WorstVertexPosition = expectedVertices[index];
                }
            }

            deviation.Diagnostic = DescribeWorstVertex(
                bakedMesh,
                recordedWeights,
                activeShapes,
                deviation.WorstVertexIndex,
                deltas,
                deltaNormals,
                deltaTangents);
            return deviation;
        }

        private static bool[] GetRenderedVertexMask(Mesh mesh)
        {
            var rendered = new bool[mesh.vertexCount];
            var anyIndex = false;
            for (var subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                var indices = mesh.GetIndices(subMeshIndex);
                for (var index = 0; index < indices.Length; index++)
                {
                    var vertexIndex = indices[index];
                    if (vertexIndex < 0 || vertexIndex >= rendered.Length)
                    {
                        continue;
                    }
                    rendered[vertexIndex] = true;
                    anyIndex = true;
                }
            }

            // A mesh with no index buffer at all is measured in full rather than skipped entirely.
            return anyIndex ? rendered : null;
        }

        private static string DescribeWorstVertex(
            Mesh bakedMesh,
            IReadOnlyList<BlendShapeWeight> recordedWeights,
            IReadOnlyList<int> activeShapes,
            int worstVertexIndex,
            Vector3[] deltas,
            Vector3[] deltaNormals,
            Vector3[] deltaTangents)
        {
            if (worstVertexIndex < 0 || activeShapes.Count == 0)
            {
                return null;
            }

            var contributions = new List<string>();
            var ranked = new List<KeyValuePair<float, string>>(activeShapes.Count);
            foreach (var shapeIndex in activeShapes)
            {
                var frameIndex = bakedMesh.GetBlendShapeFrameCount(shapeIndex) - 1;
                var frameWeight = bakedMesh.GetBlendShapeFrameWeight(shapeIndex, frameIndex);
                bakedMesh.GetBlendShapeFrameVertices(shapeIndex, frameIndex, deltas, deltaNormals, deltaTangents);
                var recorded = recordedWeights[shapeIndex];
                var magnitude = (deltas[worstVertexIndex] * (recorded.Weight / frameWeight)).magnitude;
                if (magnitude <= 0.0000001f)
                {
                    continue;
                }

                var description = $"'{recorded.Name}' moves it {magnitude:E3} at weight {recorded.Weight:0.###}";
                if (Mathf.Abs(recorded.SourceFrameWeight - NormalizedBlendShapeFrameWeight) > 0.000001f)
                {
                    description += $" (source frame weight {recorded.SourceFrameWeight:0.###})";
                }
                ranked.Add(new KeyValuePair<float, string>(magnitude, description));
            }

            foreach (var entry in ranked.OrderByDescending(item => item.Key).Take(3))
            {
                contributions.Add(entry.Value);
            }
            if (contributions.Count == 0)
            {
                return null;
            }

            return $"Worst control point {worstVertexIndex}; largest contributors: " +
                   string.Join("; ", contributions) + ".";
        }

        /// <summary>
        /// Adds context that explains the most common reasons a reconstruction drifts at all.
        /// </summary>
        private static string DescribeBlendShapeEnvironment(IEnumerable<BlendShapeWeightSet> weightSets)
        {
            var notes = new List<string>();
            if (PlayerSettings.legacyClampBlendShapeWeights)
            {
                notes.Add(
                    "Player Settings > Other > Legacy > \"Clamp BlendShapes (Deprecated)\" is enabled, " +
                    "so Unity clamps weights to 0-100 while an unclamped FBX would extrapolate.");
            }

            var rescaled = weightSets
                .SelectMany(set => set.Weights)
                .Count(weight => Mathf.Abs(weight.SourceFrameWeight - NormalizedBlendShapeFrameWeight) > 0.000001f);
            if (rescaled > 0)
            {
                notes.Add(
                    $"{rescaled} BlendShape channels use a frame weight other than 100 and were " +
                    "rescaled to a 100-weight target.");
            }

            return notes.Count == 0 ? null : string.Join("\n", notes);
        }
    }
}
