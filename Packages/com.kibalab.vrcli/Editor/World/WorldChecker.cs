using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC.Editor;
using VRC.Core;
using VRC.SDK3.Editor;
using VRC.SDKBase;
using VRC.SDKBase.Editor;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class WorldChecker
    {
        public static async Task<CheckReport> RunAsync(DeploymentRequest request, string scenePath)
        {
            CheckReport report = new CheckReport();
            DeploymentLog.Phase("PREPARE", "Opening the target scene for a read-only preflight check.");
            BuildTarget expectedTarget = request.Platform == "Android"
                ? BuildTarget.Android
                : BuildTarget.StandaloneWindows64;
            if (EditorUserBuildSettings.activeBuildTarget != expectedTarget)
            {
                report.AddError("Unity active build target is " + EditorUserBuildSettings.activeBuildTarget +
                    ", but the check requested " + expectedTarget + ".");
            }
            if (expectedTarget == BuildTarget.Android &&
                EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
            {
                report.AddError("Android texture compression must be ASTC for VRChat uploads.");
            }
            ContentScene.Open(scenePath);

            VRC_SceneDescriptor descriptor = UnityEngine.Object.FindObjectOfType<VRC_SceneDescriptor>();
            if (descriptor == null)
            {
                report.AddError("VRC_SceneDescriptor was not found in the selected scene.");
                return report;
            }

            PipelineManager pipeline = descriptor.GetComponent<PipelineManager>() ??
                                       UnityEngine.Object.FindObjectOfType<PipelineManager>();
            if (pipeline == null)
            {
                report.AddError("PipelineManager was not found in the selected scene.");
                return report;
            }

            string blueprintId = string.IsNullOrWhiteSpace(request.BlueprintId)
                ? pipeline.blueprintId
                : request.BlueprintId;
            report.Blueprint = string.IsNullOrWhiteSpace(blueprintId) ? null : blueprintId;
            if (!string.IsNullOrWhiteSpace(request.BlueprintId)) pipeline.blueprintId = request.BlueprintId;

            if (!UpdateLayers.AreLayersSetup())
                report.AddError("Required VRChat project layers are not configured.");
            if (!UpdateLayers.IsCollisionLayerMatrixSetup())
                report.AddError("The required VRChat collision matrix is not configured.");

            if (string.IsNullOrWhiteSpace(blueprintId))
            {
                report.AddWarning("No Blueprint ID is assigned; server ownership and upload-consent checks were skipped.");
            }
            else if (!blueprintId.StartsWith("wrld_", StringComparison.Ordinal))
            {
                report.AddError("The scene PipelineManager contains an invalid world Blueprint ID: " + blueprintId);
            }
            else
            {
                DeploymentLog.Phase("WORLD", "Checking the existing world record and authenticated ownership.");
                await WorldMetadata.FetchOwnedAsync(blueprintId);
                if (await OwnershipAgreement.CheckAsync(blueprintId))
                    DeploymentLog.Info("WORLD", "Content ownership consent is already recorded.");
                else
                    report.AddWarning("Content ownership consent is not recorded; deploy with --yes before uploading.");
            }

            DeploymentLog.Phase("SDK", "Running the VRChat SDK panel validation without building or uploading.");
            IVRCSdkWorldBuilderApi builder = await WorldDeployer.GetBuilderAsync();
            string invalidReason;
            if (!builder.IsValidBuilder(out invalidReason))
            {
                report.AddError(string.IsNullOrWhiteSpace(invalidReason)
                    ? "The VRChat SDK world builder rejected the current scene."
                    : invalidReason.Replace('\n', ' '));
            }
            else
            {
                try
                {
                    SdkDiagnostics.Collect(builder, report);
                }
                catch (Exception exception)
                {
                    report.AddError("VRChat SDK validation could not complete: " + exception.GetBaseException().Message);
                }
            }

            DeploymentLog.Phase("CHECK", "VRChat SDK preflight diagnostics collected.");
            foreach (string message in report.Errors) DeploymentLog.Info("CHECK", "ERROR: " + message);
            foreach (string message in report.Warnings) DeploymentLog.Info("CHECK", "WARNING: " + message);
            foreach (string message in report.Information) DeploymentLog.Info("CHECK", "INFO: " + message);
            DeploymentLog.Info("CHECK", "Summary: " + report.Errors.Count + " error(s), " +
                report.Warnings.Count + " warning(s), " + report.Information.Count + " informational message(s).");
            DeploymentLog.Info("CHECK", "Dry run complete; no bundle was built and no VRChat record was changed.");
            return report;
        }

    }
}
