using System;
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
                RequiredFbxExporterVersion);
            ValidatePackageVersion(
                PackageManagerInfo.FindForAssembly(typeof(FbxManager).Assembly),
                "com.autodesk.fbx",
                RequiredFbxSdkVersion);

            if (FbxPropertyHandleField == null || FbxPropertyDoubleConstructor == null)
            {
                throw new NotSupportedException(
                    "The Autodesk FBX 4.2.1 property adapter is unavailable. " +
                    "Reinstall FBX Exporter 4.2.1 and Autodesk FBX SDK 4.2.1.");
            }
        }

        private static void ValidatePackageVersion(PackageManagerInfo package, string expectedName, string expectedVersion)
        {
            if (package == null ||
                !string.Equals(package.name, expectedName, StringComparison.Ordinal) ||
                !string.Equals(package.version, expectedVersion, StringComparison.Ordinal))
            {
                var actual = package == null ? "not installed" : $"{package.name}@{package.version}";
                throw new NotSupportedException(
                    $"This exporter requires {expectedName}@{expectedVersion}; found {actual}.");
            }
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

            var handle = (HandleRef)FbxPropertyHandleField.GetValue(property);
            using (var typedProperty = (FbxPropertyDouble)FbxPropertyDoubleConstructor.Invoke(
                       new object[] { handle.Handle, false }))
            {
                typedProperty.Set(value);
                if (Math.Abs(typedProperty.Get() - value) > 0.000001)
                {
                    throw new InvalidOperationException(
                        $"Could not set FBX material property '{material.GetName()}.{propertyName}'.");
                }
            }
        }
    }
}
