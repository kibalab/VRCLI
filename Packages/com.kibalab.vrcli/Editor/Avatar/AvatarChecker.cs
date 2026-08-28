using System;
using System.Threading.Tasks;
using UnityEditor;
using VRC.SDK3A.Editor;

namespace KibaLab.WorldDeployment.Editor
{
    internal static class AvatarChecker
    {
        public static async Task<CheckReport> RunAsync(DeploymentRequest request, string scenePath)
        {
            CheckReport report = new CheckReport();
            DeploymentLog.Phase("PREPARE", "Opening the avatar scene for a read-only preflight check.");
            BuildTarget expected = request.Platform == "Android" ? BuildTarget.Android : BuildTarget.StandaloneWindows64;
            if (EditorUserBuildSettings.activeBuildTarget != expected)
                report.AddError("Unity active build target is " + EditorUserBuildSettings.activeBuildTarget + ", but the check requested " + expected + ".");
            if (expected == BuildTarget.Android && EditorUserBuildSettings.androidBuildSubtarget != MobileTextureSubtarget.ASTC)
                report.AddError("Android texture compression must be ASTC for VRChat uploads.");

            ContentScene.Open(scenePath);
            AvatarTarget target;
            try
            {
                target = AvatarTarget.Find(request.BlueprintId);
            }
            catch (Exception exception)
            {
                report.AddError(exception.Message);
                return report;
            }

            string blueprint = target.Pipeline.blueprintId;
            report.Blueprint = string.IsNullOrWhiteSpace(blueprint) ? null : blueprint;
            if (string.IsNullOrWhiteSpace(blueprint))
            {
                report.AddWarning("No Avatar Blueprint ID is assigned; this will create a new private avatar and requires --title, --thumbnail, and --yes during deploy.");
            }
            else if (!blueprint.StartsWith("avtr_", StringComparison.Ordinal))
            {
                report.AddError("The selected PipelineManager contains an invalid avatar Blueprint ID: " + blueprint);
            }
            else
            {
                DeploymentLog.Phase("AVATAR", "Checking the existing avatar record and authenticated ownership.");
                await AvatarMetadata.FetchOwnedAsync(blueprint);
                if (await OwnershipAgreement.CheckAsync(blueprint))
                    DeploymentLog.Info("AVATAR", "Content ownership consent is already recorded.");
                else
                    report.AddWarning("Content ownership consent is not recorded; deploy with --yes before uploading.");
            }

            DeploymentLog.Phase("SDK", "Running VRChat avatar SDK validation without building or uploading.");
            IVRCSdkAvatarBuilderApi builder = await AvatarDeployer.GetBuilderAsync();
            builder.SelectAvatar(target.GameObject);
            string invalidReason;
            if (!builder.IsValidBuilder(out invalidReason))
                report.AddError(string.IsNullOrWhiteSpace(invalidReason) ? "The VRChat SDK avatar builder rejected the selected avatar." : invalidReason.Replace('\n', ' '));
            else
            {
                try { SdkDiagnostics.Collect(builder, report); }
                catch (Exception exception) { report.AddError("VRChat SDK validation could not complete: " + exception.GetBaseException().Message); }
            }

            DeploymentLog.Phase("CHECK", "VRChat avatar SDK preflight diagnostics collected.");
            foreach (string message in report.Errors) DeploymentLog.Info("CHECK", "ERROR: " + message);
            foreach (string message in report.Warnings) DeploymentLog.Info("CHECK", "WARNING: " + message);
            foreach (string message in report.Information) DeploymentLog.Info("CHECK", "INFO: " + message);
            DeploymentLog.Info("CHECK", "Summary: " + report.Errors.Count + " error(s), " + report.Warnings.Count + " warning(s), " + report.Information.Count + " informational message(s).");
            DeploymentLog.Info("CHECK", "Dry run complete; no bundle was built and no VRChat record was changed.");
            return report;
        }
    }
}
