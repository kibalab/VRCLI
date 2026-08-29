using System;
using System.IO;
using UnityEngine;

namespace KibaLab.WorldDeployment.Editor
{
    [Serializable]
    internal sealed class DeploymentResult
    {
        public bool Success;
        public int ExitCode;
        public string Blueprint;
        public bool Created;
        public string Platform;
        public string ContentType;
        public string Stage;
        public string Message;
        public string[] Errors;
        public string[] Warnings;
        public string[] Information;
        public ContentTarget[] Targets;
        public string VrcliVersion;
        public string UnityVersion;
        public string SdkVersion;
        public long DurationMs;
        public PhaseTiming[] PhaseTimings;
        public BuildArtifact Artifact;
        public int PreviousVersion;
        public int ServerVersion;
        public string ServerUpdatedAt;

        public static void Write(
            string resultFile,
            bool success,
            int exitCode,
            string blueprint,
            bool created,
            string platform,
            string contentType,
            string stage,
            string message,
            string[] errors = null,
            string[] warnings = null,
            string[] information = null,
            ContentTarget[] targets = null,
            DeploymentOutcome outcome = null)
        {
            DeploymentResult result = new DeploymentResult
            {
                Success = success,
                ExitCode = exitCode,
                Blueprint = blueprint,
                Created = created,
                Platform = platform,
                ContentType = contentType,
                Stage = stage,
                Message = message,
                Errors = errors,
                Warnings = warnings,
                Information = information,
                Targets = targets,
                VrcliVersion = DeploymentLog.Version,
                UnityVersion = Application.unityVersion,
                SdkVersion = VRC.Tools.SdkVersion,
                DurationMs = DeploymentLog.ElapsedMilliseconds,
                PhaseTimings = DeploymentLog.SnapshotTimings(),
                Artifact = outcome != null ? outcome.Artifact : null,
                PreviousVersion = outcome != null ? outcome.PreviousVersion : 0,
                ServerVersion = outcome != null ? outcome.ServerVersion : 0,
                ServerUpdatedAt = outcome != null ? outcome.ServerUpdatedAt : null
            };

            string directory = Path.GetDirectoryName(resultFile);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(resultFile, JsonUtility.ToJson(result, true));
        }
    }
}
