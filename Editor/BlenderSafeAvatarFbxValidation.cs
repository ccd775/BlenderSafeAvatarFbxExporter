using System;
using System.Collections.Generic;
using UnityEngine;

namespace ccd775.AvatarFbxExporter
{
    /// <summary>
    /// Controls how a measured geometric deviation is treated.
    /// Every level reports the same numbers; they differ only in what aborts the export.
    /// </summary>
    public enum BlenderSafeFbxValidationLevel
    {
        /// <summary>
        /// Reports sub-millimetre deviations and aborts only on a deviation large enough to be seen.
        /// </summary>
        Balanced = 0,

        /// <summary>
        /// Approximates the 0.1.x behaviour: almost any measurable deviation aborts the export.
        /// </summary>
        Strict = 1,

        /// <summary>
        /// Never aborts on a geometric deviation. Structural faults still abort.
        /// </summary>
        ReportOnly = 2
    }

    [Serializable]
    public sealed class BlenderSafeFbxExportOptions
    {
        public bool EmbedAllMaterialTextures = true;
        public bool OverwriteExisting;
        public BlenderSafeFbxValidationLevel ValidationLevel = BlenderSafeFbxValidationLevel.Balanced;

        /// <summary>
        /// Optional explicit abort budget, as a fraction of the measured geometry size.
        /// Values at or below zero fall back to <see cref="ValidationLevel"/>.
        /// </summary>
        public float GeometryFailRatio;
    }

    public static partial class BlenderSafeAvatarFbxExporter
    {
        // Deviations are compared against the size of the geometry they were measured on, so an
        // avatar authored in centimetres behaves like one authored in metres. The absolute floors
        // keep the budget sane for very small meshes.
        private const float BalancedWarnRatio = 0.0001f;
        private const float BalancedFailRatio = 0.005f;
        private const float StrictWarnRatio = 0.00001f;
        private const float StrictFailRatio = 0.0001f;
        private const float WarnAbsoluteFloor = 0.00001f;
        private const float FailAbsoluteFloor = 0.0001f;

        private const int MaxRecordedWarnings = 200;

        private sealed class ExportContext
        {
            public BlenderSafeFbxExportOptions Options;
            public BlenderSafeFbxExportResult Result;
            public readonly HashSet<string> SeenWarnings = new HashSet<string>(StringComparer.Ordinal);
            public int SuppressedWarnings;
        }

        private static ExportContext activeExport;

        private static BlenderSafeFbxValidationLevel ActiveValidationLevel =>
            activeExport?.Options?.ValidationLevel ?? BlenderSafeFbxValidationLevel.Balanced;

        /// <summary>
        /// Records a non-fatal problem. The export continues and the message ends up in
        /// <see cref="BlenderSafeFbxExportResult.Warnings"/> and the Unity console.
        /// </summary>
        private static void Warn(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            var context = activeExport;
            if (context == null)
            {
                Debug.LogWarning("[Blender-Safe FBX] " + message);
                return;
            }

            if (!context.SeenWarnings.Add(message))
            {
                return;
            }
            if (context.Result.Warnings.Count >= MaxRecordedWarnings)
            {
                context.SuppressedWarnings++;
                return;
            }

            context.Result.Warnings.Add(message);
            Debug.LogWarning("[Blender-Safe FBX] " + message);
        }

        private static void FlushSuppressedWarnings()
        {
            var context = activeExport;
            if (context == null || context.SuppressedWarnings <= 0)
            {
                return;
            }
            context.Result.Warnings.Add(
                $"{context.SuppressedWarnings} further warnings were suppressed.");
            context.SuppressedWarnings = 0;
        }

        private static float GetWarnRatio()
        {
            return ActiveValidationLevel == BlenderSafeFbxValidationLevel.Strict
                ? StrictWarnRatio
                : BalancedWarnRatio;
        }

        private static float GetFailRatio()
        {
            var configured = activeExport?.Options?.GeometryFailRatio ?? 0f;
            if (configured > 0f)
            {
                return configured;
            }

            switch (ActiveValidationLevel)
            {
                case BlenderSafeFbxValidationLevel.Strict:
                    return StrictFailRatio;
                case BlenderSafeFbxValidationLevel.ReportOnly:
                    return float.PositiveInfinity;
                default:
                    return BalancedFailRatio;
            }
        }

        /// <summary>
        /// Turns an absolute deviation into a dimensionless one by dividing it by the size of the
        /// geometry it was measured on. A zero or non-finite reference length falls back to 1.
        /// </summary>
        private static float ToDeviationRatio(float absoluteError, float referenceLength)
        {
            if (!IsFinite(absoluteError))
            {
                return float.PositiveInfinity;
            }
            if (!IsFinite(referenceLength) || referenceLength <= 0.000001f)
            {
                referenceLength = 1f;
            }
            return absoluteError / referenceLength;
        }

        private static float GetBoundsDiagonal(Vector3 size)
        {
            return IsFinite(size) ? size.magnitude : 1f;
        }

        /// <summary>
        /// Reports a measured geometric deviation. Below the warn budget nothing happens, between
        /// the budgets a warning is recorded, above the fail budget the export is aborted.
        /// </summary>
        /// <param name="gate">Short name of the stage that produced the deviation.</param>
        /// <param name="absoluteError">Deviation in the units it was measured in.</param>
        /// <param name="referenceLength">Size of the geometry the deviation was measured on.</param>
        /// <param name="consequence">Plain-language description of what the deviation means.</param>
        /// <param name="detail">Optional diagnostic lines that help locate the cause.</param>
        private static void ReportGeometryDeviation(
            string gate,
            float absoluteError,
            float referenceLength,
            string consequence,
            string detail = null)
        {
            // A non-finite measurement is a structural fault, not a deviation to be budgeted.
            if (!IsFinite(absoluteError))
            {
                throw new InvalidOperationException(
                    $"{gate} produced a non-finite deviation. {consequence}" +
                    (string.IsNullOrEmpty(detail) ? string.Empty : "\n" + detail));
            }

            var ratio = ToDeviationRatio(absoluteError, referenceLength);
            var warnBudget = Mathf.Max(WarnAbsoluteFloor, GetWarnRatio() * Mathf.Max(referenceLength, 0f));
            var failBudget = GetFailRatio() == float.PositiveInfinity
                ? float.PositiveInfinity
                : Mathf.Max(FailAbsoluteFloor, GetFailRatio() * Mathf.Max(referenceLength, 0f));

            if (absoluteError <= warnBudget)
            {
                return;
            }

            var summary =
                $"{gate} deviates from the Unity preview by {absoluteError:E3} " +
                $"({ratio:P3} of the measured geometry size {referenceLength:E3}).";
            var body = string.IsNullOrEmpty(detail) ? consequence : consequence + "\n" + detail;

            if (absoluteError <= failBudget)
            {
                Warn($"{summary}\n{body}\nThis is below the abort budget {failBudget:E3}; the FBX was still written.");
                return;
            }

            throw new InvalidOperationException(
                $"{summary}\n{body}\n" +
                $"This exceeds the abort budget {failBudget:E3} for validation level {ActiveValidationLevel}.\n" +
                "Set Validation to \"Report only\" in the exporter window to export anyway.");
        }
    }
}
