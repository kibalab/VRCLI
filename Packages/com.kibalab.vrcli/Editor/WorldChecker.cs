using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
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
            WorldDeployer.OpenScene(scenePath);

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
            report.WorldId = string.IsNullOrWhiteSpace(blueprintId) ? null : blueprintId;
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
                    builder.CreateValidationsGUI(new VisualElement());
                    CollectSdkIssues(builder, "GUIErrors", report.AddError);
                    CollectSdkIssues(builder, "GUIWarnings", report.AddWarning);
                    CollectSdkIssues(builder, "GUIInfos", report.AddInformation);
                    CollectSdkIssues(builder, "GUILinks", report.AddInformation);
                    CollectSdkIssues(builder, "GUIStats", report.AddInformation);
                }
                catch (Exception exception)
                {
                    report.AddError("VRChat SDK validation could not complete: " + Unwrap(exception).Message);
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

        private static void CollectSdkIssues(
            IVRCSdkWorldBuilderApi builder,
            string fieldName,
            Action<string> add)
        {
            FieldInfo panelField = FindField(builder.GetType(), "_builder");
            object panel = panelField != null ? panelField.GetValue(builder) : null;
            if (panel == null) throw new InvalidOperationException("The VRChat SDK validation panel is unavailable.");

            FieldInfo issuesField = FindField(panel.GetType(), fieldName);
            IDictionary issues = issuesField != null ? issuesField.GetValue(panel) as IDictionary : null;
            if (issues == null) throw new InvalidOperationException("The VRChat SDK " + fieldName + " report is unavailable.");

            foreach (DictionaryEntry entry in issues)
            {
                IEnumerable entries = entry.Value as IEnumerable;
                if (entries == null) continue;
                foreach (object issue in entries)
                {
                    FieldInfo textField = issue.GetType().GetField("issueText", BindingFlags.Instance | BindingFlags.Public);
                    string text = textField != null ? textField.GetValue(issue) as string : null;
                    if (!string.IsNullOrWhiteSpace(text)) add(text.Replace('\r', ' ').Replace('\n', ' ').Trim());
                }
            }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            while (type != null)
            {
                FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        private static Exception Unwrap(Exception exception)
        {
            TargetInvocationException invocation = exception as TargetInvocationException;
            return invocation != null && invocation.InnerException != null ? invocation.InnerException : exception;
        }
    }

    internal sealed class CheckReport
    {
        private readonly List<string> errors = new List<string>();
        private readonly List<string> warnings = new List<string>();
        private readonly List<string> information = new List<string>();

        public IReadOnlyList<string> Errors => errors;
        public IReadOnlyList<string> Warnings => warnings;
        public IReadOnlyList<string> Information => information;
        public string WorldId { get; set; }
        public bool Success => errors.Count == 0;

        public void AddError(string message) => Add(errors, message);
        public void AddWarning(string message) => Add(warnings, message);
        public void AddInformation(string message) => Add(information, message);

        private static void Add(List<string> destination, string message)
        {
            if (string.IsNullOrWhiteSpace(message) || destination.Contains(message)) return;
            destination.Add(message);
        }
    }
}
