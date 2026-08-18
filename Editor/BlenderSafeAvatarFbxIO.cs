using System;
using System.IO;
using UnityEditor;
using PackageManagerInfo = UnityEditor.PackageManager.PackageInfo;

namespace ccd775.AvatarFbxExporter
{
    public static partial class BlenderSafeAvatarFbxExporter
    {
        private static string CreateStagingDirectory()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "ccd775",
                "BlenderSafeAvatarFbxExporter");
            var directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string ResolveAssetSourcePath(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return Path.GetFullPath(assetPath);
            }
            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return null;
            }

            var package = PackageManagerInfo.FindForAssetPath(assetPath);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath) || string.IsNullOrEmpty(package.assetPath))
            {
                return null;
            }
            var relativePath = assetPath.Substring(package.assetPath.Length).TrimStart('/', '\\');
            return Path.GetFullPath(Path.Combine(package.resolvedPath, relativePath));
        }

        private static void CommitOutputFile(string stagingPath, string outputPath, bool overwriteExisting)
        {
            if (File.Exists(outputPath) && !overwriteExisting)
            {
                throw new IOException(
                    $"The target FBX already exists: '{outputPath}'. " +
                    "Pass overwriteExisting: true only after confirming replacement.");
            }

            var directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new ArgumentException("Invalid FBX output path.", nameof(outputPath));
            }
            Directory.CreateDirectory(directory);

            var token = Guid.NewGuid().ToString("N");
            var pendingPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{token}.pending");
            var backupPath = Path.Combine(directory, $".{Path.GetFileName(outputPath)}.{token}.backup");
            var hadExistingFile = File.Exists(outputPath);
            var committed = false;
            try
            {
                File.Copy(stagingPath, pendingPath, false);
                if (new FileInfo(pendingPath).Length != new FileInfo(stagingPath).Length)
                {
                    throw new IOException("The staged FBX copy has an unexpected file size.");
                }

                if (hadExistingFile)
                {
                    File.Replace(pendingPath, outputPath, backupPath, true);
                }
                else
                {
                    File.Move(pendingPath, outputPath);
                }

                try
                {
                    ImportIfProjectAsset(outputPath);
                    committed = true;
                }
                catch
                {
                    if (hadExistingFile && File.Exists(backupPath))
                    {
                        File.Replace(backupPath, outputPath, null, true);
                        ImportIfProjectAsset(outputPath);
                    }
                    else if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }
                    throw;
                }
            }
            finally
            {
                if (File.Exists(pendingPath))
                {
                    File.Delete(pendingPath);
                }
                if (committed && File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
        }

        private static void TryDeleteStagingDirectory(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }
            try
            {
                Directory.Delete(directory, true);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"Could not delete temporary Blender-Safe FBX directory '{directory}': {exception.Message}");
            }
        }
    }
}
