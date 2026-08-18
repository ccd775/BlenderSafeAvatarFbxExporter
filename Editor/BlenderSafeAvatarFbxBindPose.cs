using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        private static int AlignBoneDefaultsToBindPoses(FbxScene scene)
        {
            var bindGlobals = new Dictionary<FbxNode, FbxMatrix>();
            try
            {
                CollectBindPoseGlobals(scene, bindGlobals);
                var zero = new FbxVector4(0.0, 0.0, 0.0);

                foreach (var pair in bindGlobals.OrderBy(item => GetFbxNodeDepth(item.Key)))
                {
                    var node = pair.Key;
                    if (node.InheritType.Get() != FbxTransform.EInheritType.eInheritRSrs)
                    {
                        throw new InvalidOperationException(
                            $"FBX bone '{node.GetName()}' does not use RSrs transform inheritance.");
                    }
                    if (node.LclTranslation.GetCurveNode() != null ||
                        node.LclRotation.GetCurveNode() != null ||
                        node.LclScaling.GetCurveNode() != null)
                    {
                        throw new InvalidOperationException(
                            $"FBX bone '{node.GetName()}' has transform animation and cannot be rest-pose aligned safely.");
                    }

                    var parent = node.GetParent();
                    FbxMatrix parentGlobal;
                    var ownsParentGlobal = false;
                    if (parent != null && bindGlobals.TryGetValue(parent, out parentGlobal))
                    {
                    }
                    else
                    {
                        for (var ancestor = parent; ancestor != null; ancestor = ancestor.GetParent())
                        {
                            if (bindGlobals.ContainsKey(ancestor))
                            {
                                throw new InvalidOperationException(
                                    $"FBX bone '{node.GetName()}' has a non-bind helper below another bind bone.");
                            }
                        }

                        if (parent == null)
                        {
                            parentGlobal = new FbxMatrix();
                            parentGlobal.SetIdentity();
                        }
                        else
                        {
                            parentGlobal = new FbxMatrix(parent.EvaluateGlobalTransform());
                        }
                        ownsParentGlobal = true;
                    }

                    try
                    {
                        using (var parentInverse = parentGlobal.Inverse())
                        using (var localBind = parentInverse * pair.Value)
                        using (var rotation = new FbxQuaternion())
                        {
                            localBind.GetElements(
                                out var translation,
                                rotation,
                                out var shear,
                                out var scale,
                                out var sign);
                            rotation.Normalize();

                            var maxShear = Math.Max(
                                Math.Abs(shear.X),
                                Math.Max(Math.Abs(shear.Y), Math.Abs(shear.Z)));
                            var minScale = Math.Min(
                                Math.Abs(scale.X),
                                Math.Min(Math.Abs(scale.Y), Math.Abs(scale.Z)));
                            if (maxShear > FbxShearTolerance)
                            {
                                throw new InvalidOperationException(
                                    $"FBX bone '{node.GetName()}' has non-TRS shear {maxShear:E6}.");
                            }
                            if (sign <= 0.0 || minScale <= 0.0000000001)
                            {
                                throw new InvalidOperationException(
                                    $"FBX bone '{node.GetName()}' has reflected or singular scale.");
                            }

                            using (var reconstructed = new FbxMatrix(translation, rotation, scale))
                            {
                                var reconstructionError = FbxMatrixDifference(localBind, reconstructed);
                                if (reconstructionError > FbxMatrixErrorTolerance)
                                {
                                    throw new InvalidOperationException(
                                        $"FBX bone '{node.GetName()}' cannot be represented as a pure TRS. " +
                                        $"Matrix error: {reconstructionError:E6}.");
                                }
                            }

                            FbxVector4 euler;
                            using (var rotationMatrix = new FbxAMatrix())
                            {
                                rotationMatrix.SetQ(rotation);
                                euler = rotationMatrix.GetR();
                            }

                            foreach (var pivotSet in new[]
                                     {
                                         FbxNode.EPivotSet.eSourcePivot,
                                         FbxNode.EPivotSet.eDestinationPivot
                                     })
                            {
                                node.SetRotationOffset(pivotSet, zero);
                                node.SetRotationPivot(pivotSet, zero);
                                node.SetPreRotation(pivotSet, zero);
                                node.SetPostRotation(pivotSet, zero);
                                node.SetScalingOffset(pivotSet, zero);
                                node.SetScalingPivot(pivotSet, zero);
                                node.SetRotationOrder(pivotSet, FbxEuler.EOrder.eOrderXYZ);
                                node.SetPivotState(pivotSet, FbxNode.EPivotState.ePivotReference);
                            }

                            node.SetRotationActive(true);
                            node.LclTranslation.Set(new FbxDouble3(
                                translation.X,
                                translation.Y,
                                translation.Z));
                            node.LclRotation.Set(new FbxDouble3(euler.X, euler.Y, euler.Z));
                            node.LclScaling.Set(new FbxDouble3(scale.X, scale.Y, scale.Z));
                        }
                    }
                    finally
                    {
                        if (ownsParentGlobal)
                        {
                            parentGlobal.Dispose();
                        }
                    }
                }

                ValidateFbxBoneDefaults(bindGlobals);
                return bindGlobals.Count;
            }
            finally
            {
                foreach (var matrix in bindGlobals.Values)
                {
                    matrix.Dispose();
                }
            }
        }

        private static void ValidateSavedFbxBoneDefaults(string fbxPath, int expectedBoneCount)
        {
            var manager = FbxManager.Create();
            var ioSettings = FbxIOSettings.Create(manager, Globals.IOSROOT);
            ioSettings.SetBoolProp(Globals.IMP_FBX_MATERIAL, false);
            ioSettings.SetBoolProp(Globals.IMP_FBX_TEXTURE, false);
            ioSettings.SetBoolProp(Globals.IMP_FBX_ANIMATION, false);
            ioSettings.SetBoolProp(Globals.IMP_FBX_EXTRACT_EMBEDDED_DATA, false);
            manager.SetIOSettings(ioSettings);
            try
            {
                var scene = FbxScene.Create(manager, "BlenderSafeValidation");
                try
                {
                    using (var importer = FbxImporter.Create(manager, "BlenderSafeValidationImporter"))
                    {
                        if (!importer.Initialize(fbxPath, -1, manager.GetIOSettings()) || !importer.Import(scene))
                        {
                            throw new InvalidOperationException(
                                "Failed to reopen the saved FBX for bind-pose validation.");
                        }
                    }

                    var bindGlobals = new Dictionary<FbxNode, FbxMatrix>();
                    try
                    {
                        CollectBindPoseGlobals(scene, bindGlobals);
                        if (bindGlobals.Count != expectedBoneCount)
                        {
                            throw new InvalidOperationException(
                                $"Saved FBX bind-pose bone count changed from {expectedBoneCount} " +
                                $"to {bindGlobals.Count}.");
                        }
                        ValidateFbxBoneDefaults(bindGlobals);
                    }
                    finally
                    {
                        foreach (var matrix in bindGlobals.Values)
                        {
                            matrix.Dispose();
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
        }

        private static void CollectBindPoseGlobals(
            FbxScene scene,
            IDictionary<FbxNode, FbxMatrix> bindGlobals)
        {
            var bindPoseCount = 0;
            for (var poseIndex = 0; ; poseIndex++)
            {
                var pose = scene.GetPose(poseIndex);
                if (pose == null)
                {
                    break;
                }
                if (!pose.IsBindPose())
                {
                    continue;
                }

                bindPoseCount++;
                for (var entryIndex = 0; entryIndex < pose.GetCount(); entryIndex++)
                {
                    var node = pose.GetNode(entryIndex);
                    if (node == null || node.GetSkeleton() == null)
                    {
                        continue;
                    }

                    var matrix = new FbxMatrix(pose.GetMatrix(entryIndex));
                    if (bindGlobals.TryGetValue(node, out var previous))
                    {
                        var error = FbxMatrixDifference(previous, matrix);
                        matrix.Dispose();
                        if (error > FbxMatrixErrorTolerance)
                        {
                            throw new InvalidOperationException(
                                $"FBX bind poses disagree for bone '{node.GetName()}'. " +
                                $"Matrix error: {error:E6}.");
                        }
                    }
                    else
                    {
                        bindGlobals.Add(node, matrix);
                    }
                }
            }

            if (bindPoseCount == 0 || bindGlobals.Count == 0)
            {
                throw new InvalidOperationException("The exported FBX contains no skeleton bind pose.");
            }
        }

        private static void ValidateFbxBoneDefaults(IReadOnlyDictionary<FbxNode, FbxMatrix> bindGlobals)
        {
            using (var time = FbxTime.FromSecondDouble(0.0))
            {
                foreach (var pair in bindGlobals)
                {
                    var actual = pair.Key.EvaluateGlobalTransform(
                        time,
                        FbxNode.EPivotSet.eSourcePivot,
                        false,
                        true);
                    var error = FbxMatrixDifference(actual, pair.Value);
                    if (error > FbxMatrixErrorTolerance)
                    {
                        throw new InvalidOperationException(
                            $"FBX bone '{pair.Key.GetName()}' default pose differs from its bind pose. " +
                            $"Matrix error: {error:E6}.");
                    }
                }
            }
        }

        private static double FbxMatrixDifference(FbxMatrix left, FbxMatrix right)
        {
            var max = 0.0;
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    max = Math.Max(max, Math.Abs(left.Get(row, column) - right.Get(row, column)));
                }
            }
            return max;
        }

        private static double FbxMatrixDifference(FbxAMatrix left, FbxMatrix right)
        {
            var max = 0.0;
            for (var row = 0; row < 4; row++)
            {
                for (var column = 0; column < 4; column++)
                {
                    max = Math.Max(max, Math.Abs(left.Get(row, column) - right.Get(row, column)));
                }
            }
            return max;
        }

        private static int GetFbxNodeDepth(FbxNode node)
        {
            var depth = 0;
            for (var parent = node.GetParent(); parent != null; parent = parent.GetParent())
            {
                depth++;
            }
            return depth;
        }
    }
}
