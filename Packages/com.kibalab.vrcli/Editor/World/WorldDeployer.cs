using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Core;
using VRC.Editor;
using VRC.SDK3.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using Object = UnityEngine.Object;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class WorldDeployer
    {
        private static string lastUploadStatus;
        private static int lastUploadBucket = -1;
        private static bool uploadIncludesThumbnail;

        public static async Task<string> DeployAsync(DeploymentRequest request, string scenePath)
        {
            DeploymentLog.Phase("PREPARE", "Preparing project and scene for the VRChat SDK builder.");
            ContentScene.ValidatePlatform(request.Platform);
            EnsureVrchatProjectSettings();
            ContentScene.Open(scenePath);

            VRC_SceneDescriptor descriptor = Object.FindObjectOfType<VRC_SceneDescriptor>();
            if (descriptor == null) throw new InvalidOperationException("VRC_SceneDescriptor was not found in the selected scene.");

            PipelineManager pipeline = descriptor.GetComponent<PipelineManager>();
            if (pipeline == null) pipeline = Object.FindObjectOfType<PipelineManager>();
            if (pipeline == null) throw new InvalidOperationException("PipelineManager was not found in the selected scene.");
            DeploymentLog.Info("PREPARE", "VRC_SceneDescriptor and PipelineManager were found.");

            if (!request.IsNew)
            {
                bool usesSceneBlueprint = string.IsNullOrWhiteSpace(request.BlueprintId);
                string blueprintId = usesSceneBlueprint ? pipeline.blueprintId : request.BlueprintId;
                if (string.IsNullOrWhiteSpace(blueprintId))
                {
                    throw new ArgumentException(
                        "No Blueprint ID was provided and the selected scene's PipelineManager has no Blueprint ID.");
                }
                if (!blueprintId.StartsWith("wrld_", StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        "The selected world Blueprint ID must begin with 'wrld_': " + blueprintId);
                }

                request.UseBlueprintId(blueprintId);
                DeploymentLog.Info("PREPARE", usesSceneBlueprint
                    ? "Using the scene PipelineManager Blueprint ID: " + blueprintId
                    : "Using the requested Blueprint override: " + blueprintId);
            }

            // Let the SDK panel initialize in its new-world state. Giving it a not-yet-created
            // ID during initialization makes its background lookup treat the ID as invalid and clear it.
            pipeline.blueprintId = request.IsNew ? string.Empty : request.BlueprintId;
            VRCWorld world;
            if (request.IsNew)
            {
                if (!File.Exists(request.ThumbnailPath))
                {
                    throw new FileNotFoundException("Thumbnail was not found.", request.ThumbnailPath);
                }

                world = new VRCWorld
                {
                    Name = request.Title,
                    Description = request.Description,
                    Capacity = request.Capacity,
                    RecommendedCapacity = request.RecommendedCapacity,
                    Tags = new List<string>(request.Tags)
                };
                DeploymentLog.Phase("WORLD", "Prepared new private world metadata.");
                DeploymentLog.Info("WORLD", "Name: " + world.Name);
                DeploymentLog.Info("WORLD", "Capacity: " + world.RecommendedCapacity + " recommended / " + world.Capacity + " maximum");
                DeploymentLog.Info("WORLD", "Tags: " + (world.Tags.Count == 0 ? "none" : string.Join(", ", world.Tags)));
                DeploymentLog.Info("WORLD", "Thumbnail: " + request.ThumbnailPath + " (" + DeploymentLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length) + ")");
            }
            else
            {
                DeploymentLog.Phase("WORLD", "Fetching the existing VRChat world record.");
                world = await WorldMetadata.FetchOwnedAsync(request.BlueprintId);
                WorldMetadata.Apply(ref world, request);
                DeploymentLog.Info("WORLD", "World: " + world.Name + " (version " + world.Version + ")");
                DeploymentLog.Info("WORLD", "Release status: " + world.ReleaseStatus);
                DeploymentLog.Info("WORLD", "Capacity: " + world.RecommendedCapacity + " recommended / " + world.Capacity + " maximum");
                DeploymentLog.Info("WORLD", "Ownership confirmed for the authenticated account.");
                DeploymentLog.Info("WORLD", "Existing " + request.Platform + " bundle: " +
                    (string.IsNullOrWhiteSpace(world.GetLatestAssetUrlForPlatform(VRC.Tools.Platform)) ? "not present; a platform bundle will be added" : "present; a new file version will be uploaded"));
            }

            DeploymentLog.Phase("SDK", "Initializing the VRChat world builder.");
            IVRCSdkWorldBuilderApi builder = await GetBuilderAsync();
            DeploymentLog.Info("SDK", "VRChat world builder is ready.");
            pipeline.blueprintId = request.BlueprintId;
            if (request.IsNew)
            {
                SynchronizeBuilderBlueprint(builder, request.BlueprintId);
                await OwnershipAgreement.AcceptForNewContentAsync(request.BlueprintId, request.OwnershipAccepted);
            }
            else
            {
                await OwnershipAgreement.EnsureAsync(request.BlueprintId, request.OwnershipAccepted);
            }
            EventHandler<string> buildProgress = OnBuildProgress;
            EventHandler<string> buildSuccess = (sender, path) =>
                DeploymentLog.Info("BUILD", "SDK produced build artifact: " + path);
            builder.OnSdkBuildProgress += buildProgress;
            builder.OnSdkBuildSuccess += buildSuccess;
            (string path, string signature) build;
            try
            {
                DeploymentLog.Phase("BUILD", "Starting SDK validation and world asset-bundle build for " + request.Platform + ".");
                DeploymentLog.Info("SIGNATURE", "The SDK will generate a fresh signature for this bundle.");
                build = await builder.BuildWithSignature();
            }
            finally
            {
                builder.OnSdkBuildProgress -= buildProgress;
                builder.OnSdkBuildSuccess -= buildSuccess;
            }

            if (string.IsNullOrWhiteSpace(build.path) || !File.Exists(build.path) ||
                string.IsNullOrWhiteSpace(build.signature))
            {
                throw new BuilderException("The SDK did not return a valid built bundle path and signature.");
            }

            FileInfo bundle = new FileInfo(build.path);
            DeploymentLog.Info("BUILD", "Bundle ready: " + build.path);
            DeploymentLog.Info("BUILD", "Bundle size: " + DeploymentLog.FormatBytes(bundle.Length));
            DeploymentLog.Info("SIGNATURE", "Fresh bundle signature generated successfully (" + build.signature.Length + " characters; value hidden).");

            world.UdonProducts = descriptor.udonProducts;
            ResetUploadProgress(request.IsNew);
            DeploymentLog.Phase("UPLOAD", request.IsNew
                ? "Starting bundle, thumbnail, signature, and metadata upload for a new world."
                : request.HasMetadataUpdate || request.UpdateThumbnail
                    ? "Starting platform bundle, signature, and requested metadata update for the existing world."
                    : "Starting platform bundle and refreshed-signature upload for the existing world.");
            DeploymentLog.Info("UPLOAD", "Target platform: " + request.Platform);
            DeploymentLog.Info("UPLOAD", "Bundle payload: " + DeploymentLog.FormatBytes(bundle.Length));
            if (request.IsNew)
                DeploymentLog.Info("UPLOAD", "Thumbnail payload: " + DeploymentLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length));
            DeploymentLog.Info("SIGNATURE", request.IsNew
                ? "The generated signature will be stored with the new world record."
                : "The world record will be updated with the generated signature after bundle upload.");
            VRCWorld uploaded;
            if (request.IsNew)
            {
                uploaded = await VRCApi.CreateNewWorld(
                    request.BlueprintId,
                    world,
                    build.path,
                    request.ThumbnailPath,
                    build.signature,
                    OnApiUploadProgress);
            }
            else
            {
                uploaded = await VRCApi.UpdateWorldBundle(
                    request.BlueprintId,
                    world,
                    build.path,
                    build.signature,
                    OnApiUploadProgress);

                if (request.HasMetadataUpdate)
                {
                    DeploymentLog.Info("UPLOAD", "Saving requested world metadata fields.");
                    uploaded = await VRCApi.UpdateWorldInfo(request.BlueprintId, world);
                    DeploymentLog.Info("UPLOAD", "World metadata update completed.");
                }

                if (request.UpdateThumbnail)
                {
                    ResetUploadProgress(false);
                    DeploymentLog.Info("UPLOAD", "Uploading replacement thumbnail: " + request.ThumbnailPath +
                        " (" + DeploymentLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length) + ")");
                    uploaded = await VRCApi.UpdateWorldImage(
                        request.BlueprintId,
                        uploaded,
                        request.ThumbnailPath,
                        OnApiUploadProgress);
                    DeploymentLog.Info("UPLOAD", "Thumbnail update completed.");
                }
            }

            if (string.IsNullOrWhiteSpace(uploaded.ID))
                throw new UploadException("VRChat did not return the uploaded world record.");
            DeploymentLog.Info("UPLOAD", request.IsNew
                ? "Bundle and thumbnail uploads completed; the new world record was created."
                : "Bundle upload completed; the platform asset reference was updated.");
            DeploymentLog.Info("SIGNATURE", "VRChat confirmed the world-signature update.");
            DeploymentLog.Info("UPLOAD", "Server world version: " + uploaded.Version);
            DeploymentLog.Info("UPLOAD", "Server updated time: " + uploaded.UpdatedAt.ToUniversalTime().ToString("O"));
            return uploaded.ID;
        }

        private static void SynchronizeBuilderBlueprint(IVRCSdkWorldBuilderApi builder, string blueprintId)
        {
            FieldInfo lastBlueprint = builder.GetType().GetField(
                "_lastBlueprintId",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (lastBlueprint == null)
            {
                Type baseType = builder.GetType().BaseType;
                while (lastBlueprint == null && baseType != null)
                {
                    lastBlueprint = baseType.GetField("_lastBlueprintId", BindingFlags.Instance | BindingFlags.NonPublic);
                    baseType = baseType.BaseType;
                }
            }
            if (lastBlueprint != null) lastBlueprint.SetValue(builder, blueprintId);
        }

        private static void EnsureVrchatProjectSettings()
        {
            if (!UpdateLayers.AreLayersSetup())
            {
                DeploymentLog.Info("PREPARE", "Configuring required VRChat project layers.");
                UpdateLayers.SetupEditorLayers();
                DeploymentLog.Info("PREPARE", "Required VRChat project layers were configured.");
            }
            else
            {
                DeploymentLog.Info("PREPARE", "Required VRChat project layers are configured.");
            }
            if (!UpdateLayers.IsCollisionLayerMatrixSetup())
            {
                DeploymentLog.Info("PREPARE", "Configuring the required VRChat collision matrix.");
                UpdateLayers.SetupCollisionLayerMatrix();
                DeploymentLog.Info("PREPARE", "VRChat collision matrix was configured.");
            }
            else
            {
                DeploymentLog.Info("PREPARE", "VRChat collision matrix is configured.");
            }
        }

        internal static async Task<IVRCSdkWorldBuilderApi> GetBuilderAsync()
        {
            EditorWindow window = EditorWindow.GetWindow<VRCSdkControlPanel>(false, "VRChat SDK", false);
            window.Show();

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                IVRCSdkWorldBuilderApi builder;
                if (VRCSdkControlPanel.TryGetBuilder(out builder)) return builder;
                await Task.Delay(100);
            }
            throw new TimeoutException("VRChat SDK world builder did not initialize.");
        }

        private static void OnBuildProgress(object sender, string status)
        {
            DeploymentLog.Info("BUILD", status);
        }

        private static void OnApiUploadProgress(string status, float percentage)
        {
            string safeStatus = string.IsNullOrWhiteSpace(status)
                ? "Transferring"
                : status.Replace('\r', ' ').Replace('\n', ' ').Trim();
            float bounded = Math.Max(0f, Math.Min(1f, percentage));
            int bucket = (int)Math.Floor(bounded * 20f);
            if (string.Equals(lastUploadStatus, safeStatus, StringComparison.Ordinal) &&
                bucket <= lastUploadBucket && bounded < 1f)
                return;

            lastUploadStatus = safeStatus;
            lastUploadBucket = bucket;
            string component = "bundle";
            float componentProgress = bounded;
            if (uploadIncludesThumbnail)
            {
                if (bounded < 0.5f)
                {
                    componentProgress = bounded * 2f;
                }
                else
                {
                    component = "thumbnail";
                    componentProgress = (bounded - 0.5f) * 2f;
                }
            }

            DeploymentLog.Info("UPLOAD", component + ": " + safeStatus + " " +
                (componentProgress * 100f).ToString("F1", CultureInfo.InvariantCulture) + "% (overall " +
                (bounded * 100f).ToString("F1", CultureInfo.InvariantCulture) + "%)");
        }

        private static void ResetUploadProgress(bool includesThumbnail)
        {
            lastUploadStatus = null;
            lastUploadBucket = -1;
            uploadIncludesThumbnail = includesThumbnail;
        }
    }
}
