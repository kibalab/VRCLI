using System;
using System.Linq;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using VRC;
using VRC.Core;
using VRC.SDKBase.Editor;
using VRC.SDKBase.Editor.Api;

namespace KibaLab.WorldDeployment.Editor
{
    public static class DeploymentEntryPoint
    {
        private static bool running;

        public static void Run()
        {
            if (running)
            {
                Debug.LogError("[VRCLI] A deployment is already running.");
                EditorApplication.Exit(125);
                return;
            }

            running = true;
            EditorApplication.delayCall += Begin;
        }

        private static async void Begin()
        {
            DeploymentRequest request = null;
            try
            {
                DeploymentLog.Start();
                request = DeploymentRequest.FromEnvironment();
                DeploymentLog.Phase("BOOT", request.Operation + " request accepted for " + request.Platform + ".");
                await Authentication.LoginAsync(request);
                DeploymentLog.Phase("CONTEXT", "Authentication complete. Resolving operation context.");

                string scenePath = ContentScene.Resolve(request.ScenePath);
                LogDeploymentContext(request, scenePath);
                IContentHandler handler = CreateHandler(request.ContentType);
                if (request.Operation == RequestOperation.Check)
                {
                    CheckReport report = await handler.CheckAsync(request, scenePath);
                    int checkExitCode = report.Success ? 0 : 40;
                    DeploymentResult.Write(
                        request.ResultFile,
                        report.Success,
                        checkExitCode,
                        report.Blueprint,
                        false,
                        request.Platform,
                        request.ContentType.ToString(),
                        "check",
                        report.Success
                            ? "Preflight check completed without blocking errors; no bundle was built or uploaded."
                            : "Preflight check found upload-blocking problems; no bundle was built or uploaded.",
                        report.Errors.ToArray(),
                        report.Warnings.ToArray(),
                        report.Information.ToArray());
                    DeploymentLog.Phase("COMPLETE", report.Success
                        ? "Preflight check passed without server changes."
                        : "Preflight check completed with blocking errors.");
                    EditorApplication.Exit(checkExitCode);
                    return;
                }

                DeploymentOutcome outcome = await handler.DeployAsync(request, scenePath);
                DeploymentResult.Write(request.ResultFile, true, 0, outcome.Blueprint, outcome.Created, request.Platform, request.ContentType.ToString(), "complete", outcome.Message, outcome: outcome);
                DeploymentLog.Phase("COMPLETE", "Deployment completed successfully for " + outcome.Blueprint + ".");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                int exitCode = Classify(exception);
                TargetSelectionException targetSelection = exception as TargetSelectionException;
                string resultFile = request != null ? request.ResultFile : Environment.GetEnvironmentVariable(DeploymentEnvironment.ResultFile);
                if (!string.IsNullOrWhiteSpace(resultFile))
                {
                    DeploymentResult.Write(
                        resultFile,
                        false,
                        exitCode,
                        request != null ? request.BlueprintId : null,
                        false,
                        request != null ? request.Platform : null,
                        request != null ? request.ContentType.ToString() : null,
                        targetSelection != null ? "target-selection" :
                        exception is OperationCanceledException ? "cancelled" : StageFor(exitCode),
                        Describe(exception),
                        targets: targetSelection != null ? targetSelection.Targets : null);
                }
                DeploymentLog.Info("FAILED", "Phase " + DeploymentLog.CurrentPhase + " failed with exit code " + exitCode + ": " + Describe(exception));
                Debug.LogException(exception);
                EditorApplication.Exit(exitCode);
            }
        }

        private static void LogDeploymentContext(DeploymentRequest request, string scenePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            APIUser user = APIUser.CurrentUser;
            DeploymentLog.Info("CONTEXT", "Deployment context:");
            DeploymentLog.Info("CONTEXT", "Account: " + user.displayName + " (" + user.id + ")");
            DeploymentLog.Info("CONTEXT", "Project: " + PlayerSettings.productName);
            DeploymentLog.Info("CONTEXT", "Project root: " + projectRoot);
            DeploymentLog.Info("CONTEXT", "Project version: " + PlayerSettings.bundleVersion);
            if (!string.IsNullOrWhiteSpace(scenePath))
            {
                DeploymentLog.Info("CONTEXT", "Scene: " + scenePath);
                DeploymentLog.Info("CONTEXT", "Requested platform: " + request.Platform);
                DeploymentLog.Info("CONTEXT", "Unity active target: " + EditorUserBuildSettings.activeBuildTarget);
            }
            DeploymentLog.Info("CONTEXT", "Unity version: " + Application.unityVersion);
            DeploymentLog.Info("CONTEXT", "VRChat SDK version: " + VRC.Tools.SdkVersion);
            DeploymentLog.Info("CONTEXT", "VRChat SDK platform: " + VRC.Tools.Platform);
            DeploymentLog.Info("CONTEXT", "VRCLI bridge version: " + DeploymentLog.Version);
            DeploymentLog.Info("CONTEXT", "Mode: " + request.Operation);
            DeploymentLog.Info("CONTEXT", "Content type: " + request.ContentType);
            if (!string.IsNullOrWhiteSpace(request.BlueprintId))
                DeploymentLog.Info("CONTEXT", "Blueprint: " + request.BlueprintId);
            if (!string.IsNullOrWhiteSpace(request.TargetPath))
                DeploymentLog.Info("CONTEXT", "Avatar target: " + request.TargetPath);
        }

        private static IContentHandler CreateHandler(ContentType contentType)
        {
            Type handlerType = TypeCache.GetTypesDerivedFrom<IContentHandler>()
                .FirstOrDefault(type => !type.IsAbstract && !type.IsInterface &&
                    ((IContentHandler)Activator.CreateInstance(type)).ContentType == contentType);
            if (handlerType == null)
                throw new InvalidOperationException("The " + contentType + " SDK bridge is unavailable. Ensure the matching VRChat SDK package is installed.");
            return (IContentHandler)Activator.CreateInstance(handlerType);
        }

        private static int Classify(Exception exception)
        {
            if (exception is ArgumentException || exception is FileNotFoundException || exception is TargetSelectionException) return 10;
            if (exception is LoginException) return 30;
            if (exception is ContentOwnershipException || exception is OwnershipException) return 60;
            if (exception is ValidationException || exception is BuilderException || exception is BuildBlockedException) return 40;
            if (exception is UploadException || exception is BundleExistsException) return 50;
            if (exception is ApiErrorException || exception is RequestFailedException) return 70;
            if (exception is TimeoutException) return 124;
            if (exception is OperationCanceledException) return 130;
            return 125;
        }

        private static string StageFor(int exitCode)
        {
            switch (exitCode)
            {
                case 10: return "project";
                case 30: return "authentication";
                case 40: return "build";
                case 50: return "upload";
                case 60: return "ownership";
                case 124: return "timeout";
                case 130: return "cancelled";
                default: return "unexpected";
            }
        }

        private static string Describe(Exception exception)
        {
            ValidationException validation = exception as ValidationException;
            if (validation != null && validation.Errors != null && validation.Errors.Count > 0)
            {
                return validation.Message + ": " + string.Join("; ", validation.Errors.Where(
                    message => !string.IsNullOrWhiteSpace(message)));
            }

            ApiErrorException apiError = exception as ApiErrorException;
            if (apiError != null)
            {
                return apiError.ErrorMessage + " (HTTP " + (int)apiError.StatusCode + ")";
            }

            RequestFailedException requestFailed = exception as RequestFailedException;
            if (requestFailed != null && requestFailed.StatusCode != 0)
            {
                return requestFailed.Message + " (HTTP " + (int)requestFailed.StatusCode + ")";
            }

            return exception.Message;
        }
    }
}
