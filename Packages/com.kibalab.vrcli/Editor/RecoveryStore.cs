using System;
using System.IO;
using UnityEngine;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class RecoveryStore
    {
        private const string ManifestName = "recovery.json";
        private static BuildArtifact currentArtifact;

        public static void Start()
        {
            currentArtifact = null;
        }

        public static void Preserve(
            DeploymentRequest request,
            BuildArtifact sourceArtifact,
            string signature,
            bool createsContent)
        {
            Directory.CreateDirectory(request.RecoveryDirectory);
            string extension = Path.GetExtension(sourceArtifact.Path);
            string bundlePath = Path.Combine(request.RecoveryDirectory, "bundle" + extension);
            if (!string.Equals(
                    Path.GetFullPath(sourceArtifact.Path),
                    Path.GetFullPath(bundlePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceArtifact.Path, bundlePath, true);
            }

            BuildArtifact preserved = BuildArtifact.Capture(bundlePath);
            string manifestPath = Path.Combine(request.RecoveryDirectory, ManifestName);
            preserved.RecoveryFile = manifestPath;
            sourceArtifact.RecoveryFile = manifestPath;
            RecoveryManifest manifest = new RecoveryManifest
            {
                ProjectPath = Directory.GetParent(Application.dataPath).FullName,
                ContentType = request.ContentType.ToString(),
                Blueprint = request.BlueprintId ?? string.Empty,
                IsNew = request.ContentType == ContentType.World && request.IsNew,
                Title = request.Title ?? string.Empty,
                Description = request.Description ?? string.Empty,
                ThumbnailPath = request.ThumbnailPath ?? string.Empty,
                Capacity = request.Capacity,
                RecommendedCapacity = request.RecommendedCapacity,
                Tags = request.Tags ?? new string[0],
                UpdateTitle = request.UpdateTitle || createsContent,
                UpdateDescription = request.UpdateDescription || createsContent,
                UpdateThumbnail = request.UpdateThumbnail || createsContent,
                UpdateCapacity = request.UpdateCapacity,
                UpdateRecommendedCapacity = request.UpdateRecommendedCapacity,
                UpdateTags = request.UpdateTags || createsContent,
                Platform = request.Platform,
                ScenePath = request.ScenePath ?? string.Empty,
                TargetPath = request.TargetPath ?? string.Empty,
                BundlePath = bundlePath,
                Signature = signature ?? string.Empty
            };
            string temporary = manifestPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true));
            if (File.Exists(manifestPath)) File.Delete(manifestPath);
            File.Move(temporary, manifestPath);
            currentArtifact = preserved;
            DeploymentLog.Info("RECOVERY", "Preserved upload recovery manifest: " + manifestPath);
        }

        public static DeploymentOutcome FailureOutcome()
        {
            return currentArtifact == null
                ? null
                : new DeploymentOutcome { Artifact = currentArtifact };
        }

    }
}
