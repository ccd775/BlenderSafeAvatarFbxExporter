using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Autodesk.Fbx;
using UnityEditor.Formats.Fbx.Exporter;
using PackageManagerInfo = UnityEditor.PackageManager.PackageInfo;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        // FBX SDK 4.2.1 exposes imported template Number properties only as FbxProperty.
        // Reopening the native handle through its typed wrapper is version-specific but avoids
        // silently rejected generic Set(float) calls. ValidateEnvironment hard-pins this adapter.
        private static readonly FieldInfo FbxPropertyHandleField = typeof(FbxProperty).GetField(
            "swigCPtr",
            BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly ConstructorInfo FbxPropertyDoubleConstructor = typeof(FbxPropertyDouble).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            new[] { typeof(IntPtr), typeof(bool) },
            null);

        private static void ValidateEnvironment()
        {
            ValidatePackageVersion(
                PackageManagerInfo.FindForAssembly(typeof(ModelExporter).Assembly),
                "com.unity.formats.fbx",
                MinimumFbxExporterVersion,
                TestedFbxExporterVersion);
            ValidatePackageVersion(
                PackageManagerInfo.FindForAssembly(typeof(FbxManager).Assembly),
                "com.autodesk.fbx",
                MinimumFbxSdkVersion,
                TestedFbxSdkVersion);

            if (FbxPropertyHandleField == null || FbxPropertyDoubleConstructor == null)
            {
                // Only a fallback for template Number properties that reject a generic Set(float).
                // Without it those individual material values are skipped, not the whole export.
                Warn(
                    "The Autodesk FBX typed-property adapter is unavailable in this SDK build. " +
                    "Material numeric factors that reject a generic write will be skipped.");
            }
        }

        /// <summary>
        /// Refuses a package that is missing or too old to carry the API this exporter uses, and
        /// only warns about a version that is newer than the one it was verified against.
        /// </summary>
        private static void ValidatePackageVersion(
            PackageManagerInfo package,
            string expectedName,
            string minimumVersion,
            string testedVersion)
        {
            if (package == null || !string.Equals(package.name, expectedName, StringComparison.Ordinal))
            {
                throw new NotSupportedException(
                    $"This exporter requires the {expectedName} package (>= {minimumVersion}); " +
                    $"found {(package == null ? "not installed" : package.name)}.");
            }

            if (ComparePackageVersions(package.version, minimumVersion) < 0)
            {
                throw new NotSupportedException(
                    $"This exporter requires {expectedName} >= {minimumVersion}; " +
                    $"found {package.version}. Update the package in Package Manager.");
            }

            if (!string.Equals(package.version, testedVersion, StringComparison.Ordinal))
            {
                Warn(
                    $"{expectedName}@{package.version} has not been verified with this exporter " +
                    $"(verified against {testedVersion}). Check the exported FBX in Blender before " +
                    "relying on it.");
            }
        }

        /// <summary>
        /// Compares the leading numeric components of two package versions. Pre-release suffixes
        /// are ignored, so "5.0.0-pre.3" compares as "5.0.0".
        /// </summary>
        private static int ComparePackageVersions(string left, string right)
        {
            var leftParts = ParseVersionComponents(left);
            var rightParts = ParseVersionComponents(right);
            for (var index = 0; index < 3; index++)
            {
                var comparison = leftParts[index].CompareTo(rightParts[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            return 0;
        }

        private static int[] ParseVersionComponents(string version)
        {
            var components = new int[3];
            if (string.IsNullOrEmpty(version))
            {
                return components;
            }

            var separatorIndex = version.IndexOfAny(new[] { '-', '+' });
            var numeric = separatorIndex < 0 ? version : version.Substring(0, separatorIndex);
            var parts = numeric.Split('.');
            for (var index = 0; index < components.Length && index < parts.Length; index++)
            {
                int value;
                if (int.TryParse(parts[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    components[index] = value;
                }
            }
            return components;
        }

        private static void SetDoubleProperty(FbxSurfaceMaterial material, string propertyName, double value)
        {
            var property = material.FindProperty(propertyName);
            if (property == null || !property.IsValid())
            {
                property = FbxProperty.Create(material, Globals.FbxDoubleDT, propertyName, propertyName);
            }

            property.Set((float)value);
            if (Math.Abs(property.GetDouble() - value) <= 0.000001)
            {
                return;
            }

            if (FbxPropertyHandleField == null || FbxPropertyDoubleConstructor == null)
            {
                Warn(
                    $"FBX material property '{material.GetName()}.{propertyName}' rejected the value " +
                    $"{value:0.###} and no typed-property adapter is available; it was left unchanged.");
                return;
            }

            var handle = (HandleRef)FbxPropertyHandleField.GetValue(property);
            using (var typedProperty = (FbxPropertyDouble)FbxPropertyDoubleConstructor.Invoke(
                       new object[] { handle.Handle, false }))
            {
                typedProperty.Set(value);
                if (Math.Abs(typedProperty.Get() - value) > 0.000001)
                {
                    // Material factors are metadata; Blender still receives usable geometry.
                    Warn(
                        $"Could not set FBX material property '{material.GetName()}.{propertyName}'; " +
                        "it was left at its template default.");
                }
            }
        }
    }
}
