using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using VRC.Editor;
using VRC.SDK3A.Editor;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class AvatarDeployer
    {
        private static string lastUploadStatus;
        private static int lastUploadBucket = -1;

        public static async Task<DeploymentOutcome> DeployAsync(DeploymentRequest request, string scenePath)
        {
            DeploymentLog.Phase("PREPARE", "Preparing the avatar scene for the VRChat SDK builder.");
            ContentScene.ValidatePlatform(request.Platform);
            ContentScene.Open(scenePath);
            AvatarTarget target = await AvatarTarget.FindAsync(request);
            try
            {
                return await DeployTargetAsync(request, target);
            }
            finally
            {
                target.RestoreActivation();
            }
        }

        private static async Task<DeploymentOutcome> DeployTargetAsync(
            DeploymentRequest request,
            AvatarTarget target)
        {
            string blueprint = target.Pipeline.blueprintId;
            bool created = string.IsNullOrWhiteSpace(blueprint);
            int previousVersion = 0;
            if (!created && !blueprint.StartsWith("avtr_", StringComparison.Ordinal))
                throw new ArgumentException("The selected avatar Blueprint ID must begin with 'avtr_': " + blueprint);
            if (created && !request.OwnershipAccepted)
                throw new ContentOwnershipException("Creating an avatar requires --yes to certify that you have the rights to upload its content.");

            VRCAvatar avatar;
            if (created)
            {
                avatar = AvatarMetadata.Create(request);
                DeploymentLog.Phase("AVATAR", "Prepared new private avatar metadata.");
                DeploymentLog.Info("AVATAR", "Name: " + avatar.Name);
                DeploymentLog.Info("AVATAR", "Thumbnail: " + request.ThumbnailPath + " (" + DeploymentLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length) + ")");
                DeploymentLog.Phase("OWNERSHIP", "Content ownership certification supplied; consent will be recorded after VRChat reserves an avatar ID.");
            }
            else
            {
                request.UseBlueprintId(blueprint);
                DeploymentLog.Phase("AVATAR", "Fetching the existing VRChat avatar record.");
                avatar = await AvatarMetadata.FetchOwnedAsync(blueprint);
                previousVersion = avatar.Version;
                AvatarMetadata.Apply(ref avatar, request);
                await OwnershipAgreement.EnsureAsync(blueprint, request.OwnershipAccepted);
                DeploymentLog.Info("AVATAR", "Avatar: " + avatar.Name + " (version " + avatar.Version + ")");
                DeploymentLog.Info("AVATAR", "Ownership confirmed for the authenticated account.");
            }

            string bundlePath;
            if (request.IsResume)
            {
                DeploymentLog.Phase("BUILD", "Reusing the preserved avatar bundle; SDK validation and build are skipped for this recovery attempt.");
                bundlePath = request.ResumeBundlePath;
            }
            else
            {
                DeploymentLog.Phase("SDK", "Initializing the VRChat avatar builder.");
                IVRCSdkAvatarBuilderApi builder = await GetBuilderAsync();
                builder.SelectAvatar(target.GameObject);
                string invalidReason;
                if (!builder.IsValidBuilder(out invalidReason))
                    throw new BuilderException(string.IsNullOrWhiteSpace(invalidReason) ? "The VRChat SDK avatar builder rejected the selected avatar." : invalidReason);

                EventHandler<string> progress = (sender, status) => DeploymentLog.Info("BUILD", status);
                builder.OnSdkBuildProgress += progress;
                try
                {
                    VRC_SdkBuilder.ActiveBuildType = VRC_SdkBuilder.BuildType.Publish;
                    DeploymentLog.Phase("BUILD", "Starting SDK validation and avatar asset-bundle build for " + request.Platform + ".");
                    bundlePath = await builder.Build(target.GameObject);
                }
                finally
                {
                    builder.OnSdkBuildProgress -= progress;
                }
            }
            if (string.IsNullOrWhiteSpace(bundlePath) || !File.Exists(bundlePath))
                throw new BuilderException("The SDK did not return a valid avatar bundle path.");
            BuildArtifact artifact = BuildArtifact.Capture(bundlePath);
            DeploymentLog.Info("BUILD", "Bundle ready: " + bundlePath);
            DeploymentLog.Info("BUILD", "Bundle size: " + DeploymentLog.FormatBytes(new FileInfo(bundlePath).Length));
            DeploymentLog.Info("BUILD", "Bundle SHA-256: " + artifact.Sha256);
            RecoveryStore.Preserve(request, artifact, null, created);
            DeploymentLog.Phase("SIGNATURE", "Avatar bundles do not use the world bundle-signature step; continuing with SDK upload.");

            ResetUploadProgress();
            if (created)
            {
                DeploymentLog.Phase("UPLOAD", "Reserving a new avatar record before uploading its bundle and thumbnail.");
                VRCAvatar reserved = await VRCApi.CreateAvatarRecord(avatar, OnUploadProgress);
                if (string.IsNullOrWhiteSpace(reserved.ID)) throw new UploadException("VRChat did not reserve a new avatar ID.");
                blueprint = reserved.ID;
                avatar = reserved;
                target.Pipeline.blueprintId = blueprint;
                target.RestoreActivation();
                EditorUtility.SetDirty(target.Pipeline);
                EditorSceneManager.SaveOpenScenes();
                request.UseBlueprintId(blueprint);
                try
                {
                    await OwnershipAgreement.AcceptForNewContentAsync(blueprint, true);
                    DeploymentLog.Info("UPLOAD", "Reserved Blueprint: " + blueprint);
                    avatar = await VRCApi.CreateNewAvatar(blueprint, avatar, bundlePath, request.ThumbnailPath, OnUploadProgress);
                }
                catch
                {
                    await VRCApi.DeleteAvatar(blueprint);
                    target.Pipeline.blueprintId = string.Empty;
                    EditorUtility.SetDirty(target.Pipeline);
                    EditorSceneManager.SaveOpenScenes();
                    throw;
                }
            }
            else
            {
                DeploymentLog.Phase("UPLOAD", request.HasMetadataUpdate || request.UpdateThumbnail
                    ? "Uploading the avatar bundle and requested metadata changes."
                    : "Uploading the avatar bundle.");
                if (request.HasMetadataUpdate) avatar = await VRCApi.UpdateAvatarInfo(blueprint, avatar);
                if (request.UpdateThumbnail) avatar = await VRCApi.UpdateAvatarImage(blueprint, avatar, request.ThumbnailPath, OnUploadProgress);
                avatar = await VRCApi.UpdateAvatarBundle(blueprint, avatar, bundlePath, OnUploadProgress);
            }

            if (string.IsNullOrWhiteSpace(avatar.ID)) throw new UploadException("VRChat did not return the uploaded avatar record.");
            DeploymentLog.Info("UPLOAD", "Avatar upload completed for " + avatar.ID + ".");
            DeploymentLog.Info("UPLOAD", "Server avatar version: " + avatar.Version);
            return new DeploymentOutcome
            {
                Blueprint = avatar.ID,
                Created = created,
                Message = created ? "Avatar created, built, and uploaded." : "Avatar build and upload completed.",
                PreviousVersion = previousVersion,
                ServerVersion = avatar.Version,
                Artifact = artifact
            };
        }

        internal static async Task<IVRCSdkAvatarBuilderApi> GetBuilderAsync()
        {
            EditorWindow window = EditorWindow.GetWindow<VRCSdkControlPanel>(false, "VRChat SDK", false);
            window.Show();
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                IVRCSdkAvatarBuilderApi builder;
                if (VRCSdkControlPanel.TryGetBuilder(out builder)) return builder;
                await Task.Delay(100);
            }
            throw new TimeoutException("VRChat SDK avatar builder did not initialize.");
        }

        private static void OnUploadProgress(string status, float percentage)
        {
            string safeStatus = string.IsNullOrWhiteSpace(status) ? "Transferring" : status.Replace('\r', ' ').Replace('\n', ' ').Trim();
            float bounded = Math.Max(0f, Math.Min(1f, percentage));
            int bucket = (int)Math.Floor(bounded * 20f);
            if (string.Equals(lastUploadStatus, safeStatus, StringComparison.Ordinal) && bucket <= lastUploadBucket && bounded < 1f) return;
            lastUploadStatus = safeStatus;
            lastUploadBucket = bucket;
            DeploymentLog.Info("UPLOAD", safeStatus + " " + (bounded * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%");
        }

        private static void ResetUploadProgress()
        {
            lastUploadStatus = null;
            lastUploadBucket = -1;
        }
    }
}
