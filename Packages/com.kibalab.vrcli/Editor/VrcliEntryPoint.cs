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

namespace KibaLab.VRCLI.Editor
{
    public static class VrcliEntryPoint
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
            VrcliRequest request = null;
            try
            {
                VrcliLog.Start();
                request = VrcliRequest.FromEnvironment();
                VrcliLog.Phase("BOOT", "Deployment request accepted for " + request.Platform + ".");
                await VrcliAuthentication.LoginAsync(request);
                VrcliLog.Phase("CONTEXT", "Authentication complete. Resolving deployment context.");
                string scenePath = VrcliWorldDeployer.ResolveScenePath(request.ScenePath);
                LogDeploymentContext(request, scenePath);
                string worldId = await VrcliWorldDeployer.DeployAsync(request, scenePath);
                VrcliResult.Write(request.ResultFile, true, 0, worldId, request.CreateWorld, request.Platform, "complete", request.CreateWorld ? "World created, built, and uploaded." : "World build and upload completed.");
                VrcliLog.Phase("COMPLETE", "Deployment completed successfully for " + worldId + ".");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                int exitCode = Classify(exception);
                string resultFile = request != null ? request.ResultFile : Environment.GetEnvironmentVariable("VRCLI_RESULT_FILE");
                if (!string.IsNullOrWhiteSpace(resultFile))
                {
                    VrcliResult.Write(
                        resultFile,
                        false,
                        exitCode,
                        request != null ? request.BlueprintId : null,
                        false,
                        request != null ? request.Platform : null,
                        StageFor(exitCode),
                        Describe(exception));
                }
                VrcliLog.Info("FAILED", "Phase " + VrcliLog.CurrentPhase + " failed with exit code " + exitCode + ": " + Describe(exception));
                Debug.LogException(exception);
                EditorApplication.Exit(exitCode);
            }
        }

        private static void LogDeploymentContext(VrcliRequest request, string scenePath)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            APIUser user = APIUser.CurrentUser;
            VrcliLog.Info("CONTEXT", "Deployment context:");
            VrcliLog.Info("CONTEXT", "Account: " + user.displayName + " (" + user.id + ")");
            VrcliLog.Info("CONTEXT", "Project: " + PlayerSettings.productName);
            VrcliLog.Info("CONTEXT", "Project root: " + projectRoot);
            VrcliLog.Info("CONTEXT", "Project version: " + PlayerSettings.bundleVersion);
            VrcliLog.Info("CONTEXT", "Scene: " + scenePath);
            VrcliLog.Info("CONTEXT", "Requested platform: " + request.Platform);
            VrcliLog.Info("CONTEXT", "Unity active target: " + EditorUserBuildSettings.activeBuildTarget);
            VrcliLog.Info("CONTEXT", "Unity version: " + Application.unityVersion);
            VrcliLog.Info("CONTEXT", "VRChat SDK version: " + VRC.Tools.SdkVersion);
            VrcliLog.Info("CONTEXT", "VRChat SDK platform: " + VRC.Tools.Platform);
            VrcliLog.Info("CONTEXT", "VRCLI bridge version: " + VrcliLog.Version);
            VrcliLog.Info("CONTEXT", "Mode: " + (request.CreateWorld ? "create new private world" : "update existing world"));
            VrcliLog.Info("CONTEXT", "Blueprint: " + request.BlueprintId);
        }

        private static int Classify(Exception exception)
        {
            if (exception is ArgumentException || exception is FileNotFoundException) return 10;
            if (exception is VrcliAuthenticationException) return 30;
            if (exception is VrcliOwnershipException || exception is OwnershipException) return 60;
            if (exception is ValidationException || exception is BuilderException || exception is BuildBlockedException) return 40;
            if (exception is UploadException || exception is BundleExistsException) return 50;
            if (exception is ApiErrorException || exception is RequestFailedException) return 70;
            if (exception is TimeoutException) return 124;
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
