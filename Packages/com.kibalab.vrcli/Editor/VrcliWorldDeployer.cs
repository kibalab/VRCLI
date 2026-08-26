using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using VRC.Core;
using VRC.Editor;
using VRC.SDK3.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;
using Object = UnityEngine.Object;

namespace KibaLab.VRCLI.Editor
{
    internal static class VrcliWorldDeployer
    {
        private static string lastUploadStatus;
        private static int lastUploadBucket = -1;
        private static bool uploadIncludesThumbnail;

        public static async Task<string> DeployAsync(VrcliRequest request, string scenePath)
        {
            VrcliLog.Phase("PREPARE", "Preparing project and scene for the VRChat SDK builder.");
            ValidateActivePlatform(request.Platform);
            EnsureVrchatProjectSettings();
            OpenScene(scenePath);

            VRC_SceneDescriptor descriptor = Object.FindObjectOfType<VRC_SceneDescriptor>();
            if (descriptor == null) throw new InvalidOperationException("VRC_SceneDescriptor was not found in the selected scene.");

            PipelineManager pipeline = descriptor.GetComponent<PipelineManager>();
            if (pipeline == null) pipeline = Object.FindObjectOfType<PipelineManager>();
            if (pipeline == null) throw new InvalidOperationException("PipelineManager was not found in the selected scene.");
            VrcliLog.Info("PREPARE", "VRC_SceneDescriptor and PipelineManager were found.");

            // Let the SDK panel initialize in its new-world state. Giving it a not-yet-created
            // ID during initialization makes its background lookup treat the ID as invalid and clear it.
            pipeline.blueprintId = request.CreateWorld ? string.Empty : request.BlueprintId;
            VRCWorld world;
            if (request.CreateWorld)
            {
                if (!File.Exists(request.ThumbnailPath))
                {
                    throw new FileNotFoundException("Thumbnail was not found.", request.ThumbnailPath);
                }

                world = new VRCWorld
                {
                    Name = request.WorldName,
                    Description = request.WorldDescription,
                    Capacity = request.Capacity,
                    RecommendedCapacity = request.RecommendedCapacity,
                    Tags = new List<string>(request.Tags)
                };
                VrcliLog.Phase("WORLD", "Prepared new private world metadata.");
                VrcliLog.Info("WORLD", "Name: " + world.Name);
                VrcliLog.Info("WORLD", "Capacity: " + world.RecommendedCapacity + " recommended / " + world.Capacity + " maximum");
                VrcliLog.Info("WORLD", "Tags: " + (world.Tags.Count == 0 ? "none" : string.Join(", ", world.Tags)));
                VrcliLog.Info("WORLD", "Thumbnail: " + request.ThumbnailPath + " (" + VrcliLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length) + ")");
            }
            else
            {
                VrcliLog.Phase("WORLD", "Fetching the existing VRChat world record.");
                world = await VRCApi.GetWorld(request.BlueprintId, true);
                if (string.IsNullOrWhiteSpace(world.ID)) throw new InvalidOperationException("VRChat did not return world data for " + request.BlueprintId + ".");
                if (APIUser.CurrentUser == null || world.AuthorId != APIUser.CurrentUser.id)
                {
                    throw new VrcliOwnershipException("The logged-in account does not own " + request.BlueprintId + ".");
                }
                ApplyExistingWorldMetadata(ref world, request);
                VrcliLog.Info("WORLD", "World: " + world.Name + " (version " + world.Version + ")");
                VrcliLog.Info("WORLD", "Release status: " + world.ReleaseStatus);
                VrcliLog.Info("WORLD", "Capacity: " + world.RecommendedCapacity + " recommended / " + world.Capacity + " maximum");
                VrcliLog.Info("WORLD", "Ownership confirmed for the authenticated account.");
                VrcliLog.Info("WORLD", "Existing " + request.Platform + " bundle: " +
                    (string.IsNullOrWhiteSpace(world.GetLatestAssetUrlForPlatform(VRC.Tools.Platform)) ? "not present; a platform bundle will be added" : "present; a new file version will be uploaded"));
            }

            VrcliLog.Phase("SDK", "Initializing the VRChat world builder.");
            IVRCSdkWorldBuilderApi builder = await GetBuilderAsync();
            VrcliLog.Info("SDK", "VRChat world builder is ready.");
            pipeline.blueprintId = request.BlueprintId;
            if (request.CreateWorld)
            {
                SynchronizeBuilderBlueprint(builder, request.BlueprintId);
                await VrcliOwnershipAgreement.AcceptForNewContentAsync(request.BlueprintId, request.AcceptContentOwnership);
            }
            else
            {
                await VrcliOwnershipAgreement.EnsureAsync(request.BlueprintId, request.AcceptContentOwnership);
            }
            EventHandler<string> buildProgress = OnBuildProgress;
            EventHandler<string> buildSuccess = (sender, path) =>
                VrcliLog.Info("BUILD", "SDK produced build artifact: " + path);
            builder.OnSdkBuildProgress += buildProgress;
            builder.OnSdkBuildSuccess += buildSuccess;
            (string path, string signature) build;
            try
            {
                VrcliLog.Phase("BUILD", "Starting SDK validation and world asset-bundle build for " + request.Platform + ".");
                VrcliLog.Info("SIGNATURE", "The SDK will generate a fresh signature for this bundle.");
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
            VrcliLog.Info("BUILD", "Bundle ready: " + build.path);
            VrcliLog.Info("BUILD", "Bundle size: " + VrcliLog.FormatBytes(bundle.Length));
            VrcliLog.Info("SIGNATURE", "Fresh bundle signature generated successfully (" + build.signature.Length + " characters; value hidden).");

            world.UdonProducts = descriptor.udonProducts;
            ResetUploadProgress(request.CreateWorld);
            VrcliLog.Phase("UPLOAD", request.CreateWorld
                ? "Starting bundle, thumbnail, signature, and metadata upload for a new world."
                : request.HasWorldInfoUpdate || request.UpdateThumbnail
                    ? "Starting platform bundle, signature, and requested metadata update for the existing world."
                    : "Starting platform bundle and refreshed-signature upload for the existing world.");
            VrcliLog.Info("UPLOAD", "Target platform: " + request.Platform);
            VrcliLog.Info("UPLOAD", "Bundle payload: " + VrcliLog.FormatBytes(bundle.Length));
            if (request.CreateWorld)
                VrcliLog.Info("UPLOAD", "Thumbnail payload: " + VrcliLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length));
            VrcliLog.Info("SIGNATURE", request.CreateWorld
                ? "The generated signature will be stored with the new world record."
                : "The world record will be updated with the generated signature after bundle upload.");
            VRCWorld uploaded;
            if (request.CreateWorld)
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

                if (request.HasWorldInfoUpdate)
                {
                    VrcliLog.Info("UPLOAD", "Saving requested world metadata fields.");
                    uploaded = await VRCApi.UpdateWorldInfo(request.BlueprintId, world);
                    VrcliLog.Info("UPLOAD", "World metadata update completed.");
                }

                if (request.UpdateThumbnail)
                {
                    ResetUploadProgress(false);
                    VrcliLog.Info("UPLOAD", "Uploading replacement thumbnail: " + request.ThumbnailPath +
                        " (" + VrcliLog.FormatBytes(new FileInfo(request.ThumbnailPath).Length) + ")");
                    uploaded = await VRCApi.UpdateWorldImage(
                        request.BlueprintId,
                        uploaded,
                        request.ThumbnailPath,
                        OnApiUploadProgress);
                    VrcliLog.Info("UPLOAD", "Thumbnail update completed.");
                }
            }

            if (string.IsNullOrWhiteSpace(uploaded.ID))
                throw new UploadException("VRChat did not return the uploaded world record.");
            VrcliLog.Info("UPLOAD", request.CreateWorld
                ? "Bundle and thumbnail uploads completed; the new world record was created."
                : "Bundle upload completed; the platform asset reference was updated.");
            VrcliLog.Info("SIGNATURE", "VRChat confirmed the world-signature update.");
            VrcliLog.Info("UPLOAD", "Server world version: " + uploaded.Version);
            VrcliLog.Info("UPLOAD", "Server updated time: " + uploaded.UpdatedAt.ToUniversalTime().ToString("O"));
            return uploaded.ID;
        }

        private static void ApplyExistingWorldMetadata(ref VRCWorld world, VrcliRequest request)
        {
            if (request.UpdateThumbnail && !File.Exists(request.ThumbnailPath))
                throw new FileNotFoundException("Replacement thumbnail was not found.", request.ThumbnailPath);

            if (request.UpdateWorldName)
            {
                world.Name = request.WorldName;
                VrcliLog.Info("WORLD", "Requested name update: " + world.Name);
            }
            if (request.UpdateWorldDescription)
            {
                world.Description = request.WorldDescription ?? string.Empty;
                VrcliLog.Info("WORLD", "Requested description update (" + world.Description.Length + " characters).");
            }
            if (request.UpdateCapacity)
            {
                world.Capacity = request.Capacity;
                VrcliLog.Info("WORLD", "Requested maximum capacity update: " + world.Capacity);
            }
            if (request.UpdateRecommendedCapacity)
            {
                if (request.RecommendedCapacity > world.Capacity)
                {
                    throw new ArgumentException(
                        "Recommended capacity " + request.RecommendedCapacity +
                        " exceeds the effective maximum capacity " + world.Capacity + ".");
                }
                world.RecommendedCapacity = request.RecommendedCapacity;
                VrcliLog.Info("WORLD", "Requested recommended capacity update: " + world.RecommendedCapacity);
            }
            else if (request.UpdateCapacity && world.RecommendedCapacity > world.Capacity)
            {
                world.RecommendedCapacity = world.Capacity;
                VrcliLog.Info("WORLD", "Recommended capacity was clamped to the new maximum: " + world.RecommendedCapacity);
            }
            if (request.UpdateWorldTags)
            {
                List<string> tags = world.Tags ?? new List<string>();
                foreach (string tag in request.Tags)
                {
                    if (!tags.Contains(tag)) tags.Add(tag);
                }
                world.Tags = tags;
                VrcliLog.Info("WORLD", "Requested tags merged: " +
                    (request.Tags.Length == 0 ? "no additions" : string.Join(", ", request.Tags)));
            }
            if (request.UpdateThumbnail)
                VrcliLog.Info("WORLD", "Requested thumbnail replacement: " + request.ThumbnailPath);
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

        internal static string ResolveScenePath(string scenePath)
        {
            string selected = scenePath;
            if (string.IsNullOrWhiteSpace(selected))
            {
                foreach (EditorBuildSettingsScene scene in EditorBuildSettings.scenes)
                {
                    if (!scene.enabled) continue;
                    selected = scene.path;
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(selected))
            {
                throw new ArgumentException("No scene was specified and EditorBuildSettings has no enabled scene.");
            }

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string fullPath = Path.IsPathRooted(selected)
                ? selected
                : Path.Combine(projectRoot, selected.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("Scene was not found.", selected);
            }
            return selected;
        }

        private static void OpenScene(string scenePath)
        {
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            VrcliLog.Info("PREPARE", "Opened build scene: " + scenePath);
        }

        private static void ValidateActivePlatform(string requestedPlatform)
        {
            BuildTarget expected = requestedPlatform == "Android" ? BuildTarget.Android : BuildTarget.StandaloneWindows64;
            if (EditorUserBuildSettings.activeBuildTarget != expected)
            {
                throw new InvalidOperationException(
                    "Unity active build target is " + EditorUserBuildSettings.activeBuildTarget +
                    ", but VRCLI requested " + expected + ".");
            }

            VrcliLog.Info("PREPARE", "Active Unity build target matches " + expected + ".");

            if (expected == BuildTarget.Android && EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
            {
                EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
                VrcliLog.Info("PREPARE", "Android texture compression target changed to ASTC.");
            }
            else if (expected == BuildTarget.Android)
            {
                VrcliLog.Info("PREPARE", "Android texture compression target is ASTC.");
            }
        }

        private static void EnsureVrchatProjectSettings()
        {
            if (!UpdateLayers.AreLayersSetup())
            {
                VrcliLog.Info("PREPARE", "Configuring required VRChat project layers.");
                UpdateLayers.SetupEditorLayers();
                VrcliLog.Info("PREPARE", "Required VRChat project layers were configured.");
            }
            else
            {
                VrcliLog.Info("PREPARE", "Required VRChat project layers are configured.");
            }
            if (!UpdateLayers.IsCollisionLayerMatrixSetup())
            {
                VrcliLog.Info("PREPARE", "Configuring the required VRChat collision matrix.");
                UpdateLayers.SetupCollisionLayerMatrix();
                VrcliLog.Info("PREPARE", "VRChat collision matrix was configured.");
            }
            else
            {
                VrcliLog.Info("PREPARE", "VRChat collision matrix is configured.");
            }
        }

        private static async Task<IVRCSdkWorldBuilderApi> GetBuilderAsync()
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
            VrcliLog.Info("BUILD", status);
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

            VrcliLog.Info("UPLOAD", component + ": " + safeStatus + " " +
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
